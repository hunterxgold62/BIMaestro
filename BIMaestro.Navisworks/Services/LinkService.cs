using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;

namespace BIMaestro.Navisworks.Services
{
    internal sealed class ModelReference
    {
        public string File { get; set; }
        public string Path { get; set; }
        public string Status { get; set; }
        public string AbsolutePath { get; set; }
        public string ReplacementPath { get; set; }
    }

    internal sealed class LinkService
    {
        private readonly Document document;
        private bool updating;
        internal const string DiagnosticVersion = "Diagnostic 4";
        private StringBuilder lastDiagnostic = new StringBuilder();
        private string diagnosticPath;
        private string diagnosticWriteError;

        public string DiagnosticReport => lastDiagnostic.ToString() + "\r\nFichier local : " +
            diagnosticPath + (diagnosticWriteError == null ? "" : "\r\nÉcriture impossible : " + diagnosticWriteError);

        private void SaveDiagnostic()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(diagnosticPath));
                File.WriteAllText(diagnosticPath, lastDiagnostic.ToString(), Encoding.UTF8);
                diagnosticWriteError = null;
            }
            catch (Exception error)
            {
                // Keep the entire report in memory even if local storage is unavailable.
                diagnosticWriteError = error.Message;
            }
        }

        public LinkService(Document document) { this.document = document; }

        public static bool IsNwf(Document document)
        {
            return document != null && !document.IsClear &&
                string.Equals(System.IO.Path.GetExtension(document.FileName), ".nwf", StringComparison.OrdinalIgnoreCase);
        }

        public List<ModelReference> GetReferences()
        {
            EnsureDocument();
            // FileName is the loaded reference; SourceFileName can instead be the RVT
            // from which an appended NWC was exported and must never be used here.
            return document.Models.Select(model => model.FileName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var absolute = ReferencePath.Normalize(path, document.FileName);
                    return new ModelReference
                    {
                        File = System.IO.Path.GetFileName(path), Path = path, AbsolutePath = absolute,
                        Status = absolute == null ? "Référence non locale" :
                            (System.IO.File.Exists(absolute) ? "Disponible" : "Introuvable")
                    };
                }).ToList();
        }

        public void ValidateReplacement(ModelReference reference, string replacement)
        {
            EnsureDocument();
            var newPath = ReferencePath.Normalize(replacement, document.FileName);
            if (reference.AbsolutePath == null || newPath == null)
                throw new InvalidOperationException("Seuls les chemins locaux ou UNC sont pris en charge.");
            if (ReferencePath.Same(reference.AbsolutePath, newPath))
                throw new InvalidOperationException("Choisissez un chemin différent.");
            if (!File.Exists(newPath))
                throw new FileNotFoundException("Le nouveau fichier est introuvable.", newPath);
            if (string.Equals(System.IO.Path.GetExtension(newPath), ".nwf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choisissez une maquette, pas un autre NWF.");
        }

        public string ApplyAndReload(IEnumerable<ModelReference> references)
        {
            if (updating) throw new InvalidOperationException("Un rechargement est déjà en cours.");
            lastDiagnostic = new StringBuilder("BIMaestro — " + DiagnosticVersion + "\r\n");
            diagnosticWriteError = null;
            diagnosticPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMaestro", "Navisworks", "Logs", "Liens-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
            lastDiagnostic.AppendLine("Date : " + DateTime.Now.ToString("O"));
            lastDiagnostic.AppendLine("DLL : " + typeof(LinkService).Assembly.Location);
            lastDiagnostic.AppendLine("Identifiant du build : " + typeof(LinkService).Module.ModuleVersionId);
            lastDiagnostic.AppendLine("API : " + typeof(Document).Assembly.FullName);
            SaveDiagnostic();
            try
            {
                return ApplyAndReloadCore(references);
            }
            catch (Exception error)
            {
                lastDiagnostic.AppendLine("Résultat : " + error);
                throw;
            }
            finally
            {
                SaveDiagnostic();
            }
        }

        private string ApplyAndReloadCore(IEnumerable<ModelReference> references)
        {
            EnsureDocument();
            if (updating) throw new InvalidOperationException("Un rechargement est déjà en cours.");
            if (references == null) throw new ArgumentNullException(nameof(references));
            var changes = references.Where(r => !string.IsNullOrWhiteSpace(r.ReplacementPath)).ToList();
            if (changes.Count == 0) throw new InvalidOperationException("Aucun changement préparé.");
            var before = GetReferences();
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                ValidateReplacement(change, change.ReplacementPath);
                if (!before.Any(r => ReferencePath.Same(r.AbsolutePath, change.AbsolutePath)))
                    throw new InvalidOperationException("La référence n'est plus chargée : " + change.Path);
                var target = ReferencePath.Normalize(change.ReplacementPath, document.FileName);
                // Reject swaps/chains as well: they are ambiguous during recursive resolution.
                if (before.Any(r => ReferencePath.Same(r.AbsolutePath, target)) ||
                    mapping.Values.Contains(target, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Un fichier cible est déjà chargé ou sélectionné deux fois : " + target);
                mapping.Add(change.AbsolutePath, target);
            }

            var nwf = ReferencePath.Normalize(document.FileName, null);
            if (nwf == null || !File.Exists(nwf))
                throw new InvalidOperationException("Enregistrez d'abord le NWF sur un chemin local ou UNC.");
            var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var diskBackup = nwf + ".BIMaestro-" + suffix + ".avant-enregistrement.nwf";
            var recovery = nwf + ".BIMaestro-" + suffix + ".avant-rechargement.nwf";
            var diagnostic = lastDiagnostic;
            diagnostic.AppendLine("NWF : " + nwf);
            foreach (var entry in mapping)
                diagnostic.AppendLine("Demandé : " + entry.Key + " => " + entry.Value);
            SaveDiagnostic();
            int resolutionEvents = 0;
            int matchingParents = 0;
            var intercepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EventHandler<FileResolvingEventArgs> resolver = (sender, args) =>
            {
                // During OpenFile, Document.FileName may be empty or refer to a staging
                // document. Resolve against the captured NWF, never the mutable document.
                var parent = ReferencePath.Normalize(args.ReferringFileName, nwf);
                resolutionEvents++;
                // Bound the report when readers resolve thousands of textures/dependencies.
                if (resolutionEvents <= 200)
                {
                    diagnostic.AppendLine("FileResolving : " + args.FileReference);
                    diagnostic.AppendLine("  Parent : " + args.ReferringFileName);
                    diagnostic.AppendLine("  Parent enregistré : " + args.ReferringFileNameAsSaved);
                    diagnostic.AppendLine("  Ouverture : " + args.ResolveToOpen + "; déjà traité : " + args.Handled);
                }
                if (!ReferencePath.Same(parent, nwf)) return;
                matchingParents++;
                var requested = ReferencePath.Normalize(args.FileReference, nwf);
                string target;
                if (requested == null || !mapping.TryGetValue(requested, out target)) return;
                args.ResolvedFileReference = target;
                // Explicit local-file redirection: the observed NWF reload emits only
                // ResolveToOpen=false and omits the redirected model when this is null.
                // Supply both paths, then require the model inventory to confirm loading.
                // This compatibility workaround must be verified in each supported host.
                args.FileNameToOpen = target;
                args.Handled = true;
                intercepted.Add(requested);
                diagnostic.AppendLine("Redirigé : " + requested + " => " + target);
                diagnostic.AppendLine("  FileNameToOpen transmis : " + args.FileNameToOpen);
            };

            updating = true;
            bool reopening = false;
            bool subscribed = false;
            try
            {
                // Preserve the previous disk version before saving the user's current work.
                File.Copy(nwf, diskBackup, false);
                if (document.IsModified && !document.TrySaveFile(nwf))
                    throw new IOException("Impossible d'enregistrer le NWF. Aucun rechargement effectué.");
                File.Copy(nwf, recovery, false);
                Application.FileResolving += resolver;
                subscribed = true;
                reopening = true;
                diagnostic.AppendLine("Début de TryOpenFile : " + nwf);
                SaveDiagnostic();
                if (!document.TryOpenFile(nwf))
                    throw new IOException("Navisworks n'a pas pu rouvrir le NWF.");
                Application.FileResolving -= resolver;
                subscribed = false;

                var after = GetReferences();
                var expected = before.Select(r => r.AbsolutePath == null ? r.Path :
                    (mapping.ContainsKey(r.AbsolutePath) ? mapping[r.AbsolutePath] : r.AbsolutePath))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                var actual = after.Select(r => r.AbsolutePath ?? r.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                diagnostic.AppendLine("Événements : " + resolutionEvents +
                    "; parent reconnu : " + matchingParents + "; chemins redirigés : " + intercepted.Count);
                foreach (var path in expected) diagnostic.AppendLine("Attendu : " + path);
                foreach (var path in actual) diagnostic.AppendLine("Chargé : " + path);
                SaveDiagnostic();
                if (intercepted.Count != mapping.Count ||
                    !expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                {
                    var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
                    var unexpected = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var path in missing) diagnostic.AppendLine("Absent après chargement : " + path);
                    foreach (var path in unexpected) diagnostic.AppendLine("Inattendu après chargement : " + path);
                    var reason = resolutionEvents == 0
                        ? "Navisworks n'a émis aucun événement FileResolving pendant la réouverture."
                        : matchingParents == 0
                            ? "Les événements reçus n'identifient pas le NWF attendu comme fichier parent."
                            : intercepted.Count != mapping.Count
                                ? "Seuls " + intercepted.Count + " chemin(s) sur " + mapping.Count + " ont été interceptés."
                                : "Tous les chemins ont été interceptés, mais la liste des fichiers chargés diffère du résultat attendu.";
                    throw new InvalidOperationException("Remplacement non validé. " + reason +
                        "\r\nRéférences attendues : " + expected.Length + "; chargées : " + actual.Length +
                        (missing.Length == 0 ? "" : "\r\nFichiers absents : " +
                            string.Join(", ", missing.Select(path => System.IO.Path.GetFileName(path)))));
                }

                // Do not persist an unreviewed model. Ctrl+S commits the verified links.
                diagnostic.AppendLine("Résultat : chemins vérifiés.");
                var cleanupWarnings = new List<string>();
                // Only delete the two uniquely named files created for this operation.
                // A cleanup failure must not roll back a successful model replacement.
                foreach (var temporaryBackup in new[] { diskBackup, recovery })
                {
                    try
                    {
                        File.Delete(temporaryBackup);
                        diagnostic.AppendLine("Sauvegarde temporaire supprimée : " + temporaryBackup);
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupWarnings.Add(temporaryBackup + " : " + cleanupError.Message);
                        diagnostic.AppendLine("Nettoyage impossible : " + cleanupWarnings.Last());
                    }
                }
                return cleanupWarnings.Count == 0
                    ? "Les deux sauvegardes temporaires ont été supprimées."
                    : "Le remplacement a réussi, mais certaines sauvegardes temporaires n'ont pas pu être supprimées :\r\n" +
                        string.Join("\r\n", cleanupWarnings);
            }
            catch (Exception error)
            {
                diagnostic.AppendLine("Erreur : " + error);
                if (subscribed)
                {
                    Application.FileResolving -= resolver;
                    subscribed = false;
                }
                var restoration = "";
                if (reopening)
                {
                    // Original NWF on disk still contains the saved work and original links.
                    try
                    {
                        if (!document.TryOpenFile(nwf))
                            restoration = "\r\nLa restauration automatique a échoué. Ouvrez la sauvegarde indiquée.";
                        else
                        {
                            var restored = GetReferences().Select(r => r.AbsolutePath ?? r.Path)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                            var original = before.Select(r => r.AbsolutePath ?? r.Path)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                            restoration = original.SequenceEqual(restored, StringComparer.OrdinalIgnoreCase)
                                ? "\r\nLe NWF a été rouvert et ses chemins précédents ont été vérifiés."
                                : "\r\nLe NWF a été rouvert, mais ses références ont changé. Vérifiez-les avant d'enregistrer.";
                        }
                    }
                    catch
                    {
                        restoration = "\r\nLa restauration automatique a échoué. Ouvrez la sauvegarde indiquée.";
                    }
                }
                diagnostic.AppendLine(restoration);
                throw new InvalidOperationException(error.Message + restoration +
                    "\r\nAucun changement de lien n'a été enregistré par BIMaestro." +
                    (File.Exists(recovery) ? "\r\nSauvegarde de votre travail : " + recovery :
                    (File.Exists(diskBackup) ? "\r\nCopie du NWF initial : " + diskBackup : "")), error);
            }
            finally
            {
                if (subscribed) Application.FileResolving -= resolver;
                updating = false;
            }
        }
        private void EnsureDocument()
        {
            if (!ReferenceEquals(Application.ActiveDocument, document) || !IsNwf(document))
                throw new InvalidOperationException("Ouvrez un NWF enregistré pour modifier ses liens.");
        }
    }
}
