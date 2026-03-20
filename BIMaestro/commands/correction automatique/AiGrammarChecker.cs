using IA;
using Licensing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScanTextRevit
{
    public class AiGrammarChecker
    {
        private readonly string _jwt;
        private const int MAX_CHARS_PER_CHUNK = 3000;

        public AiGrammarChecker(string jwt)
        {
            _jwt = jwt;
        }

        /// <summary>
        /// Déclenché après chaque chunk, en fournissant la clé (feuille ou vue) 
        /// et la liste complète des corrections renvoyées par l'IA.
        /// </summary>
        public event Action<string, List<CorrectionItem>> ChunkProcessed;

        /// <summary>
        /// Déclenché lorsque tous les chunks ont été traités.
        /// </summary>
        public event Action OnAllChunksCompleted;

        /// <summary>
        /// Déclenché à chaque chunk pour mettre à jour la barre de progression (pourcentage 0-100).
        /// </summary>
        public event Action<double> ProgressUpdated;

        private AiGrammarCache _cache = new AiGrammarCache();

        /// <summary>
        /// Lance la vérification en découpant les textes en chunks, appelle l'IA, 
        /// puis déclenche ChunkProcessed et ProgressUpdated à chaque chunk.
        /// </summary>
        public async Task<Dictionary<string, List<CorrectionItem>>> CheckGrammarInChunksAsync(
            Dictionary<string, List<ScannedTextItem>> textsByViewSheet)
        {
            var finalResults = new Dictionary<string, List<CorrectionItem>>();
            var tasks = new List<Task>();

            // 1) Calcul du nombre total de chunks (pour la progression)
            int totalChunks = 0;
            foreach (var kvp in textsByViewSheet)
            {
                var scannedItems = kvp.Value ?? new List<ScannedTextItem>();
                totalChunks += SplitScannedTextsIntoChunks(scannedItems, MAX_CHARS_PER_CHUNK).Count;
            }
            int processedChunks = 0;

            // 2) Pour chaque vue/feuille
            foreach (var kvp in textsByViewSheet)
            {
                string key = kvp.Key;
                var scannedItems = kvp.Value ?? new List<ScannedTextItem>();

                // On crée la clé dans finalResults pour stocker le cumul final
                finalResults[key] = new List<CorrectionItem>();

                var task = Task.Run(async () =>
                {
                    // Découpage en chunks
                    var chunkedLists = SplitScannedTextsIntoChunks(scannedItems, MAX_CHARS_PER_CHUNK);

                    foreach (var chunk in chunkedLists)
                    {
                        // Préparation du prompt
                        var promptBuilder = new StringBuilder();
                        var linesArray = chunk
     .Select((item, idx) => new {
         LineNumber = idx + 1,
         Text = item.Text.Trim()
     })
     .ToList();
                        string linesJson = JsonConvert.SerializeObject(linesArray, Formatting.None);
                        string prompt = BuildPrompt(linesJson);
                        string promptHash = _cache.ComputeHash(prompt);

                        // 3) Appel à l'IA (avec cache)
                        List<CorrectionItem> corrections;
                        if (_cache.TryGet(promptHash, out corrections))
                        {
                            // On a déjà une réponse pour ce prompt
                        }
                        else
                        {
                            string aiResponse = await CallChatGptApiAsync(prompt);
                            corrections = ParseCorrectionsRobust(aiResponse, promptBuilder.ToString());
                            _cache.Add(promptHash, corrections);
                        }

                        // 4) Mapper LineNumber -> ElementId
                        for (int i = 0; i < corrections.Count; i++)
                        {
                            int lineNum = corrections[i].LineNumber;
                            if (lineNum >= 1 && lineNum <= chunk.Count)
                            {
                                corrections[i].ElementId = chunk[lineNum - 1].ElementId;
                            }
                        }

                        // 5) Réduction des faux positifs (espaces/ponctuation/étiquettes BIM).
                        var filteredCorrections = new List<CorrectionItem>();
                        foreach (var corr in corrections)
                        {
                            if (!string.IsNullOrEmpty(corr.OriginalText) && !string.IsNullOrEmpty(corr.CorrectedText))
                            {
                                var original = corr.OriginalText.Trim();
                                var corrected = corr.CorrectedText.Trim();

                                if (IsWhitespaceOnlyDifference(original, corrected) && IsLikelyLabelOrTag(original))
                                {
                                    // Cas bruyant: étiquettes/codes où l'IA ne fait qu'ajuster des espaces.
                                    continue;
                                }

                                if (IsFormattingOnlyDifference(original, corrected))
                                {
                                    corr.Category = "Mineur";
                                }
                            }

                            filteredCorrections.Add(corr);
                        }

                        // 6) On envoie toutes les corrections utiles au UI.
                        lock (finalResults)
                        {
                            finalResults[key].AddRange(filteredCorrections);
                        }

                        // On notifie la fenêtre (WPF) de ce chunk
                        ChunkProcessed?.Invoke(key, filteredCorrections);

                        // 7) Mise à jour de la progression
                        int current = System.Threading.Interlocked.Increment(ref processedChunks);
                        double percent = (double)current * 100 / totalChunks;
                        ProgressUpdated?.Invoke(percent);
                    }
                });
                tasks.Add(task);
            }

            // 8) Attente de tous les chunks
            await Task.WhenAll(tasks);

            // 9) Notification de fin
            OnAllChunksCompleted?.Invoke();
            return finalResults;
        }

        private string BuildPrompt(string linesJson)
        {
            return
              "Tu es un correcteur expert en français. Pour chaque objet du tableau JSON ci‑dessous, "
            + "fournis LineNumber, OriginalText, CorrectedText, Explanation, Category. "
            + "Ne signale PAS les micro-variantes de mise en forme (espaces en trop/en moins, doubles espaces, espace insécable, ponctuation cosmétique) sauf si le sens change réellement. "
            + "Pour les textes très courts ou de type étiquette/code (ex: 'Niv. 01', 'A-101', 'Lot CVC'), évite les corrections stylistiques et corrige uniquement les fautes évidentes qui nuisent à la lisibilité. "
            + "Si une correction est uniquement cosmétique, renvoie Category=Mineur. Si c'est une vraie faute de langue (grammaire/conjugaison/accord/sens), renvoie Category=Erreur. "
            + "Si aucun texte ne requiert de correction, réponds STRICTEMENT avec [] (tableau JSON vide).\n"
            + linesJson;
        }

        private List<List<ScannedTextItem>> SplitScannedTextsIntoChunks(List<ScannedTextItem> items, int maxChars)
        {
            var chunks = new List<List<ScannedTextItem>>();
            var currentChunk = new List<ScannedTextItem>();
            int currentSize = 0;

            foreach (var item in items)
            {
                // On découpe l'item en sous-phrases via ponctuation forte
                var sentences = Regex.Split(item.Text.Trim(),
                    @"(?<=[\.!\?])\s+", RegexOptions.Compiled);
                foreach (var sentence in sentences)
                {
                    if (sentence.Length == 0) continue;
                    // Si la phrase seule dépasse maxChars, on la force quand même
                    if (currentSize + sentence.Length + 1 > maxChars && currentChunk.Count > 0)
                    {
                        chunks.Add(new List<ScannedTextItem>(currentChunk));
                        currentChunk.Clear();
                        currentSize = 0;
                    }
                    currentChunk.Add(new ScannedTextItem
                    {
                        Text = sentence,
                        ElementId = item.ElementId
                    });
                    currentSize += sentence.Length + 1;
                }
            }
            if (currentChunk.Count > 0)
                chunks.Add(currentChunk);

            return chunks;
        }

        private async Task<string> CallChatGptApiAsync(string prompt)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var json = AiClient.SendOpenAI(_jwt, "gpt-4o-mini", prompt);
                    string messageContent = json["choices"]?[0]?["message"]?["content"]?.ToString();
                    return messageContent;
                }
                catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
                {
                    // Quota dépassé → on renvoie un CorrectionItem avec le message centralisé
                    var errorObj = new CorrectionItem
                    {
                        LineNumber = 0,
                        OriginalText = prompt,
                        CorrectedText = AiClient.QuotaExceededMessage,
                        Explanation = "",
                        Category = "Erreur"
                    };
                    var arr = new List<CorrectionItem> { errorObj };
                    return JsonConvert.SerializeObject(arr);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("AI proxy error (403)"))
                {
                    // Au cas où le proxy renvoie un autre message 403
                    var errorObj = new CorrectionItem
                    {
                        LineNumber = 0,
                        OriginalText = prompt,
                        CorrectedText = AiClient.QuotaExceededMessage,
                        Explanation = "",
                        Category = "Erreur"
                    };
                    var arr = new List<CorrectionItem> { errorObj };
                    return JsonConvert.SerializeObject(arr);
                }
                catch (Exception ex)
                {
                    // Toutes les autres erreurs
                    var errorObj = new CorrectionItem
                    {
                        LineNumber = 0,
                        OriginalText = prompt,
                        CorrectedText = $"Erreur API: {ex.Message}",
                        Explanation = "",
                        Category = "Erreur"
                    };
                    var arr = new List<CorrectionItem> { errorObj };
                    return JsonConvert.SerializeObject(arr);
                }
            });
        }



        private List<CorrectionItem> ParseCorrectionsRobust(string aiResponse, string originalTextIfError)
        {
            string cleaned = aiResponse?.Trim() ?? "";
            if (string.IsNullOrEmpty(cleaned))
            {
                return new List<CorrectionItem>
                {
                    new CorrectionItem
                    {
                        LineNumber = 0,
                        OriginalText = originalTextIfError,
                        CorrectedText = "Erreur lors de la désérialisation de la réponse de l'IA.",
                        Explanation = "Réponse vide ou nulle.",
                        Category = "Erreur"
                    }
                };
            }

            string validJson = ExtractValidJson(cleaned);
            try
            {
                var token = JToken.Parse(validJson);
                if (token.Type == JTokenType.Array)
                {
                    return token.ToObject<List<CorrectionItem>>();
                }
                else if (token.Type == JTokenType.Object)
                {
                    var single = token.ToObject<CorrectionItem>();
                    return new List<CorrectionItem> { single };
                }
            }
            catch (Exception ex)
            {
                return new List<CorrectionItem>
                {
                    new CorrectionItem
                    {
                        LineNumber = 0,
                        OriginalText = originalTextIfError,
                        CorrectedText = "Erreur lors de la désérialisation de la réponse de l'IA.",
                        Explanation = ex.Message,
                        Category = "Erreur"
                    }
                };
            }
            return new List<CorrectionItem>
            {
                new CorrectionItem
                {
                    LineNumber = 0,
                    OriginalText = originalTextIfError,
                    CorrectedText = "Erreur lors de la désérialisation de la réponse de l'IA.",
                    Explanation = "Format inattendu ou parsing impossible.",
                    Category = "Erreur"
                }
            };
        }

        private string ExtractValidJson(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input.Trim();

            // Cherche un bloc ```json ... ```
            var m = Regex.Match(input, @"```json\s*(\[\s*[\s\S]*\]|\{\s*[\s\S]*\})\s*```",
                                RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value.Trim();

            // Sinon retourne tout ce qui ressemble à un tableau JSON
            m = Regex.Match(input, @"(\[\s*[\s\S]*\])");
            if (m.Success)
                return m.Groups[1].Value.Trim();

            // Fallback : on renvoie l’input brut (puis JToken.Parse lèvera)
            return input.Trim();
        }


        private string RemovePunctuation(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return new string(input.Where(ch => !char.IsPunctuation(ch)).ToArray());
        }

        private bool IsFormattingOnlyDifference(string original, string corrected)
        {
            string NormalizeForCompare(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return string.Empty;

                var normalized = value.Normalize(NormalizationForm.FormKD)
                    .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    .ToArray();

                string noPunctuation = new string(normalized.Where(ch => !char.IsPunctuation(ch)).ToArray());
                string collapsedWhitespace = Regex.Replace(noPunctuation, @"\s+", " ").Trim();
                return collapsedWhitespace.ToLowerInvariant();
            }

            return NormalizeForCompare(original) == NormalizeForCompare(corrected);
        }

        private bool IsWhitespaceOnlyDifference(string original, string corrected)
        {
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(corrected))
                return false;

            string o = Regex.Replace(original, @"\s+", "").Trim();
            string c = Regex.Replace(corrected, @"\s+", "").Trim();
            return string.Equals(o, c, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(original, corrected, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLikelyLabelOrTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();

            if (trimmed.Length <= 18) return true;

            bool hasDigit = trimmed.Any(char.IsDigit);
            bool hasSeparator = trimmed.Any(ch => ch == '-' || ch == '_' || ch == '/' || ch == '.');
            bool fewWords = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length <= 3;

            return fewWords && (hasDigit || hasSeparator);
        }
    }
}