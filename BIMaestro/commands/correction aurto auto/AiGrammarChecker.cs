using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using IA;

namespace ScanTextRevit
{
    public class AiGrammarChecker
    {
        private readonly string _apiKey = ApiKeys.OpenAIKey;
        private readonly string _apiUrl = "https://api.openai.com/v1/chat/completions";
        private const int MAX_CHARS_PER_CHUNK = 3000;

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
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            promptBuilder.AppendLine($"{i + 1}. {chunk[i].Text.Trim()}");
                        }
                        string prompt = BuildPrompt(promptBuilder.ToString());
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

                        // 5) Forcer la catégorie "Mineur" si seule la ponctuation diffère
                        foreach (var corr in corrections)
                        {
                            if (!string.IsNullOrEmpty(corr.OriginalText) && !string.IsNullOrEmpty(corr.CorrectedText))
                            {
                                string origNoPunct = RemovePunctuation(corr.OriginalText).Trim();
                                string corrNoPunct = RemovePunctuation(corr.CorrectedText).Trim();
                                if (string.Equals(origNoPunct, corrNoPunct, StringComparison.OrdinalIgnoreCase))
                                {
                                    corr.Category = "Mineur";
                                }
                            }
                        }

                        // 6) On envoie TOUTES les corrections au UI,
                        //    c'est la fenêtre qui se chargera de filtrer 
                        //    (afin de pouvoir afficher "Aucune erreur détectée" si besoin).
                        lock (finalResults)
                        {
                            finalResults[key].AddRange(corrections);
                        }

                        // On notifie la fenêtre (WPF) de ce chunk
                        ChunkProcessed?.Invoke(key, corrections);

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

        private string BuildPrompt(string chunkedTexts)
        {
            return "Tu es un correcteur orthographique et grammatical expert en français. Corrige les fautes dans les textes suivants. " +
                   "Pour chaque ligne, renvoie un objet JSON contenant : " +
                   "LineNumber (numéro de ligne, commençant à 1), OriginalText, CorrectedText, Explanation, et Category ('Mineur' si seule la ponctuation ou les espaces ont été modifiés, 'Erreur' sinon). " +
                   "Réponds uniquement avec un tableau JSON ayant exactement le même nombre d'objets que de lignes. " +
                   "Voici les textes (chaque ligne préfixée par son numéro) :\n" +
                   chunkedTexts;
        }

        private List<List<ScannedTextItem>> SplitScannedTextsIntoChunks(List<ScannedTextItem> items, int maxChars)
        {
            var chunks = new List<List<ScannedTextItem>>();
            var currentChunk = new List<ScannedTextItem>();
            int currentSize = 0;
            foreach (var item in items)
            {
                // +1 pour le saut de ligne potentiel
                if (currentSize + item.Text.Length + 1 > maxChars && currentChunk.Count > 0)
                {
                    chunks.Add(new List<ScannedTextItem>(currentChunk));
                    currentChunk.Clear();
                    currentSize = 0;
                }
                currentChunk.Add(item);
                currentSize += item.Text.Length + 1;
            }
            if (currentChunk.Count > 0)
                chunks.Add(currentChunk);

            return chunks;
        }

        private async Task<string> CallChatGptApiAsync(string prompt)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                var requestBody = new
                {
                    model = "gpt-4o-mini",
                    messages = new object[]
                    {
                        new { role = "system", content = "Tu es un expert en correction grammaticale et orthographique du français." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0
                };

                string jsonBody = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    HttpResponseMessage response = await client.PostAsync(_apiUrl, content, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return $"[{{ \"LineNumber\": 0, \"OriginalText\": \"{prompt}\", \"CorrectedText\": \"Erreur API: {response.StatusCode}\", \"Explanation\": \"\" }}]";
                    }
                    string responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        dynamic jsonResponse = JsonConvert.DeserializeObject(responseString);
                        string messageContent = jsonResponse.choices[0].message.content;
                        return messageContent;
                    }
                    catch (Exception ex)
                    {
                        return $"[{{ \"LineNumber\": 0, \"OriginalText\": \"{prompt}\", \"CorrectedText\": \"Erreur lors de l'analyse de la réponse de l'API.\", \"Explanation\": \"{ex.Message}\" }}]";
                    }
                }
                catch (Exception ex)
                {
                    return $"[{{ \"LineNumber\": 0, \"OriginalText\": \"{prompt}\", \"CorrectedText\": \"Erreur lors de l'appel à l'API: {ex.Message}\", \"Explanation\": \"\" }}]";
                }
            }
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
            if (string.IsNullOrWhiteSpace(input))
                return input;
            input = input.Trim();

            // Si la réponse commence par ```json
            if (input.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                int startBlock = input.IndexOf("```json", StringComparison.OrdinalIgnoreCase) + "```json".Length;
                int endBlock = input.IndexOf("```", startBlock);
                if (endBlock > startBlock)
                {
                    input = input.Substring(startBlock, endBlock - startBlock).Trim();
                }
            }

            // Retrait progressif de la fin jusqu'à obtenir du JSON valide
            while (!string.IsNullOrEmpty(input))
            {
                try
                {
                    JToken.Parse(input);
                    return input;
                }
                catch (JsonReaderException)
                {
                    input = input.Substring(0, input.Length - 1).Trim();
                }
            }
            return input;
        }

        private string RemovePunctuation(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return new string(input.Where(ch => !char.IsPunctuation(ch)).ToArray());
        }
    }
}