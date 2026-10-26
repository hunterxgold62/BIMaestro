using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Linq;

namespace BIMaestro.Storage
{
    /// <summary>Stable button names, independent of the current UI language.</summary>
    public static class PreferenceStorage
    {
        private const string Marker = ".organized-v1";
        private static readonly ConcurrentDictionary<string, Lazy<bool>> Layouts =
            new ConcurrentDictionary<string, Lazy<bool>>(StringComparer.OrdinalIgnoreCase);
        public static string MigrationWarning { get; private set; }
        internal static readonly Dictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Favorites.txt"] = "Navigateur de Familles", ["Config.txt"] = "Navigateur de Familles",
            ["CheminsFamille.json"] = "Navigateur de Familles", ["Collections.json"] = "Navigateur de Familles",
            ["FamilyUsage.json"] = "Navigateur de Familles", ["FamilyRecent.json"] = "Navigateur de Familles",
            ["RadialMenuCollectionState.json"] = "Rosace familles",
            ["RecentButtons.json"] = "Rosace Boutons", ["RadialButtons.json"] = "Rosace Boutons",
            ["RibbonLayout.json"] = "Option", ["ui-language.json"] = "Option",
            ["couleursRuban.json"] = "Option", ["couleursRubanRevit.json"] = "Option",
            ["iconesArborescence.json"] = "Option", ["clash3d_options.json"] = "SmartCheck",
            ["HomeAppearance.json"] = "Accueil",
            ["mep-booster-enabled.txt"] = "MEP Booster", ["mep-booster-angle.txt"] = "MEP Booster",
            ["AppearanceGuideFirstLaunch.txt"] = "Option", ["resetColor.txt"] = "Couleurs",
            ["CacheTailleFamille.json"] = "Analyse de Poids",
            ["smartcheck_ignored.json"] = "SmartCheck", ["smartcheck_status.json"] = "SmartCheck",
            ["thème IA.json"] = "Corr. IA", ["thème IA auto.json"] = "Corr. IA",
            ["translation_cache.json"] = "Trad.IA", ["RevitUnits.json"] = "Unités",
            ["SelectSimilarPreferences.json"] = "Sélection d'objet", ["SelectSimilarLastSet.json"] = "Sélection d'objet",
            ["ReservationAutoV3.json"] = "Auto réservation", ["ResaPerso.json"] = "Auto réservation",
            ["Menu contexte.json"] = "Menu contextuel", ["ViewHoverPreview.txt"] = "Miniature",
            ["ElementHistoryDeletedMeshMode.json"] = "Qui a fait ça",
            ["DynamoButtons.json"] = "Dynamo", ["DynamoPaths.txt"] = "Dynamo",
            ["snake.json"] = "Snake", ["flappybird.json"] = "Flappy Bird",
            ["AutoCanalisation.txt"] = "Auto canalisation"
        };

        public static string Folder(string button, string root = null)
        {
            if (string.IsNullOrWhiteSpace(button) || button.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || button == "." || button == "..") throw new ArgumentException("Invalid preference group.", nameof(button));
            root = Path.GetFullPath(root ?? UserDataStorage.Root);
            bool organized = Layouts.GetOrAdd(root, r => new Lazy<bool>(() => Prepare(r, true))).Value;
            string folder = Path.Combine(root, "SauvegardePréférence");
            if (organized) folder = Path.Combine(folder, button);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void Initialize(string root = null) { Folder("Option", root); }

        // Import old locations without overwriting current settings or deleting the source.
        // A separate marker prevents an intentional reset/deletion from reviving old settings.
        public static string FilePath(string button, string name, params string[] legacyPaths)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
                throw new ArgumentException("Invalid preference filename.", nameof(name));
            string folder = Folder(button);
            return ResolveFile(folder, name, legacyPaths);
        }

        internal static string ResolveFile(string folder, string name, params string[] legacyPaths)
        {
            string prefs = Path.GetFileName(folder) == "SauvegardePréférence" ? folder : Path.GetDirectoryName(folder);
            return ImportFile(Path.Combine(folder, name), new[] {
                Path.Combine(prefs, "Autres préférences", name), Path.Combine(prefs, name)
            }.Concat(legacyPaths ?? new string[0]).ToArray());
        }

