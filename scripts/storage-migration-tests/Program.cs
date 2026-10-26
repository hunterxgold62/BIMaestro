using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BIMaestro.Storage;

internal static class Program
{
    private static string Sandbox;
    private static int Count;
    private static string Scenario(string name)
    {
        string path = Path.Combine(Sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Count++;
    }
    private static string Seed(string docs)
    {
        string old = Path.Combine(docs, "RevitLogs");
        Directory.CreateDirectory(Path.Combine(old, "License"));
        File.WriteAllText(Path.Combine(old, "License", "install_id.txt"), "existing-install-id");
        File.WriteAllBytes(Path.Combine(old, "history.dat"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(Path.Combine(old, "empty"));
        return old;
    }
    private static void Main()
    {
        // Retain all fixtures for review; never access the real user's Documents.
        Sandbox = Path.Combine(Path.GetFullPath("tmp"), "BIMaestro-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        string fresh = Scenario("fresh");
        Check(UserDataStorage.ResolveRoot(fresh) == Path.Combine(fresh, "BIMaestro_Revit"), "fresh root");
        Check(!Directory.Exists(Path.Combine(fresh, "RevitLogs")), "no legacy folder created");

        string docs = Scenario("existing");
        string old = Seed(docs);
        string prefs = Path.Combine(old, "SauvegardePréférence");
        Directory.CreateDirectory(prefs);
        string json = "{\"path\":\"" + (old + "\\FamilleRevit").Replace("\\", "\\\\") + "\",\"other\":\"" + (old + "Other").Replace("\\", "\\\\") + "\"}";
        File.WriteAllText(Path.Combine(prefs, "CheminsFamille.json"), json);
        File.WriteAllText(Path.Combine(prefs, "Favorites.txt"), old + "\\FamilleRevit\\test.rfa\n" + old + "Other\\test.rfa");
        string target = UserDataStorage.ResolveRoot(docs);
        Check(target == Path.Combine(docs, "BIMaestro_Revit"), "migrated root: " + UserDataStorage.MigrationWarning);
        Check(File.ReadAllText(Path.Combine(target, "License", "install_id.txt")) == "existing-install-id", "identity preserved");
        Check(File.ReadAllBytes(Path.Combine(old, "history.dat")).SequenceEqual(File.ReadAllBytes(Path.Combine(target, "history.dat"))), "binary integrity");
        Check(Directory.Exists(Path.Combine(target, "empty")), "empty folders preserved");
        Check(File.ReadAllText(Path.Combine(prefs, "CheminsFamille.json")) == json, "original preferences untouched");
        string migratedJson = File.ReadAllText(Path.Combine(target, "SauvegardePréférence", "CheminsFamille.json"));
        Check(migratedJson.Contains((target + "\\FamilleRevit").Replace("\\", "\\\\")), "JSON paths migrated");
        Check(migratedJson.Contains((old + "Other").Replace("\\", "\\\\")), "similarly named paths untouched");
        Check(File.ReadAllText(Path.Combine(target, "SauvegardePréférence", "Favorites.txt")).StartsWith(target + "\\"), "text paths migrated");
        File.WriteAllText(Path.Combine(target, "history.dat"), "new activity");
        Check(UserDataStorage.ResolveRoot(docs) == target && File.ReadAllText(Path.Combine(target, "history.dat")) == "new activity", "restart never overwrites migrated data");

        string conflict = Scenario("conflict");
        string conflictOld = Seed(conflict);
        Directory.CreateDirectory(Path.Combine(conflict, "BIMaestro_Revit"));
        File.WriteAllText(Path.Combine(conflict, "BIMaestro_Revit", "keep.txt"), "keep");
        Check(UserDataStorage.ResolveRoot(conflict) == conflictOld, "conflict falls back");
        Check(File.ReadAllText(Path.Combine(conflict, "BIMaestro_Revit", "keep.txt")) == "keep", "conflict untouched");
        Check(!File.Exists(Path.Combine(conflict, "BIMaestro_Revit", ".bimaestro-migration-v1")), "conflict not committed");

        string locked = Scenario("locked");
        string lockedOld = Seed(locked);
        using (var stream = new FileStream(Path.Combine(lockedOld, "history.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(UserDataStorage.ResolveRoot(locked) == lockedOld, "locked file falls back");
        Check(!Directory.Exists(Path.Combine(locked, "BIMaestro_Revit")), "partial copy never published");
        Check(UserDataStorage.ResolveRoot(locked) == Path.Combine(locked, "BIMaestro_Revit"), "retry succeeds after unlocking");

        string concurrent = Scenario("concurrent");
        Seed(concurrent);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => UserDataStorage.ResolveRoot(concurrent))).ToArray();
        Task.WaitAll(tasks);
        Check(tasks.All(t => t.Result == Path.Combine(concurrent, "BIMaestro_Revit")), "concurrent migration uses one destination");
        Check(Directory.GetDirectories(concurrent, ".BIMaestro-migration-*").Length == 0, "successful staging renamed");
        string foreign = Scenario("unrelated");
        Directory.CreateDirectory(Path.Combine(foreign, "RevitLogsOther"));
        UserDataStorage.ResolveRoot(foreign);
        Check(Directory.Exists(Path.Combine(foreign, "RevitLogsOther")), "unrelated directories untouched");
        Console.WriteLine("PASS: " + Count + " assertions. Fixtures: " + Sandbox);
    }
}
