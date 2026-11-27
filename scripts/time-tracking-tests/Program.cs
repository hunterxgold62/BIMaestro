using System;
using System.IO;
using System.Linq;
using BIMaestro.Dashboard;

class Program
{
    static int count;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); count++; }
    static void Main()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "tracking.xml");
        const string identity = "creation:maquette", original = @"C:\2024\École & école.rvt", moved = @"D:\2026\École renommée.rvt", copy = @"D:\Copie.rvt";
        try
        {
            var initial = TimeTrackingRegistry.Observe(file, identity, original, "École");
            Check(!initial.Pending && initial.Project == original, "Original path adopts its legacy history without a question");
            var pending = TimeTrackingRegistry.Observe(file, identity, moved, "École renommée");
            Check(pending.Pending, "Renamed, moved or upgraded model requires an explicit choice");
            Check(TimeTrackingRegistry.Load(file).Find(identity, moved).Pending, "Decide later survives reopening the registry");
            var rows = new[] { (Identity: "", Path: original, Hours: 12.0), (Identity: identity, Path: moved, Hours: 1.0), (Identity: identity, Path: moved, Hours: .5) };
            var data = TimeTrackingRegistry.Load(file);
            Check(rows.Where(x => data.Resolve(x.Identity, x.Path) == pending.PendingId).Sum(x => x.Hours) == 1.5, "Multiple deferred sessions stay pending across restarts");
            Check(rows.Where(x => data.Resolve(x.Identity, x.Path) == original).Sum(x => x.Hours) == 12, "Pending time is excluded from the recognized project until a decision");
            Check(TimeTrackingRegistry.Decide(file, identity, moved, original, false), "Continue same project commits");
            data = TimeTrackingRegistry.Load(file);
            Check(rows.Where(x => data.Resolve(x.Identity, x.Path) == original).Sum(x => x.Hours) == 13.5, "Continue adds every deferred session exactly once");
            Check(!TimeTrackingRegistry.Decide(file, identity, moved, null, true), "A second concurrent decision cannot overwrite the first");
            Check(!TimeTrackingRegistry.Observe(file, identity, original, "École").Pending && !TimeTrackingRegistry.Observe(file, identity, moved, "École").Pending, "Returning to either old or new file does not ask again");
            var duplicate = TimeTrackingRegistry.Observe(file, identity, copy, "Nouveau projet");
            Check(duplicate.Pending, "A further renamed copy has its own question");
            TimeTrackingRegistry.Decide(file, identity, copy, null, true);
            data = TimeTrackingRegistry.Load(file);
            string newProject = data.Resolve(identity, copy);
            Check(newProject != original && !data.IsPending(identity, copy), "Start new project keeps a separate counter");
            Check(data.Resolve(identity, moved) == original && data.Resolve(identity, original) == original, "Separating a copy does not affect previously accepted paths");
            Check(!TimeTrackingRegistry.Observe(file, identity, copy, "Nouveau projet").Pending, "New-project decision persists on reopening");
            Check(data.Resolve(identity, @"d:/2026/école renommée.rvt") == original, "Path case and separators do not trigger duplicate questions");
            Check(TimeTrackingRegistry.Observe(file, "creation:unrelated", @"C:\Autre\École.rvt", "École").Project != original, "Same filename with a different identity is not recognized as the old model");
            var branch = TimeTrackingRegistry.Observe(file, identity, @"D:\Troisième.rvt", "Variante");
            data = TimeTrackingRegistry.Load(file);
            Check(data.Candidates(branch).Count == 2, "Copies with separate histories offer a choice of the recognized projects");
            bool invalidRejected = false;
            try { TimeTrackingRegistry.Decide(file, identity, branch.Path, "missing", false); } catch (InvalidDataException) { invalidRejected = true; }
            Check(invalidRejected && TimeTrackingRegistry.Load(file).Find(identity, branch.Path).Pending, "An invalid candidate never consumes pending sessions");
            TimeTrackingRegistry.SeparateProjects(file, new[] { original });
            data = TimeTrackingRegistry.Load(file);
            Check(data.Resolve(identity, original) != data.Resolve(identity, moved), "Manual separation also separates previously linked identity paths");
            Check(!data.Find(identity, original).Pending && !data.Find(identity, moved).Pending, "Manual separation preserves remembered choices without new prompts");
            var replaced = TimeTrackingRegistry.Observe(file, "creation:replacement", original, "Autre maquette");
            Check(replaced.Project != original, "An unrelated model replacing a file gets its own counter");
            string compatibility = Path.Combine(folder, "compatibility.xml");
            const string oldIdentity = "element:legacy-project", nativeIdentity = "creation:upgraded-project";
            TimeTrackingRegistry.Observe(compatibility, oldIdentity, original, "École 2023");
            var upgraded = TimeTrackingRegistry.Observe(compatibility, nativeIdentity, moved, "École 2026", oldIdentity);
            Check(upgraded.Pending, "2022/2023 identity is recognized after conversion to 2024 or later");
            var compatible = TimeTrackingRegistry.Load(compatibility);
            Check(compatible.Candidates(upgraded).Single().Project == original, "Upgrade offers the original project instead of starting over");
            TimeTrackingRegistry.Decide(compatibility, nativeIdentity, moved, original, false);
            compatible = TimeTrackingRegistry.Load(compatibility);
            Check(compatible.Resolve(nativeIdentity, moved) == compatible.Resolve(oldIdentity, original), "Old and modern identity rows share the chosen total");
            Check(!TimeTrackingRegistry.Observe(compatibility, nativeIdentity, moved, "École", oldIdentity).Pending
                && !TimeTrackingRegistry.Observe(compatibility, oldIdentity, original, "École").Pending, "Returning across the old/new API boundary does not repeat the question");
            var unrelated = TimeTrackingRegistry.Observe(compatibility, "creation:different-model", copy, "Autre modèle", oldIdentity);
            Check(!unrelated.Pending && unrelated.Project != original, "Different native GUIDs are not merged through a shared template element");
            string reverse = Path.Combine(folder, "reverse.xml");
            TimeTrackingRegistry.Observe(reverse, nativeIdentity, original, "École", oldIdentity);
            var backwards = TimeTrackingRegistry.Observe(reverse, oldIdentity, moved, "Copie ouverte dans Revit 2023");
            Check(backwards.Pending, "Legacy API also recognizes a lineage first seen in the modern API");
            Check(TimeTrackingRegistry.Load(reverse).Candidates(backwards).Single().Project == original, "The identity bridge survives saving and reopening the registry");
            File.WriteAllText(file, "broken XML"); bool brokenRejected = false;
            try { TimeTrackingRegistry.Observe(file, identity, moved, "École"); } catch (System.Xml.XmlException) { brokenRejected = true; }
            Check(brokenRejected && File.ReadAllText(file) == "broken XML", "Unreadable registry is reported without overwriting decisions");
            Console.WriteLine(count + " checks passed");
        }
        finally { foreach (var item in Directory.GetFiles(folder)) File.Delete(item); Directory.Delete(folder); }
    }
}