        internal static string ImportFile(string destination, params string[] legacyPaths)
        {
            destination = Path.GetFullPath(destination);
            string folder = Path.GetDirectoryName(destination);
            string id;
            using (var sha = SHA256.Create())
                id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(destination.ToUpperInvariant()))).Replace("-", "");
            string marker = Path.Combine(folder, ".imported-" + Path.GetFileName(destination) + "-v1");
            using (var mutex = new Mutex(false, @"Local\BIMaestroPreferenceImport-" + id))
            {
                bool held = false;
                string temporary = null;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Preference import is already running.");
                    if (File.Exists(marker)) return destination;
                    Directory.CreateDirectory(folder);
                    if (!File.Exists(destination))
                    {
                        string source = (legacyPaths ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(Path.GetFullPath).FirstOrDefault(p => !string.Equals(p, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
                        if (source != null)
                        {
                            temporary = Path.Combine(folder, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
                            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                                input.Position = 0;
                                using (var sha = SHA256.Create())
                                using (var copy = File.OpenRead(temporary))
                                    if (!sha.ComputeHash(input).SequenceEqual(sha.ComputeHash(copy)))
                                        throw new IOException("Preference import verification failed: " + source);
                                // A writer outside this helper may have created the destination meanwhile.
                                try { File.Move(temporary, destination); }
                                catch (IOException) when (File.Exists(destination)) { }
                            }
                        }
                    }
                    File.WriteAllText(marker, "BIMaestro preference import v1");
                    File.SetAttributes(marker, FileAttributes.Hidden);
                    return destination;
                }
                finally
                {
                    try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
                    finally { if (held) mutex.ReleaseMutex(); }
                }
            }
        }

        // Copy and verify the whole old layout before switching it as a unit. The backup
        // remains outside SauvegardePréférence; all clients choose the same layout.
        internal static bool Prepare(string root, bool checkRevitSessions = false)
        {
            string prefs = Path.Combine(root, "SauvegardePréférence");
            string backup = Path.Combine(root, "SauvegardePréférence_avant_classement_v1");
            string stage = Path.Combine(root, ".preferences-stage-" + Guid.NewGuid().ToString("N"));
            string id;
            using (var sha = SHA256.Create())
                id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, @"Local\BIMaestroPreferences-" + id))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Preference migration is already running.");
                    if (!Directory.Exists(prefs) && Directory.Exists(backup)) UserDataStorage.Publish(backup, prefs);
                    if (File.Exists(Path.Combine(prefs, Marker))) return true;
                    if (string.Equals(Path.GetFileName(root), "RevitLogs", StringComparison.OrdinalIgnoreCase))
                        return false; // Never reorganize the preserved legacy source after a deferred root migration.
                    if (!Directory.Exists(prefs))
                    {
                        Directory.CreateDirectory(prefs);
                        File.WriteAllText(Path.Combine(prefs, Marker), "BIMaestro preferences layout v1");
                        File.SetAttributes(Path.Combine(prefs, Marker), FileAttributes.Hidden);
                        return true;
                    }
                    if (checkRevitSessions && System.Diagnostics.Process.GetProcessesByName("Revit").Length > 1)
                        throw new IOException("Fermer les autres sessions Revit avant le classement des préférences.");
                    if (Directory.Exists(backup) || File.Exists(backup))
                        throw new IOException("Une sauvegarde de classement existe déjà ; aucun fichier remplacé.");
                    Directory.CreateDirectory(prefs);
                    UserDataStorage.CopyVerified(prefs, stage);
                    UserDataStorage.VerifyTree(prefs, stage);
                    foreach (string file in Directory.GetFiles(stage))
                    {
                        string name = Path.GetFileName(file);
                        string group = GroupFor(name);
                        string destination = Path.Combine(stage, group);
                        Directory.CreateDirectory(destination);
                        File.Move(file, Path.Combine(destination, name)); // no overwrite, including mixed layouts
                    }
                    File.WriteAllText(Path.Combine(stage, Marker), "BIMaestro preferences layout v1");
                    File.SetAttributes(Path.Combine(stage, Marker), FileAttributes.Hidden);
                    VerifyOrganized(prefs, stage);
                    UserDataStorage.Publish(prefs, backup);
                    try { UserDataStorage.Publish(stage, prefs); }
                    catch
                    {
                        if (!Directory.Exists(prefs)) UserDataStorage.Publish(backup, prefs);
                        throw;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    if (!held || !Directory.Exists(prefs)) throw;
                    MigrationWarning = "Le classement des préférences par bouton a été différé. " +
                        "Les anciens emplacements restent utilisés pour cette session.\n\n" + ex.Message;
                    return false;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static void VerifyOrganized(string source, string stage)
        {
            if (Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length
                != Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length - 1)
                throw new IOException("The preference file list changed during migration.");
            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                string group = GroupFor(name);
                using (var sha = SHA256.Create())
                using (var original = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var copy = File.OpenRead(Path.Combine(stage, group, name)))
                    if (!sha.ComputeHash(original).SequenceEqual(sha.ComputeHash(copy)))
                        throw new IOException("Preferences changed during migration: " + name);
            }
            foreach (string dir in Directory.GetDirectories(source))
                UserDataStorage.VerifyTree(dir, Path.Combine(stage, Path.GetFileName(dir)));
        }

        private static string GroupFor(string name)
        {
            string group;
            if (Groups.TryGetValue(name, out group)) return group;
            // Imports can run while a deferred layout still uses the flat folder.
            // Keep their reset markers beside the settings when that layout is classified.
            const string prefix = ".imported-", suffix = "-v1";
            if (name.Length > prefix.Length + suffix.Length
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && Groups.TryGetValue(name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length), out group)) return group;
            return "Autres préférences";
        }
    }
}
