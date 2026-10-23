using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Analyse
{
    // The local, flushed batch is the authority until the shared journal AND its
    // outbox have acknowledged it. No network call is made by Stage.
    internal sealed class ElementHistoryLocalSpool
    {
        internal static readonly ElementHistoryLocalSpool Default = new ElementHistoryLocalSpool(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "HistorySpool"));
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex FileName = new Regex(@"\A[0-9]{19}-[0-9a-f]{64}\.pending\.json\z", RegexOptions.CultureInvariant);
        private readonly string DirectoryPath;
        private readonly object Sync = new object();
        private readonly object TransportSync = new object();
        private sealed class Batch
        {
            public int Version { get; set; } = 1;
            public string Destination { get; set; }
            public string Day { get; set; }
            public int Count { get; set; }
            public string Sha256 { get; set; }
            public string Payload { get; set; }
        }

        internal ElementHistoryLocalSpool(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Dossier de journal local absent.", nameof(directory));
            DirectoryPath = Path.GetFullPath(directory);
        }

        private static string FullDestination(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new IOException("Dossier de destination historique absent.");
            return Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        private static string Name(Batch batch, long ticks)
            => ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Hash(batch.Destination.ToUpperInvariant() + "\n" + batch.Day + "\n" + batch.Payload) + ".pending.json";

        internal void Stage(string destination, IEnumerable<ElementHistoryEvent> events)
        {
            destination = FullDestination(destination);
            var pending = (events ?? throw new ArgumentNullException(nameof(events))).ToList();
            if (pending.Count == 0) return;
            if (pending.Any(e => e == null || string.IsNullOrWhiteSpace(e.EventId) || e.Ts == default))
                throw new InvalidDataException("Un événement local doit posséder une identité et une date.");
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                foreach (var group in pending.GroupBy(e => e.Ts.ToUniversalTime().Date))
                {
                    var items = group.ToList();
                    var payload = string.Concat(items.Select(e => JsonConvert.SerializeObject(e, Formatting.None) + "\n"));
                    var batch = new Batch { Destination = destination, Day = group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Count = items.Count, Sha256 = Hash(payload), Payload = payload };
                    var name = Name(batch, items[0].Ts.ToUniversalTime().Ticks);
                    var path = Path.Combine(DirectoryPath, name);
                    if (File.Exists(path)) { Read(path); continue; }
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(batch, Formatting.None));
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                        Read(temporary, name); // Verify every byte before queue removal/publication.
                        try { File.Move(temporary, path); }
                        catch (IOException) { if (!File.Exists(path)) throw; }
                        Read(path);
                    }
                    finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
                }
            }
        }

        private static Batch Read(string path, string expectedName = null)
        {
            JObject data;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var text = new StreamReader(input, Utf8, false))
            using (var reader = new JsonTextReader(text) { MaxDepth = 128, DateParseHandling = DateParseHandling.None })
            {
                data = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Données supplémentaires dans un lot historique local.");
            }
            var batch = data.ToObject<Batch>(JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, TypeNameHandling = TypeNameHandling.None }));
            if (batch?.Version != 1 || batch.Count < 1 || batch.Count > 10000 || batch.Payload == null
                || Hash(batch.Payload) != batch.Sha256 || batch.Destination != FullDestination(batch.Destination)
                || !DateTime.TryParseExact(batch.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new InvalidDataException("Intégrité du lot historique local non vérifiée.");
            var events = Events(batch);
            if (events.Count != batch.Count || events.Any(e => e == null || string.IsNullOrWhiteSpace(e.EventId)
                || e.Ts == default || e.Ts.ToUniversalTime().Date != day.Date))
                throw new InvalidDataException("Identités ou dates du lot historique local invalides.");
            var name = expectedName ?? Path.GetFileName(path);
            if (!FileName.IsMatch(name) || name != Name(batch, events[0].Ts.ToUniversalTime().Ticks))
                throw new InvalidDataException("Identité du lot historique local incohérente.");
            return batch;
        }

        private static List<ElementHistoryEvent> Events(Batch batch)
            => batch.Payload.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonConvert.DeserializeObject<ElementHistoryEvent>(line)).ToList();

        internal bool HasPending(string destination)
        {
            destination = FullDestination(destination);
            if (!Directory.Exists(DirectoryPath)) return false;
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.pending.json"))
                if (string.Equals(Read(path).Destination, destination, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Shared by append and monthly compaction, including different Revit
        // processes/computers using the same store. Local staging never takes it.
        // Only IO workers may call this: an unavailable share is allowed to fail.
        internal void WithTransportLock(string destination, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            destination = FullDestination(destination);
            lock (TransportSync)
                using (var lease = AcquireTransportLease(destination)) action();
        }

        // Iterator ownership keeps the lease through enumeration and releases
        // it on early exit/failure. Journals remain streamed, with no full-store
        // snapshot allocation. This is restricted to the existing IO workers.
        internal IEnumerable<T> ReadWithTransportLock<T>(string destination, Func<IEnumerable<T>> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            destination = FullDestination(destination);
            lock (TransportSync)
                using (var lease = AcquireTransportLease(destination))
                    foreach (var item in read()) yield return item;
        }

        private static FileStream AcquireTransportLease(string destination)
        {
            var folder = Path.Combine(destination, "history-data"); Directory.CreateDirectory(folder);
            var leasePath = Path.Combine(folder, "journal-transport.lease");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (timeout.ElapsedMilliseconds >= 2000) throw; Thread.Sleep(25); }
            }
        }

        // Called only by the IO worker/explicit durability flush, never while
        // collecting Revit objects. The delegate persists the conversion outbox.
        internal int Drain(string destination, Action<IEnumerable<ElementHistoryEvent>> persisted)
        {
            if (persisted == null) throw new ArgumentNullException(nameof(persisted));
            destination = FullDestination(destination);
            if (!Directory.Exists(DirectoryPath)) return 0;
            int delivered = 0;
            lock (TransportSync)
            {
                foreach (var path in Directory.GetFiles(DirectoryPath, "*.pending.json").OrderBy(p => p, StringComparer.Ordinal))
                {
                    FileStream lease;
                    try { lease = new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { continue; } // Another Revit/process owns this batch.
                    using (lease)
                    {
                        if (!File.Exists(path)) continue;
                        var batch = Read(path);
                        if (!string.Equals(batch.Destination, destination, StringComparison.OrdinalIgnoreCase)) continue;
                        var attempt = path + ".attempted";
                        bool retry = File.Exists(attempt);
                        if (!retry)
                            using (var marker = new FileStream(attempt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            { var bytes = Utf8.GetBytes(batch.Sha256); marker.Write(bytes, 0, bytes.Length); marker.Flush(true); }
                        WithTransportLock(destination, () =>
                        {
                            AppendVerified(batch, retry);
                            persisted(Events(batch));
                            // Stage still verifies the newly published local file
                            // before its caller removes the RAM queue head. Wait
                            // only for that local section, never hold it on IO.
                            lock (Sync)
                            {
                                File.Delete(path); // Shared journal and outbox are acknowledged.
                                File.Delete(attempt);
                            }
                        });
                        delivered += batch.Count;
                    }
                    try { File.Delete(path + ".lease"); } catch { }
                }
            }
            return delivered;
        }

        private static void AppendVerified(Batch batch, bool retry)
        {
            Directory.CreateDirectory(batch.Destination);
            var path = Path.Combine(batch.Destination, "element-history-" + batch.Day + ".jsonl");
            using (var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                RepairIncompleteTail(output, batch.Destination);
                var lines = batch.Payload.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (retry && output.Length > 0)
                {
                    var expected = lines.ToDictionary(line => (string)JObject.Parse(line)["EventId"], JObject.Parse, StringComparer.Ordinal);
                    output.Position = 0;
                    using (var reader = new StreamReader(output, Utf8, false, 65536, true))
                        RemoveDelivered(reader, expected);
                    RemoveArchived(batch, expected);
                    lines = lines.Where(line => expected.ContainsKey((string)JObject.Parse(line)["EventId"])).ToArray();
                }
                else if (retry)
                {
                    var expected = lines.ToDictionary(line => (string)JObject.Parse(line)["EventId"], JObject.Parse, StringComparer.Ordinal);
                    RemoveArchived(batch, expected);
                    lines = lines.Where(line => expected.ContainsKey((string)JObject.Parse(line)["EventId"])).ToArray();
                }
                if (lines.Length == 0) return;
                var originalLength = output.Length;
                try
                {
                    string prefix = "";
                    if (originalLength > 0)
                    { output.Position = originalLength - 1; var last = output.ReadByte(); if (last != '\n' && last != '\r') prefix = "\n"; }
                    var bytes = Utf8.GetBytes(prefix + string.Join("\n", lines) + "\n");
                    output.Position = originalLength;
                    output.Write(bytes, 0, bytes.Length); output.Flush(true);
                    output.Position = originalLength;
                    var verification = new byte[65536]; int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = output.Read(verification, 0, Math.Min(verification.Length, bytes.Length - offset));
                        if (read <= 0) throw new IOException("Relecture du journal partagé tronquée.");
                        for (int index = 0; index < read; index++)
                            if (verification[index] != bytes[offset + index]) throw new IOException("Écriture du journal partagé non vérifiée.");
                        offset += read;
                    }
                }
                catch
                {
                    try { output.SetLength(originalLength); output.Flush(true); } catch { }
                    throw;
                }
            }
        }

        private static void RemoveDelivered(TextReader reader, Dictionary<string, JObject> expected)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                JObject found;
                try { found = JObject.Parse(line); } catch (JsonException) { continue; }
                var id = (string)found["EventId"];
                if (id != null && expected.TryGetValue(id, out var wanted))
                {
                    if (!JToken.DeepEquals(found, wanted)) throw new InvalidDataException("Conflit d’identité dans le journal partagé.");
                    expected.Remove(id);
                }
            }
        }

        private static void RemoveArchived(Batch batch, Dictionary<string, JObject> expected)
        {
            // Another process may have compacted a successfully appended batch
            // after its owner crashed before local acknowledgement.
            foreach (var name in new[] { "element-history-" + batch.Day + ".jsonl.gz", "element-history-" + batch.Day.Substring(0, 7) + ".jsonl.gz" })
            {
                var path = Path.Combine(batch.Destination, name);
                if (!File.Exists(path)) continue;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Utf8, false, 65536)) RemoveDelivered(reader, expected);
            }
        }

        // A process/power failure cannot execute append rollback. Preserve an
        // incomplete final JSON line as evidence before removing that fragment;
        // complete events are never removed. The durable local batch replays it.
        private static void RepairIncompleteTail(FileStream journal, string destination)
        {
            if (journal.Length == 0) return;
            journal.Position = journal.Length - 1;
            int last = journal.ReadByte();
            if (last == '\n' || last == '\r') return;
            long boundary = 0, cursor = journal.Length;
            var buffer = new byte[65536];
            bool found = false;
            while (cursor > 0 && !found)
            {
                int count = (int)Math.Min(buffer.Length, cursor); cursor -= count;
                journal.Position = cursor;
                int offset = 0;
                while (offset < count)
                { int read = journal.Read(buffer, offset, count - offset); if (read <= 0) throw new IOException("Fin de journal illisible."); offset += read; }
                for (int index = count - 1; index >= 0; index--)
                    if (buffer[index] == '\n' || buffer[index] == '\r') { boundary = cursor + index + 1; found = true; break; }
            }
            if (journal.Length - boundary > int.MaxValue) throw new InvalidDataException("Fragment final du journal trop volumineux pour une récupération automatique.");
            var fragment = new byte[(int)(journal.Length - boundary)]; journal.Position = boundary;
            int copied = 0;
            while (copied < fragment.Length)
            { int read = journal.Read(fragment, copied, fragment.Length - copied); if (read <= 0) throw new IOException("Fragment final du journal tronqué."); copied += read; }
            try { JObject.Parse(Utf8.GetString(fragment)); return; }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { }
            string id;
            using (var sha = SHA256.Create()) id = BitConverter.ToString(sha.ComputeHash(fragment)).Replace("-", "").ToLowerInvariant();
            var folder = Path.Combine(destination, "history-data", "journal-recovery"); Directory.CreateDirectory(folder);
            var evidence = Path.Combine(folder, id + ".fragment");
            if (!File.Exists(evidence))
            {
                var temporary = evidence + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { output.Write(fragment, 0, fragment.Length); output.Flush(true); }
                    try { File.Move(temporary, evidence); } catch (IOException) { if (!File.Exists(evidence)) throw; }
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            }
            if (!File.ReadAllBytes(evidence).SequenceEqual(fragment)) throw new InvalidDataException("Le fragment historique n’a pas été conservé intégralement.");
            journal.SetLength(boundary); journal.Flush(true);
        }
    }
}
