using System;
using System.IO;
using System.Linq;
using BIMaestro.Dashboard;

class Program
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Main()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "BIMaestro-TimeGroups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "groups.xml");
        string oldFile = @"C:\2024\École & bâtiment.rvt", newFile = @"C:\2026\École convertie.rvt", other = @"C:\Autre\École.rvt";
        try
        {
            Check(TimeProjectGroups.Load(file).Resolve(oldFile) == oldFile, "No implicit merging of paths");
            string group = TimeProjectGroups.Update(file, new[] { oldFile, newFile }, false);
            var data = TimeProjectGroups.Load(file);
            Check(data.Resolve(oldFile) == group && data.Resolve(newFile) == group, "2024 and 2026 files persist in one project");
            Check(data.Resolve(other) == other, "Unrelated model stays separate");
            var rows = new[] { (Id: oldFile, Hours: 2.5), (Id: newFile, Hours: 1.25), (Id: other, Hours: 4.0) };
            Check(rows.Where(x => data.Resolve(x.Id) == group).Sum(x => x.Hours) == 3.75, "Old and new durations add exactly once");
            string expanded = TimeProjectGroups.Update(file, new[] { group, other }, false);
            data = TimeProjectGroups.Load(file);
            Check(rows.All(x => data.Resolve(x.Id) == expanded), "Existing groups merge transitively");
            TimeProjectGroups.Update(file, new[] { expanded }, true);
            data = TimeProjectGroups.Load(file);
            Check(rows.All(x => data.Resolve(x.Id) == x.Id), "Separating restores all original paths");
            bool staleRejected = false;
            try { TimeProjectGroups.Update(file, new[] { expanded, other }, false); } catch (IOException) { staleRejected = true; }
            Check(staleRejected, "Stale cross-session selections require a refresh");
            Check(rows.Sum(x => x.Hours) == 7.75, "Grouping and undo retain total recorded time");
            Check(TimeProjectGroups.Update(file, new[] { oldFile }, false) == null, "Single-file merge is a no-op");
            // Sequential edits from independent callers retain each other's mappings.
            TimeProjectGroups.Update(file, new[] { oldFile, newFile }, false);
            TimeProjectGroups.Update(file, new[] { other, @"C:\Other2.rvt" }, false);
            data = TimeProjectGroups.Load(file);
            Check(data.Resolve(oldFile) == data.Resolve(newFile) && data.Resolve(other) != data.Resolve(oldFile), "Independent project groups coexist");
            File.WriteAllText(file, "broken XML");
            bool failed = false;
            try { TimeProjectGroups.Update(file, new[] { oldFile, newFile }, false); } catch (System.Xml.XmlException) { failed = true; }
            Check(failed && File.ReadAllText(file) == "broken XML", "Unreadable mappings are reported and never overwritten");
        }
        finally { foreach (string item in Directory.GetFiles(folder)) File.Delete(item); Directory.Delete(folder); }
    }
}
