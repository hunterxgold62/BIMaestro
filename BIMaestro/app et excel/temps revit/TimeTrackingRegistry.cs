using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace BIMaestro.Dashboard
{
    // Identity comes from the model. Decisions belong to (identity, path), never just its name.
    // Durations stay in the original Excel rows/snapshots; resolving an alias never copies time.
    internal sealed class TimeTrackingRegistry
    {
        internal sealed class Location
        {
            internal string Identity, Path, Name, Project;
            internal bool Pending => string.IsNullOrEmpty(Project);
            internal string PendingId => "time-pending:" + Hash(Identity + "|" + CanonicalPath(Path));
        }
        private readonly List<Location> _locations = new List<Location>();
        private readonly Dictionary<string, string> _identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string Identity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_identities.TryGetValue(value, out string next))
            {
                if (!visited.Add(value)) throw new InvalidDataException("Cyclic time tracking identity alias.");
                value = next;
            }
            return value;
        }
        internal static string CanonicalPath(string value) => (value ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }
        internal Location Find(string identity, string path) => _locations.FirstOrDefault(x =>
            string.Equals(Identity(x.Identity), Identity(identity), StringComparison.OrdinalIgnoreCase) && CanonicalPath(x.Path) == CanonicalPath(path));
        internal List<Location> Candidates(Location location) => _locations.Where(x => !x.Pending &&
            string.Equals(Identity(x.Identity), Identity(location.Identity), StringComparison.OrdinalIgnoreCase) && CanonicalPath(x.Path) != CanonicalPath(location.Path))
            .GroupBy(x => x.Project, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        internal string Resolve(string identity, string path)
        {
            var location = !string.IsNullOrEmpty(identity) ? Find(identity, path) : null;
            // Legacy rows had no identity. Only reuse an unambiguous path assignment.
            if (location == null && string.IsNullOrEmpty(identity))
            {
                var matches = _locations.Where(x => CanonicalPath(x.Path) == CanonicalPath(path)).ToList();
                if (matches.Select(x => x.Project ?? x.PendingId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) location = matches.FirstOrDefault();
            }
            return location == null ? path : location.Project ?? location.PendingId;
        }
        internal bool IsPending(string identity, string path) => Resolve(identity, path).StartsWith("time-pending:", StringComparison.Ordinal);
        internal static TimeTrackingRegistry Load(string file)
        {
            var result = new TimeTrackingRegistry();
            if (!File.Exists(file)) return result;
            var root = XDocument.Load(file).Root;
            if (root == null || root.Name != "TimeTracking" || (string)root.Attribute("version") != "1") throw new InvalidDataException("Invalid time tracking registry.");
            foreach (var alias in root.Elements("Identity"))
            {
                string from = (string)alias.Attribute("from"), to = (string)alias.Attribute("to");
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new InvalidDataException("Invalid time tracking identity alias.");
                result._identities.Add(from, to);
            }
            foreach (string alias in result._identities.Keys) result.Identity(alias);
            foreach (var element in root.Elements("Location"))
            {
                var item = new Location { Identity = (string)element.Attribute("identity"), Path = (string)element.Attribute("path"),
                    Name = (string)element.Attribute("name"), Project = (string)element.Attribute("project") };
                if (string.IsNullOrWhiteSpace(item.Identity) || string.IsNullOrWhiteSpace(item.Path) || result.Find(item.Identity, item.Path) != null)
                    throw new InvalidDataException("Invalid or duplicate time tracking location.");
                result._locations.Add(item);
            }
            return result;
        }
        internal static Location Observe(string file, string identity, string path, string name, string olderIdentity = null)
        {
            if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(path)) return null;
            return Edit(file, data =>
            {
                bool aliasesChanged = false;
                if (!string.IsNullOrWhiteSpace(olderIdentity) && !string.Equals(identity, olderIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    string current = data.Identity(identity), older = data.Identity(olderIdentity);
                    bool currentKnown = data._locations.Any(x => string.Equals(data.Identity(x.Identity), current, StringComparison.OrdinalIgnoreCase));
                    bool olderKnown = data._locations.Any(x => string.Equals(data.Identity(x.Identity), older, StringComparison.OrdinalIgnoreCase));
                    bool alreadyHasNative = data._identities.Keys.Any(x => x.StartsWith("creation:", StringComparison.Ordinal)
                        && string.Equals(data.Identity(x), older, StringComparison.OrdinalIgnoreCase));
                    // Upgrade a pre-2024 lineage once. Two different native creation GUIDs must
                    // never be conflated merely because their template shared a project-info element.
                    if (!currentKnown && olderKnown && older.StartsWith("element:", StringComparison.Ordinal) && !alreadyHasNative)
                    {
                        data._identities[identity] = older;
                        aliasesChanged = true;
                    }
                    else if (current.StartsWith("creation:", StringComparison.Ordinal) && !olderKnown
                        && !string.Equals(older, current, StringComparison.OrdinalIgnoreCase))
                    {
                        data._identities[olderIdentity] = current;
                        aliasesChanged = true;
                    }
                }
                var item = data.Find(identity, path);
                if (item != null) return Tuple.Create(item, aliasesChanged);
                string canonical = data.Identity(identity);
                item = new Location { Identity = canonical, Path = path, Name = name };
                bool knownIdentity = data._locations.Any(x => string.Equals(data.Identity(x.Identity), canonical, StringComparison.OrdinalIgnoreCase));
                // A first encounter adopts the historical path, so old rows still contribute.
                if (!knownIdentity) item.Project = data._locations.Any(x => CanonicalPath(x.Path) == CanonicalPath(path))
                    ? "time-project:" + Guid.NewGuid().ToString("N") : path;
                data._locations.Add(item);
                return Tuple.Create(item, true);
            });
        }
        internal static bool Decide(string file, string identity, string path, string existingProject, bool startNew)
        {
            return Edit(file, data =>
            {
                var item = data.Find(identity, path);
                if (item == null) throw new InvalidDataException("Time tracking location is missing.");
                // First committed decision wins, even if another Revit window is still showing the question.
                if (!item.Pending) return Tuple.Create(false, false);
                if (!startNew && !data.Candidates(item).Any(x => string.Equals(x.Project, existingProject, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The selected project is no longer available.");
                item.Project = startNew ? "time-project:" + Guid.NewGuid().ToString("N") : existingProject;
                return Tuple.Create(true, true);
            });
        }
        internal static void SeparateProjects(string file, IEnumerable<string> projectIds)
        {
            var selected = new HashSet<string>(projectIds, StringComparer.OrdinalIgnoreCase);
            Edit(file, data =>
            {
                bool changed = false;
                foreach (var project in data._locations.Where(x => !x.Pending && selected.Contains(x.Project))
                    .GroupBy(x => x.Project, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    if (project.Count() < 2) continue;
                    foreach (var location in project) location.Project = "time-project:" + Guid.NewGuid().ToString("N");
                    changed = true;
                }
                return Tuple.Create(changed, changed);
            });
        }
        private static T Edit<T>(string file, Func<TimeTrackingRegistry, Tuple<T, bool>> change)
        {
            using (var mutex = new Mutex(false, @"Local\BIMaestroTimeTracking-" + Hash(CanonicalPath(System.IO.Path.GetFullPath(file)))))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Time tracking is busy in another Revit session.");
                    var data = Load(file);
                    var result = change(data);
                    if (result.Item2) data.Save(file);
                    return result.Item1;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        private void Save(string file)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                new XDocument(new XElement("TimeTracking", new XAttribute("version", "1"), _identities.Select(x => new XElement("Identity", new XAttribute("from", x.Key), new XAttribute("to", x.Value))), _locations.Select(x =>
                {
                    var element = new XElement("Location", new XAttribute("identity", x.Identity), new XAttribute("path", x.Path), new XAttribute("name", x.Name ?? ""));
                    if (!x.Pending) element.Add(new XAttribute("project", x.Project));
                    return element;
                }))).Save(temporary);
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
