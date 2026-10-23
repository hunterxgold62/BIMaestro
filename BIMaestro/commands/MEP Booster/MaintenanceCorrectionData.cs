using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BIMaestro.MepBooster
{
    // This format contains detached mechanism data only. No Revit identity,
    // human posture, floor, collision result or validation checkbox is stored.
    internal sealed class MaintenanceCorrectionFile
    {
        public int Version = 1;
        public List<MaintenanceCorrectionRecord> Corrections = new List<MaintenanceCorrectionRecord>();
    }

    internal sealed class MaintenanceCorrectionRecord
    {
        public int Version = 1;
        public string Signature, Geometry, Parameters, Connectors, Type, FamilyHint, Operation, SavedUtc;
        public double[] Command, X, Y, Z;
        public Dictionary<string, double> Fields = new Dictionary<string, double>();
        public bool Measured, Manufacturer, RotationAssumed;
        public string Notice;
        public List<string> Warnings = new List<string>();
    }

    internal static class MaintenanceCorrectionData
    {
        internal const int FileVersion = 1, MaximumRecords = 256, MaximumBytes = 2 * 1024 * 1024;
        internal static readonly string[] GestureFields = { "Radius", "Thickness", "Angle", "InitialAngle", "ChainDrop", "ChainSpacing", "Pull", "Grip", "Margin", "RX", "RY", "RZ", "Travel", "OffsetX", "OffsetY", "OffsetZ" };

        internal static string Digest(string value)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string FilePath()
        {
            string configured = Environment.GetEnvironmentVariable("BIMAESTRO_MAINTENANCE_CORRECTIONS_PATH");
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "Maintenance", "corrections-v1.json");
            if (!Path.IsPathRooted(configured)) throw new InvalidDataException("Le chemin de mémoire doit être absolu.");
            string path = Path.GetFullPath(configured);
            return Directory.Exists(path) || !Path.HasExtension(path) ? Path.Combine(path, "corrections-v1.json") : path;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Vector(double[] value) => value != null && value.Length == 3 && value.All(Finite) && value.All(v => Math.Abs(v) <= 1000000);
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        internal static bool Valid(MaintenanceCorrectionRecord record)
        {
            bool Text(string value, int max) => value != null && value.Length <= max;
            bool Hash(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
            if (record == null || record.Version != FileVersion || !Hash(record.Signature) || !Hash(record.Geometry)
                || !Hash(record.Parameters) || !Hash(record.Connectors) || !Text(record.Type, 500) || !Text(record.FamilyHint, 500)
                || !Text(record.Operation, 32) || !new[] { "LeverValve", "HandwheelValve", "ChainwheelValve", "Actuator", "Filter" }.Contains(record.Operation)
                || !Text(record.SavedUtc, 64) || !DateTime.TryParse(record.SavedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || !Text(record.Notice, 4000) || !Vector(record.Command) || !Vector(record.X) || !Vector(record.Y) || !Vector(record.Z)
                || record.Fields == null || record.Fields.Count != GestureFields.Length || GestureFields.Any(key => !record.Fields.ContainsKey(key))
                || record.Fields.Any(kv => !Finite(kv.Value) || Math.Abs(kv.Value) > 1000000)
                || record.Warnings == null || record.Warnings.Count > 32 || record.Warnings.Any(w => !Text(w, 2000))) return false;
            if (Math.Abs(Dot(record.X, record.X) - 1) > 1e-6 || Math.Abs(Dot(record.Y, record.Y) - 1) > 1e-6
                || Math.Abs(Dot(record.Z, record.Z) - 1) > 1e-6 || Math.Abs(Dot(record.X, record.Y)) > 1e-6
                || Math.Abs(Dot(record.X, record.Z)) > 1e-6 || Math.Abs(Dot(record.Y, record.Z)) > 1e-6) return false;
            // Values use Revit internal feet, except the two angles in degrees.
            foreach (string key in new[] { "Radius", "Thickness", "ChainDrop", "ChainSpacing", "Pull", "Grip", "RX", "RY", "RZ", "Travel" })
                if (record.Fields[key] < 2 / 304.8 || record.Fields[key] > 20000 / 304.8) return false;
            if (record.Fields["Margin"] < 0 || record.Fields["Margin"] > 1000 / 304.8) return false;
            if (Math.Abs(record.Fields["Angle"]) > 720) return false;
            return record.Signature == Signature(record.Geometry, record.Parameters, record.Connectors, record.Type);
        }

        internal static string Signature(string geometry, string parameters, string connectors, string type)
            // A free type name is a label, not mechanical identity. Revit ids
            // and project/family/type renaming cannot establish compatibility.
            => Digest("maintenance-anchor-v1\n" + geometry + "\n" + parameters + "\n" + connectors);

        internal static MaintenanceCorrectionFile Read(string path)
        {
            if (!File.Exists(path)) return new MaintenanceCorrectionFile();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length > MaximumBytes) throw new InvalidDataException("La mémoire dépasse la limite de lecture.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
                using (var json = new JsonTextReader(reader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                {
                    var serializer = JsonSerializer.Create(new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None,
                        MetadataPropertyHandling = MetadataPropertyHandling.Ignore, MaxDepth = 16, MissingMemberHandling = MissingMemberHandling.Error });
                    var root = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (root.Property("Version") == null || !(root["Corrections"] is JArray records))
                        throw new InvalidDataException("En-tête de mémoire absent.");
                    string[] required = typeof(MaintenanceCorrectionRecord).GetFields().Select(f => f.Name).ToArray();
                    if (records.Count > MaximumRecords || records.Any(token => !(token is JObject obj) || required.Any(name => obj.Property(name) == null)))
                        throw new InvalidDataException("Correction incomplète ou trop nombreuses corrections.");
                    var data = root.ToObject<MaintenanceCorrectionFile>(serializer);
                    if (json.Read()) throw new InvalidDataException("Contenu supplémentaire après la mémoire JSON.");
                    if (data == null || data.Version != FileVersion || data.Corrections == null || data.Corrections.Count > MaximumRecords
                        || data.Corrections.Any(r => !Valid(r))
                        || data.Corrections.GroupBy(r => r.Signature + ":" + r.Operation).Any(g => g.Count() > 1))
                        throw new InvalidDataException("Mémoire inconnue, incomplète ou invalide.");
                    return data;
                }
            }
        }

        // Serialise competing writers, including different Revit processes.
        internal static T WithLock<T>(string path, Func<T> action)
        {
            using (var mutex = new Mutex(false, "Local\\BIMaestroMaintenanceCorrections-" + Digest(Path.GetFullPath(path).ToUpperInvariant())))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(1500); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("La mémoire est occupée par un autre processus.");
                    return action();
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        internal static void Write(string path, MaintenanceCorrectionFile data)
        {
            if (data == null || data.Version != FileVersion || data.Corrections == null || data.Corrections.Count > MaximumRecords || data.Corrections.Any(r => !Valid(r))
                || data.Corrections.GroupBy(r => r.Signature + ":" + r.Operation).Any(g => g.Count() > 1))
                throw new InvalidDataException("Les corrections à enregistrer sont invalides.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(JsonConvert.SerializeObject(data, Formatting.Indented,
                new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 16 }));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("La mémoire dépasse la limite d’enregistrement.");
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".corrections-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        break;
                    }
                    catch (IOException) when (attempt < 2 && File.Exists(temporary))
                    { Thread.Sleep(20 * (attempt + 1)); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
