using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Analyse
{
    // Filesystem-only transport. It preserves native definitions as verified
    // bytes inside JSON/GZ; it does not claim to translate Revit's binary format.
    // Only materialized, locally owned scratch files have an .rvt extension.
    internal static class ElementHistoryDurableStore
    {
        internal const long MaxNativeBytes = 512L * 1024 * 1024;
        internal const int MaxRecordBytes = 32 * 1024 * 1024;
        private const long MaxNativeJsonBytes = ((MaxNativeBytes + 2) / 3) * 4 + 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex PayloadPattern = new Regex("\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex EventPattern = new Regex("\\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex ScratchPattern = new Regex("\\Asnapshot-payload-[0-9a-f]{64}-[0-9a-f]{32}\\.rvt\\z", RegexOptions.CultureInvariant);
        private static readonly object ScratchSync = new object();
        private static readonly HashSet<string> KnownScratch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ActiveScratch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object IndexSync = new object();
        private static string IndexedRoot;
        private static string[] IndexedJobs = Array.Empty<string>();
        private static long NextIndexScan;
        private sealed class NativeVerification
        {
            internal readonly object Sync = new object();
            internal bool Verified;
            internal long Length;
            internal DateTime LastWriteUtc;
        }
        private static readonly object VerificationSync = new object();
        private static readonly Dictionary<string, NativeVerification> VerifiedNative = new Dictionary<string, NativeVerification>(StringComparer.OrdinalIgnoreCase);

        private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error,
            Culture = CultureInfo.InvariantCulture, MaxDepth = 64,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        });

        private static string Root()
        {
            var directory = CollaborativeModelTrackerStore.ActiveDirectory;
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Le dossier d’historique durable est absent.");
            return Path.GetFullPath(Path.Combine(directory, "history-data"));
        }

        private static string PayloadId(string id)
        {
            if (id == null || !PayloadPattern.IsMatch(id)) throw new ArgumentException("Identifiant de contenu SHA256 invalide.", nameof(id));
            return id.ToLowerInvariant();
        }

        private static string EventId(string id)
        {
            if (id == null || !EventPattern.IsMatch(id)) throw new ArgumentException("Identifiant d’événement durable invalide.", nameof(id));
            return id;
        }

        private static string NativePath(string root, string id) => Path.Combine(root, "native", PayloadId(id) + ".json.gz");
        private static string RecordPath(string root, string kind, string id) => Path.Combine(root, kind == "job" ? "jobs" : "conversions", EventId(id) + ".json.gz");
        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data)); }
        private static void RemoveOwnTemporary(string path) { try { if (path != null) File.Delete(path); } catch { } }

        internal static string PublishNative(string scratchRvt)
        {
            if (string.IsNullOrWhiteSpace(scratchRvt) || !string.Equals(Path.GetExtension(scratchRvt), ".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Un fichier RVT temporaire est requis.", nameof(scratchRvt));
            var root = Root();
            using (var input = new FileStream(scratchRvt, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var length = input.Length;
                if (length <= 0 || length > MaxNativeBytes) throw new InvalidDataException("Taille du contenu natif hors limites.");
                string id;
                using (var sha = SHA256.Create()) id = Hex(sha.ComputeHash(input));
                var destination = NativePath(root, id);
                if (File.Exists(destination)) { ValidateNative(root, id); return id; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    input.Position = 0;
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using (var gzip = new GZipStream(file, CompressionLevel.Optimal, true))
                        using (var writer = new StreamWriter(gzip, Utf8, 65536, true))
                        using (var sha = SHA256.Create())
                        {
                            writer.Write("{\"Schema\":1,\"Kind\":\"native\",\"Sha256\":\""); writer.Write(id);
                            writer.Write("\",\"Length\":"); writer.Write(length.ToString(CultureInfo.InvariantCulture));
                            writer.Write(",\"NativeBase64\":\"");
                            var buffer = new byte[48 * 1024]; // divisible by three: no padding between blocks
                            long copied = 0;
                            while (true)
                            {
                                int count = 0, read;
                                while (count < buffer.Length && (read = input.Read(buffer, count, buffer.Length - count)) > 0) count += read;
                                if (count == 0) break;
                                copied += count;
                                if (copied > length) throw new IOException("Le RVT temporaire a changé pendant sa publication.");
                                sha.TransformBlock(buffer, 0, count, buffer, 0);
                                writer.Write(Convert.ToBase64String(buffer, 0, count));
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            if (copied != length || Hex(sha.Hash) != id) throw new IOException("Le RVT temporaire a changé pendant sa publication.");
                            writer.Write("\"}"); writer.Flush();
                        }
                        file.Flush(true);
                    }
                    ReadNative(temporary, id, Stream.Null); // complete verification before publication
                    try { File.Move(temporary, destination); }
                    catch (IOException) { if (!File.Exists(destination)) throw; }
                    ValidateNative(root, id); // never trust an existing/racing content-addressed file blindly
                    return id;
                }
                finally { RemoveOwnTemporary(temporary); }
            }
        }

        private static void Expect(TextReader reader, string expected)
        {
            foreach (var character in expected)
                if (reader.Read() != character) throw new InvalidDataException("Format du contenu natif durable inconnu ou corrompu.");
        }

        private static string ReadExact(TextReader reader, int count)
        {
            var buffer = new char[count]; int offset = 0, read;
            while (offset < count && (read = reader.Read(buffer, offset, count - offset)) > 0) offset += read;
            if (offset != count) throw new InvalidDataException("Contenu natif durable tronqué.");
            return new string(buffer);
        }

        // Canonical schema 1 is deliberately strict and streamed: a Json.NET
        // string token for a large Base64 value would allocate hundreds of MB.
        private static void ReadNative(string path, string expectedId, Stream destination)
        {
            expectedId = PayloadId(expectedId);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaxNativeJsonBytes + 1024 * 1024) throw new InvalidDataException("Capsule native trop volumineuse.");
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var bounded = new LimitedReadStream(gzip, MaxNativeJsonBytes))
                using (var reader = new StreamReader(bounded, Utf8, false, 65536))
                using (var sha = SHA256.Create())
                {
                    Expect(reader, "{\"Schema\":1,\"Kind\":\"native\",\"Sha256\":\"");
                    var declaredId = ReadExact(reader, 64);
                    if (declaredId != expectedId) throw new InvalidDataException("SHA256 de capsule différent de son identifiant.");
                    Expect(reader, "\",\"Length\":");
                    var number = new StringBuilder(); int character;
                    while ((character = reader.Read()) != ',')
                    {
                        if (character < '0' || character > '9' || number.Length >= 12) throw new InvalidDataException("Longueur native invalide.");
                        number.Append((char)character);
                    }
                    if (!long.TryParse(number.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                        || length <= 0 || length > MaxNativeBytes || number[0] == '0') throw new InvalidDataException("Longueur native hors limites.");
                    Expect(reader, "\"NativeBase64\":\"");
                    long decoded = 0, encoded = 0;
                    var expectedCharacters = ((length + 2) / 3) * 4;
                    var chars = new char[65536 + 4]; int carried = 0;
                    bool ended = false, paddingSeen = false;
                    while (!ended)
                    {
                        int read = reader.Read(chars, carried, 65536);
                        if (read == 0) throw new InvalidDataException("Base64 natif tronqué.");
                        int total = carried + read;
                        int quote = Array.IndexOf(chars, '"', 0, total);
                        int content = quote < 0 ? total : quote;
                        if (paddingSeen && content != 0) throw new InvalidDataException("Base64 après son remplissage final.");
                        int usable = quote < 0 ? content - content % 4 : content;
                        if (usable % 4 != 0) throw new InvalidDataException("Longueur Base64 native invalide.");
                        if (usable > 0)
                        {
                            encoded += usable;
                            if (encoded > expectedCharacters) throw new InvalidDataException("Base64 natif dépasse sa longueur déclarée.");
                            // JSON base64 is canonical: whitespace is never accepted.
                            for (int i = 0; i < usable; i++)
                                if (char.IsWhiteSpace(chars[i])) throw new InvalidDataException("Espacement Base64 natif invalide.");
                            byte[] bytes;
                            try { bytes = Convert.FromBase64CharArray(chars, 0, usable); }
                            catch (FormatException ex) { throw new InvalidDataException("Base64 natif invalide.", ex); }
                            paddingSeen = chars[usable - 1] == '=';
                            decoded += bytes.Length;
                            if (decoded > length) throw new InvalidDataException("Contenu natif dépasse sa longueur déclarée.");
                            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                            destination.Write(bytes, 0, bytes.Length);
                        }
                        if (quote >= 0)
                        {
                            int tail = total - quote - 1;
                            if (tail > 1 || (tail == 1 ? chars[quote + 1] : reader.Read()) != '}' || reader.Read() != -1)
                                throw new InvalidDataException("Données après le contenu natif durable.");
                            ended = true;
                        }
                        else
                        {
                            carried = total - usable;
                            if (carried > 0) Array.Copy(chars, usable, chars, 0, carried);
                        }
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (decoded != length || encoded != expectedCharacters || Hex(sha.Hash) != expectedId)
                        throw new InvalidDataException("Intégrité SHA256/longueur du contenu natif non vérifiée.");
                }
            }
        }

        private static NativeVerification VerificationFor(string path)
        {
            lock (VerificationSync)
            {
                if (!VerifiedNative.TryGetValue(path, out var entry)) VerifiedNative[path] = entry = new NativeVerification();
                return entry;
            }
        }

        // The API publishes immutable capsules. Conversion readers recheck cheap
        // metadata, rather than hashing the same MB payload for every event.
        // This is not an anti-tamper promise against an external actor restoring
        // identical timestamps: materialization ALWAYS verifies the full bytes.
        private static void VerifyNativeFile(string path, string id, Stream output, bool allowMemo)
        {
            var entry = VerificationFor(path);
            lock (entry.Sync)
            {
                try
                {
                    var before = new FileInfo(path); before.Refresh();
                    if (!before.Exists) throw new FileNotFoundException("Capsule native durable absente.", path);
                    var length = before.Length; var written = before.LastWriteTimeUtc;
                    if (allowMemo && entry.Verified && entry.Length == length && entry.LastWriteUtc == written) return;
                    entry.Verified = false;
                    ReadNative(path, id, output);
                    var after = new FileInfo(path); after.Refresh();
                    if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
                        throw new IOException("La capsule native a changé pendant sa vérification.");
                    entry.Length = length; entry.LastWriteUtc = written; entry.Verified = true;
                }
                catch { entry.Verified = false; throw; }
            }
        }

        private static void ValidateNative(string root, string id) => VerifyNativeFile(NativePath(root, id), id, Stream.Null, false);
        private static void ValidateNativeReference(string root, string id) => VerifyNativeFile(NativePath(root, id), id, Stream.Null, true);

        private static string ScratchRoot() => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BIMaestro", "history-native"));

        private static void RequirePlainDirectory(string directory)
        {
            // A redirected scratch namespace must never authorize deleting user files.
            var current = new DirectoryInfo(directory);
            for (int count = 0; current != null && count < 3; count++, current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Le dossier temporaire natif est redirigé ; opération refusée.");
        }

        internal static string MaterializeNative(string payloadId)
        {
            var id = PayloadId(payloadId); var root = Root(); var directory = ScratchRoot();
            RequirePlainDirectory(directory); Directory.CreateDirectory(directory); RequirePlainDirectory(directory);
            var path = Path.Combine(directory, "snapshot-payload-" + id + "-" + Guid.NewGuid().ToString("N") + ".rvt");
            var temporary = path + ".partial";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { VerifyNativeFile(NativePath(root, id), id, output, false); output.Flush(true); }
                File.Move(temporary, path);
                lock (ScratchSync) { KnownScratch.Add(path); ActiveScratch.Add(path); }
                return path;
            }
            finally { RemoveOwnTemporary(temporary); }
        }

        internal static void ReleaseMaterialized(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Chemin temporaire absent.", nameof(path));
            var full = Path.GetFullPath(path); var directory = ScratchRoot();
            if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) || !ScratchPattern.IsMatch(Path.GetFileName(full)))
                throw new ArgumentException("Ce fichier n’appartient pas au dossier temporaire natif.", nameof(path));
            RequirePlainDirectory(directory);
            lock (ScratchSync)
            {
                if (!KnownScratch.Contains(full)) throw new InvalidOperationException("Ce fichier temporaire n’a pas été créé par ce stockage.");
                if (!ActiveScratch.Contains(full))
                {
                    if (File.Exists(full)) throw new IOException("Le fichier temporaire déjà libéré a été remplacé.");
                    return;
                }
                if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Le fichier temporaire natif a été redirigé.");
                File.Delete(full); ActiveScratch.Remove(full);
            }
        }

        private static JToken Canonical(JToken token)
        {
            if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))));
            if (token is JArray array) return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }

        private static byte[] DataBytes(JToken data)
        {
            var bytes = Utf8.GetBytes(Canonical(data).ToString(Formatting.None));
            if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("Enregistrement durable trop volumineux.");
            return bytes;
        }

        private static JObject ReadRecord(string path, string kind, string id)
        {
            FileStream file;
            try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            using (file)
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var bounded = new LimitedReadStream(gzip, MaxRecordBytes + 65536L))
            using (var text = new StreamReader(bounded, Utf8, false, 65536))
            using (var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
            {
                var envelope = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read() || envelope.Properties().Count() != 6 || envelope["Schema"]?.Type != JTokenType.Integer
                    || envelope["Length"]?.Type != JTokenType.Integer || (int?)envelope["Schema"] != 1
                    || (string)envelope["Kind"] != kind || (string)envelope["Id"] != id || !(envelope["Data"] is JObject data))
                    throw new InvalidDataException("Format d’enregistrement durable inconnu.");
                var bytes = DataBytes(data);
                if ((long?)envelope["Length"] != bytes.LongLength || (string)envelope["Sha256"] != Hash(bytes))
                    throw new InvalidDataException("Intégrité de l’enregistrement durable non vérifiée.");
                return data;
            }
        }

        private static void WriteRecord(string root, string kind, string id, JObject data, bool job)
        {
            var path = RecordPath(root, kind, id);
            var existing = ReadRecord(path, kind, id);
            if (existing != null) { RequireSameRecord(existing, data, job); return; }
            var bytes = DataBytes(data);
            var envelope = new JObject { ["Schema"] = 1, ["Kind"] = kind, ["Id"] = id,
                ["Length"] = bytes.LongLength, ["Sha256"] = Hash(bytes), ["Data"] = Canonical(data) };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var gzip = new GZipStream(file, CompressionLevel.Optimal, true))
                    using (var writer = new StreamWriter(gzip, Utf8, 65536, true))
                    using (var json = new JsonTextWriter(writer)) { envelope.WriteTo(json); json.Flush(); writer.Flush(); }
                    file.Flush(true);
                }
                if (ReadRecord(temporary, kind, id) == null) throw new IOException("Publication durable temporaire absente.");
                try { File.Move(temporary, path); }
                catch (IOException) { if (!File.Exists(path)) throw; }
                var published = ReadRecord(path, kind, id);
                if (published == null) throw new IOException("Publication durable absente après écriture.");
                RequireSameRecord(published, data, job);
            }
            finally { RemoveOwnTemporary(temporary); }
        }

        private static void RequireSameRecord(JObject existing, JObject requested, bool job)
        {
            var first = job ? StableJob(existing) : existing;
            var second = job ? StableJob(requested) : requested;
            if (!DataBytes(first).SequenceEqual(DataBytes(second)))
                throw new InvalidDataException("Conflit d’identité : un enregistrement durable immuable existe déjà.");
        }

        private static JObject StableJob(JObject data)
        {
            var copy = (JObject)data.DeepClone();
            foreach (var property in copy.Descendants().OfType<JProperty>().Where(p => p.Name == "Native" || p.Name == "OpeningFallback").ToList())
                if (property.Value is JObject artifact) { artifact.Remove("Ready"); artifact.Remove("ReadyUtc"); }
            return copy;
        }

        private static HistoryRestoreRequest ValidateConversion(string root, JObject data)
        {
            var request = data.ToObject<HistoryRestoreRequest>(Serializer());
            if (request == null || (request.Recipe == null && string.IsNullOrWhiteSpace(request.CaptureFailure)))
                throw new InvalidDataException("Conversion sans recette ni diagnostic explicite.");
            var checkedPayloads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in data.Descendants().OfType<JProperty>().Where(p => p.Name == "Recipe"))
                if (property.Value is JObject recipe && (recipe["Version"]?.Type != JTokenType.Integer || (int?)recipe["Version"] != 1))
                    throw new InvalidDataException("Version de recette durable inconnue.");
            foreach (var property in data.Descendants().OfType<JProperty>().Where(p => p.Name == "Native" || p.Name == "OpeningFallback"))
            {
                if (property.Value.Type == JTokenType.Null) continue;
                if (!(property.Value is JObject artifact)) throw new InvalidDataException("Référence native durable invalide.");
                if (artifact["WholeModel"] != null && artifact["WholeModel"].Type != JTokenType.Boolean)
                    throw new InvalidDataException("Indicateur de copie complète invalide.");
                if ((bool?)artifact["WholeModel"] == true) throw new InvalidDataException("Une conversion durable ne peut dépendre d’une copie complète.");
                if (!string.IsNullOrWhiteSpace((string)artifact["File"])) throw new InvalidDataException("Une conversion durable conserve un chemin de RVT externe.");
                var id = PayloadId((string)artifact["PayloadId"]);
                if (checkedPayloads.Add(id)) ValidateNativeReference(root, id);
            }
            return request;
        }

        internal static void PublishConversion(string eventId, HistoryRestoreRequest request)
        {
            EventId(eventId);
            if (request == null) throw new ArgumentNullException(nameof(request));
            var root = Root(); var data = JObject.FromObject(request, Serializer());
            ValidateConversion(root, data);
            WriteRecord(root, "conversion", eventId, data, false);
            var published = ReadRecord(RecordPath(root, "conversion", eventId), "conversion", eventId);
            if (published == null) throw new IOException("Conversion absente après publication.");
            ValidateConversion(root, published);
        }

        internal static HistoryRestoreRequest ResolveConversion(string eventId)
        {
            var id = EventId(eventId); var root = Root();
            var data = ReadRecord(RecordPath(root, "conversion", id), "conversion", id);
            return data == null ? null : ValidateConversion(root, data);
        }

        internal static bool HasConversion(string eventId) => ResolveConversion(eventId) != null;

        internal static void WriteJob(ElementHistoryEvent evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            var id = EventId(evt.EventId);
            if (!string.Equals(evt.Action, "delete", StringComparison.Ordinal)) throw new InvalidDataException("Seules les suppressions créent un travail durable.");
            var root = Root(); var data = JObject.FromObject(evt, Serializer());
            WriteRecord(root, "job", id, data, true); InvalidateJobIndex(root);
        }

        private static void InvalidateJobIndex(string root)
        { lock (IndexSync) { if (IndexedRoot == root) NextIndexScan = 0; } }

        private static string[] JobPaths(string root)
        {
            lock (IndexSync)
            {
                var now = Stopwatch.GetTimestamp();
                if (IndexedRoot == root && now < NextIndexScan) return IndexedJobs;
                var directory = Path.Combine(root, "jobs");
                string[] paths;
                try { paths = Directory.GetFiles(directory, "*.json.gz"); }
                catch (DirectoryNotFoundException) { paths = Array.Empty<string>(); }
                foreach (var path in paths) EventId(Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - ".json.gz".Length));
                Array.Sort(paths, StringComparer.Ordinal);
                IndexedRoot = root; IndexedJobs = paths; NextIndexScan = now + 2 * Stopwatch.Frequency;
                return paths;
            }
        }

        internal static IEnumerable<ElementHistoryEvent> ReadPendingJobs(Action<string, Exception> onFailure = null)
        {
            var root = Root();
            foreach (var path in JobPaths(root))
            {
                var name = Path.GetFileName(path); var id = EventId(name.Substring(0, name.Length - ".json.gz".Length));
                ElementHistoryEvent evt;
                try
                {
                    var data = ReadRecord(path, "job", id);
                    if (data == null) continue; // another worker may have acknowledged this immutable job
                    evt = data.ToObject<ElementHistoryEvent>(Serializer());
                    if (evt == null || evt.EventId != id || evt.Action != "delete") throw new InvalidDataException("Identité du travail durable invalide.");
                }
                catch (Exception ex)
                {
                    if (onFailure == null) throw;
                    onFailure(id, ex);
                    continue; // preserve the damaged file; never treat it as a valid job
                }
                yield return evt; // deserialize only requested items, never all jobs on every Idling
            }
        }

        internal static void DeleteJob(string eventId)
        {
            var id = EventId(eventId); var root = Root();
            var conversion = ReadRecord(RecordPath(root, "conversion", id), "conversion", id);
            if (conversion == null) throw new InvalidOperationException("Le travail ne peut être retiré avant sa conversion durable.");
            ValidateConversion(root, conversion);
            File.Delete(RecordPath(root, "job", id)); InvalidateJobIndex(root);
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream Inner; private readonly long Limit; private long ReadBytes;
            internal LimitedReadStream(Stream inner, long limit) { Inner = inner; Limit = limit; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Inner.Read(buffer, offset, count); ReadBytes += read;
                if (ReadBytes > Limit) throw new InvalidDataException("Décompression durable au-delà de la limite.");
                return read;
            }
            public override int ReadByte() { var value = Inner.ReadByte(); if (value >= 0 && ++ReadBytes > Limit) throw new InvalidDataException("Décompression durable au-delà de la limite."); return value; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
