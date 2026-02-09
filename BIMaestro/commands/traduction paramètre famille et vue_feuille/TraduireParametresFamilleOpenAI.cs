// TraduireParametresFamilleOpenAI.cs
// Revit 2023+ – Traduction batch rapide et robuste des paramètres *renommables*
// - Pas de message au début ; un seul récapitulatif final avec la liste des renommages
// - Batching JSON + parallélisme limité (réseau uniquement)
// - Extraction JSON tolérante (balance des accolades)
// - Cache disque : %APPDATA%\BIMaestro\translation_cache.json
//
// Dépendances dans ton projet :
//   - Licensing.BaseTrackedCommand
//   - AiClient.SendOpenAI(jwt, model, prompt) -> JObject OpenAI-like { choices[0].message.content }

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class TraduireParametresFamilleOpenAI : BaseTrackedCommand
    {
        protected override string ButtonId => "TraduireParametresFamilleOpenAI";

        // Réglages réseau/performances
        private const string Model = "gpt-4o-mini";
        private const int BatchSize = 80;                 // 50–100 recommandé
        private const int MaxParallelBatches = 2;         // 2 ou 3 selon quota
        private const int MaxRetriesPerBatch = 3;
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("Erreur", "Ouvrez une famille avant d’exécuter ce plugin.");
                return Result.Cancelled;
            }

            string jwt = BIMaestroApp.LicenseJwt;

            // Pour afficher TOUT à la fin
            var finalWarnings = new List<string>();
            var finalRenameErrors = new List<string>();
            var finalRenamed = new List<(string OldName, string NewName)>();
            int totalParams = 0, userParams = 0, sharedParams = 0, builtinParams = 0;

            try
            {
                var fm = doc.FamilyManager;
                var all = fm.GetParameters();

                // 1) Collecte (sans message UI)
                var candidates = new List<FamilyParameter>();
                foreach (var fp in all)
                {
                    if (fp?.Definition == null) continue;
                    totalParams++;

                    bool isShared = IsSharedSafe(fp);
                    var bip = GetBuiltInParameterSafe(fp);
                    bool isBuiltIn = (bip != BuiltInParameter.INVALID);
                    bool canRename = !isShared && !isBuiltIn;

                    if (canRename)
                    {
                        userParams++;
                        candidates.Add(fp);
                    }
                    else
                    {
                        if (isShared) sharedParams++;
                        else if (isBuiltIn) builtinParams++;
                    }
                }

                if (candidates.Count == 0)
                {
                    TaskDialog.Show(
                        "BIMaestro - Résultat",
                        "Aucun paramètre *renommable* détecté dans cette famille.\n\n" +
                        $"Total: {totalParams}  •  Utilisateur: {userParams}  •  Partagés: {sharedParams}  •  Intégrés: {builtinParams}"
                    );
                    return Result.Succeeded;
                }

                // Snapshot des noms (pas d'API Revit dans les threads)
                var items = candidates
                    .Select(fp => new ParamItem { Param = fp, OriginalName = fp.Definition.Name })
                    .ToList();

                // 2) Cache
                var cache = LoadCache();

                // 3) Dédoublonnage et liste à traduire
                var uniqueNames = items.Select(i => i.OriginalName)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                var toTranslate = uniqueNames.Where(n => !cache.ContainsKey(n)).ToList();

                // 4) Traduction IA en batches parallélisés
                if (toTranslate.Count > 0)
                {
                    var batches = Chunk(toTranslate, BatchSize).ToList();
                    var throttler = new SemaphoreSlim(MaxParallelBatches);
                    var resultsBag = new ConcurrentBag<Dictionary<string, string>>();
                    var errorsBag = new ConcurrentBag<string>();

                    var tasks = batches.Select(async batch =>
                    {
                        await throttler.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var dict = await TranslateBatchWithRetry(jwt, batch).ConfigureAwait(false);
                            resultsBag.Add(dict);
                        }
                        catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
                        {
                            errorsBag.Add("Quota API dépassé – arrêt.");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            errorsBag.Add($"Batch IA échec: {ex.Message}");
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }).ToArray();

                    Task.WaitAll(tasks);

                    if (!resultsBag.IsEmpty)
                    {
                        foreach (var d in resultsBag)
                        {
                            foreach (var kv in d)
                            {
                                if (!cache.ContainsKey(kv.Key))
                                    cache[kv.Key] = kv.Value;
                            }
                        }
                        SaveCache(cache);
                    }

                    if (!errorsBag.IsEmpty)
                    {
                        finalWarnings.Add("Certains lots n’ont pas pu être traduits :");
                        foreach (var e in errorsBag.Take(10)) finalWarnings.Add(" - " + e);
                        if (errorsBag.Count > 10) finalWarnings.Add($"(+ {errorsBag.Count - 10} erreurs supplémentaires)");
                    }
                }

                // 5) Mapping final param -> traduction
                var renameMap = new Dictionary<FamilyParameter, string>();
                foreach (var it in items)
                {
                    if (cache.TryGetValue(it.OriginalName, out string translated))
                    {
                        translated = NormalizeName(translated);
                        if (!string.IsNullOrEmpty(translated) &&
                            !translated.Equals(it.OriginalName, StringComparison.OrdinalIgnoreCase))
                        {
                            renameMap[it.Param] = translated;
                        }
                    }
                }

                if (renameMap.Count == 0)
                {
                    TaskDialog.Show("BIMaestro - Résultat",
                        "Aucune traduction appliquée (tout est déjà en FR ou identique).\n\n" +
                        $"Paramètres analysés: {items.Count}");
                    return Result.Succeeded;
                }

                // 6) Renommage en une transaction
                using (var tx = new Transaction(doc, "Traduire paramètres via OpenAI (batch)"))
                {
                    tx.Start();

                    var existing = new HashSet<string>(
                        fm.GetParameters().Select(p => p.Definition?.Name ?? ""),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in renameMap)
                    {
                        var param = kvp.Key;
                        var target = kvp.Value;

                        if (string.IsNullOrWhiteSpace(target))
                            continue;

                        string current = param.Definition.Name;
                        if (current.Equals(target, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string finalName = EnsureUniqueName(existing, target);

                        try
                        {
                            fm.RenameParameter(param, finalName);
                            existing.Add(finalName);
                            finalRenamed.Add((current, finalName));
                        }
                        catch (Exception ex)
                        {
                            finalRenameErrors.Add($"'{current}' → '{finalName}' : {ex.Message}");
                        }
                    }

                    tx.Commit();
                }

                // 7) Message FINAL unique
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Paramètres *renommables* détectés : {items.Count}");
                    sb.AppendLine($"Paramètres traduits : {finalRenamed.Count}");
                    sb.AppendLine();

                    if (finalRenamed.Count > 0)
                    {
                        sb.AppendLine("Renommages appliqués :");
                        foreach (var p in finalRenamed.Take(30))
                            sb.AppendLine($" - {p.OldName} → {p.NewName}");
                        if (finalRenamed.Count > 30)
                            sb.AppendLine($"… (+ {finalRenamed.Count - 30} autres)");
                        sb.AppendLine();
                    }

                    if (finalRenameErrors.Count > 0)
                    {
                        sb.AppendLine("Erreurs de renommage :");
                        foreach (var e in finalRenameErrors.Take(10)) sb.AppendLine(" - " + e);
                        if (finalRenameErrors.Count > 10)
                            sb.AppendLine($"… (+ {finalRenameErrors.Count - 10} autres)");
                        sb.AppendLine();
                    }

                    if (finalWarnings.Count > 0)
                    {
                        sb.AppendLine("Avertissements :");
                        foreach (var w in finalWarnings) sb.AppendLine(w);
                        sb.AppendLine();
                    }

                    sb.AppendLine($"Total paramètres: {totalParams}  •  Utilisateur: {userParams}  •  Partagés: {sharedParams}  •  Intégrés: {builtinParams}");

                    TaskDialog.Show("BIMaestro - Traduction terminée", sb.ToString());
                }

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
            {
                TaskDialog.Show("Quota dépassé", AiClient.QuotaExceededMessage);
                return Result.Cancelled;
            }
            catch (AggregateException agg)
            {
                var flat = agg.Flatten().InnerExceptions.Select(e => e.Message).Distinct();
                TaskDialog.Show("Erreur IA", "Des erreurs réseau sont survenues :\n" + string.Join("\n", flat));
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Erreur", ex.Message);
                return Result.Failed;
            }
        }

        // ======== Helpers ========

        private class ParamItem
        {
            public FamilyParameter Param { get; set; }
            public string OriginalName { get; set; }
        }

        private static bool IsSharedSafe(FamilyParameter fp)
        {
            try { return fp.IsShared; } catch { return false; }
        }

        private static BuiltInParameter GetBuiltInParameterSafe(FamilyParameter fp)
        {
            try
            {
                if (fp?.Definition is InternalDefinition idef)
                    return idef.BuiltInParameter;
            }
            catch { }
            return BuiltInParameter.INVALID;
        }

        private static string NormalizeName(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            s = s.Replace("\r", " ").Replace("\n", " ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.EndsWith(".")) s = s.TrimEnd('.');
            return s;
        }

        private static string EnsureUniqueName(HashSet<string> existing, string desired)
        {
            if (string.IsNullOrWhiteSpace(desired)) return desired ?? "";
            if (!existing.Contains(desired)) return desired;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{desired} ({i})";
                if (!existing.Contains(candidate)) return candidate;
            }
            return desired + " (FR)";
        }

        private static IEnumerable<List<string>> Chunk(List<string> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }

        private static async Task<Dictionary<string, string>> TranslateBatchWithRetry(string jwt, List<string> batch)
        {
            int attempt = 0;
            Exception last = null;
            while (attempt < MaxRetriesPerBatch)
            {
                try
                {
                    return await TranslateBatch(jwt, batch).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    attempt++;
                    await Task.Delay(TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt)))
                              .ConfigureAwait(false);
                }
            }
            throw last ?? new Exception("Échec inconnu lors de la traduction du lot.");
        }

        private static async Task<Dictionary<string, string>> TranslateBatch(string jwt, List<string> batch)
        {
            // Prompt hyper strict : JSON uniquement, pas de Markdown, pas de texte autour
            var inputArray = JArray.FromObject(batch);
            string userPrompt =
                "Rôle: traducteur vers le FR.\n" +
                "Consigne: pour CHAQUE texte d’entrée, renvoie la traduction en français, " +
                "ou renvoie le même texte s’il est déjà en français.\n" +
                "Sortie: retourne UNIQUEMENT un objet JSON compact (pas de Markdown, pas d’explications, pas de texte autour), " +
                "dont chaque CLÉ est EXACTEMENT le texte d’entrée, et chaque VALEUR est la traduction FR. " +
                "Assure-toi que l’objet JSON est strictement valide et correctement échappé.\n\n" +
                "INPUT=" + inputArray.ToString(Formatting.None);

            // Appel IA (threadpool)
            JObject raw = AiClient.SendOpenAI(jwt, Model, userPrompt);

            // Contenu (peut contenir du bruit, on extrait l'objet JSON par équilibrage d'accolades)
            string content = raw["choices"]?[0]?["message"]?["content"]?.ToString() ?? "{}";

            string jsonObject = ExtractBalancedJsonObject(content);
            if (string.IsNullOrEmpty(jsonObject))
            {
                string trimmed = StripCodeFences(content);
                jsonObject = ExtractBalancedJsonObject(trimmed);
            }

            if (string.IsNullOrEmpty(jsonObject))
            {
                string preview = content;
                if (preview.Length > 200) preview = preview.Substring(0, 200) + "…";
                throw new Exception("Réponse IA non JSON.\nAperçu: " + preview);
            }

            Dictionary<string, string> dict;
            try
            {
                dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonObject);
            }
            catch (Exception ex)
            {
                string preview = jsonObject;
                if (preview.Length > 200) preview = preview.Substring(0, 200) + "…";
                throw new Exception("JSON IA illisible: " + ex.Message + "\nJSON: " + preview);
            }

            if (dict == null) throw new Exception("JSON IA vide.");
            return await Task.FromResult(dict);
        }

        // Extrait le 1er objet JSON équilibré { ... } par comptage d'accolades
        private static string ExtractBalancedJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int start = -1;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth > 0) depth--;
                    if (depth == 0 && start >= 0)
                    {
                        int end = i;
                        return text.Substring(start, end - start + 1);
                    }
                }
            }
            return null;
        }

        private static string StripCodeFences(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int idx = s.IndexOf('\n');
                if (idx > 0) s = s.Substring(idx + 1);
                if (s.EndsWith("```"))
                {
                    int last = s.LastIndexOf("```", StringComparison.Ordinal);
                    if (last >= 0) s = s.Substring(0, last);
                }
            }
            return s.Trim();
        }

        private static Dictionary<string, string> LoadCache()
        {
            try
            {
                string path = GetCachePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    return d ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TranslateCache] Load error: " + ex.Message);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveCache(Dictionary<string, string> cache)
        {
            try
            {
                string path = GetCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(cache, Formatting.None), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TranslateCache] Save error: " + ex.Message);
            }
        }

        private static string GetCachePath()
        {
            string MyDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dir = Path.Combine(MyDocuments, "RevitLogs", "SauvegardePréférence");
            return Path.Combine(dir, "translation_cache.json");
        }
    }
}
