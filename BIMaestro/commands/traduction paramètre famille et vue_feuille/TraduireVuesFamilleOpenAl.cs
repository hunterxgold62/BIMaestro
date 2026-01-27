// TraduireVuesFamilleOpenAI.cs
// Revit 2023+ – Traduction batch rapide et robuste des vues et feuilles
// - Réutilise l'API OpenAI et le cache disque du traducteur de paramètres
// - Même stratégie de batching, parallélisme et traitement d'erreurs

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
    public class TraduireVuesFamilleOpenAI : BaseTrackedCommand
    {
        protected override string ButtonId => "TraduireVuesFamilleOpenAI";

        private const string Model = "gpt-4o-mini";
        private const int BatchSize = 80;
        private const int MaxParallelBatches = 2;
        private const int MaxRetriesPerBatch = 3;
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiDoc = data.Application.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("Erreur", "Ouvrez un document Revit avant d’exécuter ce plugin.");
                return Result.Cancelled;
            }

            string jwt = BIMaestroApp.LicenseJwt;

            var finalWarnings = new List<string>();
            var finalRenameErrors = new List<string>();
            var finalRenamed = new List<(string OldName, string NewName)>();
            int totalViews = 0, renameableViews = 0, templateViews = 0, lockedViews = 0;
            int totalSheets = 0, renameableSheets = 0;

            try
            {
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v != null && v.ViewType != ViewType.Internal);

                var candidates = new List<View>();
                foreach (var view in allViews)
                {
                    totalViews++;
                    if (view is ViewSheet)
                        totalSheets++;

                    if (view.IsTemplate)
                    {
                        templateViews++;
                        continue;
                    }

                    var nameParam = view.get_Parameter(BuiltInParameter.VIEW_NAME);
                    bool canRename = nameParam != null && !nameParam.IsReadOnly;

                    if (canRename)
                    {
                        renameableViews++;
                        if (view is ViewSheet)
                            renameableSheets++;
                        candidates.Add(view);
                    }
                    else
                    {
                        lockedViews++;
                    }
                }

                if (candidates.Count == 0)
                {
                    TaskDialog.Show(
                        "BIMaestro - Résultat",
                        "Aucune vue ou feuille renommable détectée dans ce document.\n\n" +
                        $"Total vues/feuilles: {totalViews}  •  Renommables: {renameableViews}  •  Templates: {templateViews}  •  Verrouillées: {lockedViews}"
                    );
                    return Result.Succeeded;
                }

                var items = candidates
                    .Select(v => new ViewItem { View = v, OriginalName = v.Name })
                    .ToList();

                var cache = LoadCache();

                var uniqueNames = items.Select(i => i.OriginalName)
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                var toTranslate = uniqueNames.Where(n => !cache.ContainsKey(n)).ToList();

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

                var renameMap = new Dictionary<View, string>();
                foreach (var it in items)
                {
                    if (cache.TryGetValue(it.OriginalName, out string translated))
                    {
                        translated = NormalizeName(translated);
                        if (!string.IsNullOrEmpty(translated) &&
                            !translated.Equals(it.OriginalName, StringComparison.OrdinalIgnoreCase))
                        {
                            renameMap[it.View] = translated;
                        }
                    }
                }

                if (renameMap.Count == 0)
                {
                    TaskDialog.Show("BIMaestro - Résultat",
                        "Aucune traduction appliquée (tout est déjà en FR ou identique).\n\n" +
                        $"Vues/feuilles analysées: {items.Count}");
                    return Result.Succeeded;
                }

                using (var tx = new Transaction(doc, "Traduire vues via OpenAI (batch)"))
                {
                    tx.Start();

                    var existing = new HashSet<string>(
                        new FilteredElementCollector(doc)
                            .OfClass(typeof(View))
                            .Cast<View>()
                            .Select(v => v?.Name ?? ""),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in renameMap)
                    {
                        var view = kvp.Key;
                        var target = kvp.Value;

                        if (string.IsNullOrWhiteSpace(target))
                            continue;

                        string current = view.Name;
                        if (current.Equals(target, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string finalName = EnsureUniqueName(existing, target);

                        try
                        {
                            view.Name = finalName;
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

                var sb = new StringBuilder();
                sb.AppendLine($"Vues/feuilles renommables détectées : {items.Count}");
                sb.AppendLine($"Vues/feuilles traduites : {finalRenamed.Count}");
                sb.AppendLine();

                if (finalRenamed.Count > 0)
                {
                    sb.AppendLine("Renommages appliqués :");
                    foreach (var v in finalRenamed.Take(30))
                        sb.AppendLine($" - {v.OldName} → {v.NewName}");
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

                sb.AppendLine($"Total vues/feuilles: {totalViews}  •  Renommables: {renameableViews}  •  Templates: {templateViews}  •  Verrouillées: {lockedViews}");
                if (totalSheets > 0)
                {
                    sb.AppendLine($"Dont feuilles: {totalSheets} (renommables: {renameableSheets})");
                }

                TaskDialog.Show("BIMaestro - Traduction terminée", sb.ToString());

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

        private class ViewItem
        {
            public View View { get; set; }
            public string OriginalName { get; set; }
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
            var inputArray = JArray.FromObject(batch);
            string userPrompt =
                "Rôle: traducteur vers le FR.\n" +
                "Consigne: pour CHAQUE texte d’entrée, renvoie la traduction en français, " +
                "ou renvoie le même texte s’il est déjà en français.\n" +
                "Sortie: retourne UNIQUEMENT un objet JSON compact (pas de Markdown, pas d’explications, pas de texte autour), " +
                "dont chaque CLÉ est EXACTEMENT le texte d’entrée, et chaque VALEUR est la traduction FR. " +
                "Assure-toi que l’objet JSON est strictement valide et correctement échappé.\n\n" +
                "INPUT=" + inputArray.ToString(Formatting.None);

            JObject raw = AiClient.SendOpenAI(jwt, Model, userPrompt);

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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "BIMaestro");
            return Path.Combine(dir, "translation_cache.json");
        }
    }
}