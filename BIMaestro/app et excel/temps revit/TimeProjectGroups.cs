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
    // Presentation aliases only. Original time rows and document paths stay intact.
    internal sealed class TimeProjectGroups
    {
        private readonly Dictionary<string, string> _groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string Resolve(string id) => _groups.TryGetValue(id, out var group) ? group : id;
        internal static TimeProjectGroups Load(string file)
        {
            var result = new TimeProjectGroups();
            if (!File.Exists(file)) return result;
            var root = XDocument.Load(file).Root;
            if (root == null || root.Name != "TimeProjectGroups") throw new InvalidDataException("Invalid project groups file.");
            foreach (var item in root.Elements("Document"))
            {
                string id = (string)item.Attribute("path"), group = (string)item.Attribute("group");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(group)) throw new InvalidDataException("Invalid project group.");
                result._groups.Add(id, group);
            }
            return result;
        }
        // Reload under a cross-process lock so two Revit versions cannot overwrite each other's edits.
        internal static string Update(string file, IEnumerable<string> selectedIds, bool separate)
        {
            string mutexName;
            using (var sha = SHA256.Create())
                mutexName = @"Local\BIMaestroTimeGroups-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, mutexName))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Project groups are busy in another Revit session.");
                    var data = Load(file);
                    var ids = new HashSet<string>(selectedIds.Select(data.Resolve), StringComparer.OrdinalIgnoreCase);
                    if (ids.Any(x => x.StartsWith("project-group:", StringComparison.Ordinal) && !data._groups.ContainsValue(x)))
                        throw new IOException("Project groups changed in another Revit session. Refresh the dashboard and retry.");
                    if (ids.Count == 0 || (!separate && ids.Count < 2)) return null;
                    var members = data._groups.Where(x => ids.Contains(x.Value)).Select(x => x.Key)
                        .Concat(ids.Where(x => !data._groups.ContainsValue(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    string group = separate ? null : "project-group:" + Guid.NewGuid().ToString("N");
                    foreach (string id in members)
                        if (separate) data._groups.Remove(id); else data._groups[id] = group;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        new XDocument(new XElement("TimeProjectGroups", data._groups.OrderBy(x => x.Key).Select(x =>
                            new XElement("Document", new XAttribute("path", x.Key), new XAttribute("group", x.Value))))).Save(temporary);
                        if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return group;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
    }
}
