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
        string newPreferences = Scenario("new-preferences");
        Check(PreferenceStorage.Prepare(newPreferences), "new preferences directly organized");
        Check(!Directory.Exists(Path.Combine(newPreferences, "SauvegardePréférence_avant_classement_v1")), "new installation needs no backup");

        string organizedRoot = Scenario("organized");
        string flat = Path.Combine(organizedRoot, "SauvegardePréférence");
        Directory.CreateDirectory(flat);
        foreach (var pair in PreferenceStorage.Groups)
            File.WriteAllText(Path.Combine(flat, pair.Key), "preserved:" + pair.Key);
        File.WriteAllText(Path.Combine(flat, "unknown-old-setting.bin"), "unknown preserved");
        Check(PreferenceStorage.Prepare(organizedRoot), "organize succeeds");
        foreach (var pair in PreferenceStorage.Groups)
            Check(File.ReadAllText(Path.Combine(flat, pair.Value, pair.Key)) == "preserved:" + pair.Key, "group preserves " + pair.Key);
        Check(File.ReadAllText(Path.Combine(flat, "Autres préférences", "unknown-old-setting.bin")) == "unknown preserved", "unknown settings retained");
        Check(File.ReadAllText(Path.Combine(organizedRoot, "SauvegardePréférence_avant_classement_v1", "Favorites.txt")) == "preserved:Favorites.txt", "original layout backed up");
        Check(Directory.GetFiles(flat).Length == 1, "only hidden marker remains at preference root");
        File.WriteAllText(Path.Combine(flat, "Option", "ui-language.json"), "new language");
        Check(PreferenceStorage.Prepare(organizedRoot), "organized restart succeeds");
        Check(File.ReadAllText(Path.Combine(flat, "Option", "ui-language.json")) == "new language", "restart preserves new preferences");
        Check(PreferenceStorage.Folder("Option", organizedRoot) == Path.Combine(flat, "Option"), "clients resolve grouped folder");

        string blockedRoot = Scenario("preferences-locked");
        string blockedPrefs = Path.Combine(blockedRoot, "SauvegardePréférence");
        Directory.CreateDirectory(blockedPrefs);
        File.WriteAllText(Path.Combine(blockedPrefs, "Favorites.txt"), "locked favorites");
        using (var stream = new FileStream(Path.Combine(blockedPrefs, "Favorites.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!PreferenceStorage.Prepare(blockedRoot), "locked preferences defer entire layout");
        Check(File.ReadAllText(Path.Combine(blockedPrefs, "Favorites.txt")) == "locked favorites", "locked migration preserves source");
        Check(PreferenceStorage.Prepare(blockedRoot), "preferences migration retries successfully");

        string mixedRoot = Scenario("preferences-conflict");
        string mixedPrefs = Path.Combine(mixedRoot, "SauvegardePréférence");
        Directory.CreateDirectory(Path.Combine(mixedPrefs, "Option"));
        File.WriteAllText(Path.Combine(mixedPrefs, "ui-language.json"), "flat");
        File.WriteAllText(Path.Combine(mixedPrefs, "Option", "ui-language.json"), "grouped");
        Check(!PreferenceStorage.Prepare(mixedRoot), "mixed conflict never overwrites");
        Check(File.ReadAllText(Path.Combine(mixedPrefs, "ui-language.json")) == "flat" && File.ReadAllText(Path.Combine(mixedPrefs, "Option", "ui-language.json")) == "grouped", "both conflicting files retained");

        string interrupted = Scenario("preferences-interrupted");
        string interruptedBackup = Path.Combine(interrupted, "SauvegardePréférence_avant_classement_v1");
        Directory.CreateDirectory(interruptedBackup);
        File.WriteAllText(Path.Combine(interruptedBackup, "Favorites.txt"), "recover favorites");
        Check(PreferenceStorage.Prepare(interrupted), "recovers interrupted folder switch");
        Check(File.ReadAllText(Path.Combine(interrupted, "SauvegardePréférence", "Navigateur de Familles", "Favorites.txt")) == "recover favorites", "interrupted data restored");

        Check(PreferenceStorage.Groups["iconesArborescence.json"] == "Option", "browser icon classification");
        Check(PreferenceStorage.Groups["clash3d_options.json"] == "SmartCheck", "SmartCheck options classification");
        Check(PreferenceStorage.Groups["HomeAppearance.json"] == "Accueil", "Home classification");
        Check(PreferenceStorage.Groups["mep-booster-enabled.txt"] == "MEP Booster"
            && PreferenceStorage.Groups["mep-booster-angle.txt"] == "MEP Booster", "MEP settings classification");

        // Reproduce a layout already classified by the buggy version, with its existing marker.
        string repairRoot = Scenario("repair");
        string repairPrefs = Path.Combine(repairRoot, "SauvegardePréférence");
        string misplaced = Path.Combine(repairPrefs, "Autres préférences");
        Directory.CreateDirectory(misplaced);
        File.WriteAllText(Path.Combine(repairPrefs, ".organized-v1"), "old organized layout");
        foreach (var name in new[] { "iconesArborescence.json", "clash3d_options.json" })
        {
            string group = PreferenceStorage.Groups[name];
            File.WriteAllText(Path.Combine(misplaced, name), "preserved:" + name);
            string folder = PreferenceStorage.Folder(group, repairRoot);
            string resolved = PreferenceStorage.ResolveFile(folder, name);
            Check(resolved == Path.Combine(repairPrefs, group, name), "repaired destination " + name);
            Check(File.ReadAllText(resolved) == "preserved:" + name, "recovered misplaced " + name);
            Check(File.ReadAllText(Path.Combine(misplaced, name)) == "preserved:" + name, "misplaced original retained " + name);
        }

        // AppData locations are simulated here: no access to the actual Windows profile.
        string importRoot = Scenario("import");
        foreach (var pair in new[] {
            new[] { "HomeAppearance.test.json", "HomeAppearance.json", "Accueil", "{\"Style\":\"2024\",\"History\":true}" },
            new[] { "mep-booster-enabled.txt", "mep-booster-enabled.txt", "MEP Booster", "True" },
            new[] { "mep-booster-angle.txt", "mep-booster-angle.txt", "MEP Booster", "37.5" }
        })
        {
            string legacyFolder = Path.Combine(importRoot, "old"); Directory.CreateDirectory(legacyFolder);
            string legacy = Path.Combine(legacyFolder, pair[0]); File.WriteAllText(legacy, pair[3]);
            string targetFile = Path.Combine(importRoot, pair[2], pair[1]);
            Check(PreferenceStorage.ImportFile(targetFile, legacy) == targetFile, "import destination " + pair[1]);
            Check(File.ReadAllText(targetFile) == pair[3], "import preserves content " + pair[1]);
            Check(File.ReadAllText(legacy) == pair[3], "import retains original " + pair[1]);
            File.WriteAllText(targetFile, "new settings"); File.WriteAllText(legacy, "stale settings");
            PreferenceStorage.ImportFile(targetFile, legacy);
            Check(File.ReadAllText(targetFile) == "new settings", "restart keeps new settings " + pair[1]);
            File.Delete(targetFile);
            PreferenceStorage.ImportFile(targetFile, legacy);
            Check(!File.Exists(targetFile), "reset never resurrects old settings " + pair[1]);
        }
        string importConflict = Scenario("imp-c");
        string existingSetting = Path.Combine(importConflict, "new.json"), oldSetting = Path.Combine(importConflict, "old.json");
        File.WriteAllText(existingSetting, "current"); File.WriteAllText(oldSetting, "legacy");
        PreferenceStorage.ImportFile(existingSetting, oldSetting);
        Check(File.ReadAllText(existingSetting) == "current" && File.ReadAllText(oldSetting) == "legacy", "import conflict preserves both files");

        string importLocked = Scenario("imp-l");
        string lockedSource = Path.Combine(importLocked, "old.txt"), lockedTarget = Path.Combine(importLocked, "new.txt");
        File.WriteAllText(lockedSource, "locked source");
        using (var stream = new FileStream(lockedSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool refused = false;
            try { PreferenceStorage.ImportFile(lockedTarget, lockedSource); } catch (IOException) { refused = true; }
            Check(refused && !File.Exists(lockedTarget), "locked import never publishes defaults");
        }
        PreferenceStorage.ImportFile(lockedTarget, lockedSource);
        Check(File.ReadAllText(lockedTarget) == "locked source", "locked import retries");
        Check(Directory.GetFiles(importLocked, ".import-*.tmp").Length == 0, "import temporary files cleaned");

        string importConcurrent = Scenario("imp-p");
        string parallelSource = Path.Combine(importConcurrent, "old.txt"), parallelTarget = Path.Combine(importConcurrent, "new.txt");
        File.WriteAllText(parallelSource, "parallel source");
        var imports = Enumerable.Range(0, 4).Select(_ => Task.Run(() => PreferenceStorage.ImportFile(parallelTarget, parallelSource))).ToArray();
        Task.WaitAll(imports);
        Check(imports.All(t => t.Result == parallelTarget) && File.ReadAllText(parallelTarget) == "parallel source", "concurrent imports agree");
        Check(File.ReadAllText(parallelSource) == "parallel source", "concurrent imports retain source");

        string deferredImportRoot = Scenario("imp-f");
        string flatImport = Path.Combine(deferredImportRoot, "SauvegardePréférence");
        Directory.CreateDirectory(flatImport);
        string beforeClassification = Path.Combine(flatImport, "mep-booster-angle.txt");
        string deferredLegacy = Path.Combine(deferredImportRoot, "old-angle.txt");
        File.WriteAllText(deferredLegacy, "42.5");
        PreferenceStorage.ImportFile(beforeClassification, deferredLegacy);
        Check(PreferenceStorage.Prepare(deferredImportRoot), "classifies a previously deferred import");
        string classifiedAngle = Path.Combine(flatImport, "MEP Booster", "mep-booster-angle.txt");
        Check(File.ReadAllText(classifiedAngle) == "42.5", "classification preserves imported angle");
        File.Delete(classifiedAngle);
        PreferenceStorage.ResolveFile(Path.GetDirectoryName(classifiedAngle), Path.GetFileName(classifiedAngle), deferredLegacy);
        Check(!File.Exists(classifiedAngle), "classification preserves the one-time import marker after reset");

        Console.WriteLine("PASS: " + Count + " assertions. Fixtures: " + Sandbox);
    }
}
