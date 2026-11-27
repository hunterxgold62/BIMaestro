// TraduireVuesFamilleOpenAI.cs
// Revit 2023+ – Traduction batch rapide et robuste des vues et feuilles
// - Réutilise l'API OpenAI et le cache disque du traducteur de paramètres
// - Même stratégie de batching, parallélisme et traitement d'erreurs

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
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

        private const string Model = AiClient.TextModel;
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
                TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.Error"), UiLanguage.Get("TraduireVuesFamilleOpenAl.OpenARevitDocumentBeforeRunningThisPlugin"));
                return Result.Cancelled;
            }

            string jwt = BIMaestroApp.LicenseJwt;

            try
            {
                var o = TranslateViews(doc, jwt);
                int totalViews = o.Total, renameableViews = o.Renameable, templateViews = o.Templates, lockedViews = o.Locked;
                int totalSheets = o.Sheets, renameableSheets = o.RenameableSheets;

                if (o.Renameable == 0)
                {
                    TaskDialog.Show(
                        UiLanguage.Get("TraduireVuesFamilleOpenAl.BIMaestroResult"),
                        UiLanguage.Get("TraduireVuesFamilleOpenAl.NoRenameableViewOrSheetWasFoundIn") +
                        UiLanguage.Format("TraduireVuesFamilleOpenAl.TotalViewsSheets0Renameable1Templates2", (object)totalViews, (object)renameableViews, (object)templateViews, (object)lockedViews)
                    );
                    return Result.Succeeded;
                }

                if (o.Proposed == 0)
                {
                    TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.BIMaestroResult"),
                        UiLanguage.Get("TraduireVuesFamilleOpenAl.NoTranslationAppliedEverythingIsAlreadyInFrench") +
                        UiLanguage.Format("TraduireVuesFamilleOpenAl.ViewsSheetsAnalyzed0", (object)o.Renameable));
                    return Result.Succeeded;
                }

                var finalRenamed = o.Renamed;
                var finalRenameErrors = o.RenameErrors;
                var finalWarnings = o.Warnings;

                var sb = new StringBuilder();
                sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.RenameableViewsSheetsFound0", (object)o.Renameable));
                sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.ViewsSheetsTranslated0", (object)finalRenamed.Count));
                sb.AppendLine();

                if (finalRenamed.Count > 0)
                {
                    sb.AppendLine(UiLanguage.Get("TraduireVuesFamilleOpenAl.AppliedRenames"));
                    foreach (var v in finalRenamed.Take(30))
                        sb.AppendLine($" - {v.OldName} → {v.NewName}");
                    if (finalRenamed.Count > 30)
                        sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.0More", (object)(finalRenamed.Count - 30)));
                    sb.AppendLine();
                }

                if (finalRenameErrors.Count > 0)
                {
                    sb.AppendLine(UiLanguage.Get("TraduireVuesFamilleOpenAl.RenameErrors"));
                    foreach (var e in finalRenameErrors.Take(10)) sb.AppendLine(" - " + e);
                    if (finalRenameErrors.Count > 10)
                        sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.0More", (object)(finalRenameErrors.Count - 10)));
                    sb.AppendLine();
                }

                if (finalWarnings.Count > 0)
                {
                    sb.AppendLine(UiLanguage.Get("TraduireVuesFamilleOpenAl.Warnings"));
                    foreach (var w in finalWarnings) sb.AppendLine(w);
                    sb.AppendLine();
                }

                sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.TotalViewsSheets0Renameable1Templates2", (object)totalViews, (object)renameableViews, (object)templateViews, (object)lockedViews));
                if (totalSheets > 0)
                {
                    sb.AppendLine(UiLanguage.Format("TraduireVuesFamilleOpenAl.IncludingSheets0Renameable1", (object)totalSheets, (object)renameableSheets));
                }

                TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.BIMaestroTranslationCompleted"), sb.ToString());

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
            {
                TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.QuotaExceeded"), UiLanguage.T(AiClient.QuotaExceededMessage, "The AI quota has been exceeded."));
                return Result.Cancelled;
            }
            catch (AggregateException agg)
            {
                var flat = agg.Flatten().InnerExceptions.Select(e => e.Message).Distinct();
                TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.AIError"), UiLanguage.Get("TraduireVuesFamilleOpenAl.NetworkErrorsOccurred") + string.Join("\n", flat));
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(UiLanguage.Get("TraduireVuesFamilleOpenAl.Error"), ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Traduit les vues et feuilles renommables sans afficher de fenêtre (utilisé aussi par « Préparer la famille »).
        /// Les erreurs de quota et de réseau sont relancées à l'appelant.
        /// </summary>
        internal static FamilyTranslationOutcome TranslateViews(Document doc, string jwt)
        {
            var o = new FamilyTranslationOutcome();

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v != null && v.ViewType != ViewType.Internal);

            var candidates = new List<View>();
            foreach (var view in allViews)
            {
                o.Total++;
                if (view is ViewSheet)
                    o.Sheets++;

                if (view.IsTemplate)
                {
                    o.Templates++;
                    continue;
                }

                var nameParam = view.get_Parameter(BuiltInParameter.VIEW_NAME);
                bool canRename = nameParam != null && !nameParam.IsReadOnly;

                if (canRename)
                {
                    o.Renameable++;
                    if (view is ViewSheet)
                        o.RenameableSheets++;
                    candidates.Add(view);
                }
                else
                {
                    o.Locked++;
                }
            }

            if (candidates.Count == 0)
                return o;

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
                    o.Warnings.Add("Certains lots n’ont pas pu être traduits :");
                    foreach (var e in errorsBag.Take(10)) o.Warnings.Add(" - " + e);
                    if (errorsBag.Count > 10) o.Warnings.Add($"(+ {errorsBag.Count - 10} erreurs supplémentaires)");
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

            o.Proposed = renameMap.Count;
            if (renameMap.Count == 0)
                return o;

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
                        o.Renamed.Add((current, finalName));
                    }
                    catch (Exception ex)
                    {
                        o.RenameErrors.Add($"'{current}' → '{finalName}' : {ex.Message}");
                    }
                }

                tx.Commit();
            }

            return o;
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
