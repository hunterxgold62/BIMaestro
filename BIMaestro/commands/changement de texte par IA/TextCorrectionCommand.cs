using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Windows.Interop;

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class TextCorrectionCommand : IExternalCommand
    {
        // Clé API OpenAI
        public static string apiKey = ApiKeys.OpenAIKey;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc.Document;

                // Sélection multiple
                IList<Reference> refs = uidoc.Selection.PickObjects(
                    Autodesk.Revit.UI.Selection.ObjectType.Element,
                    "Sélectionnez une ou plusieurs TextNote à corriger"
                );
                if (refs == null || refs.Count == 0)
                {
                    return Result.Cancelled;
                }

                foreach (var r in refs)
                {
                    TextNote tn = doc.GetElement(r) as TextNote;
                    if (tn == null) continue;

                    string originalText = tn.Text;
                    string baselineText = "";

                    // Correction orthographique initiale
                    try
                    {
                        baselineText = CorrectTextWithOpenAI(
                            originalText,
                            false,
                            "Classique",
                            ""
                        ).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Erreur => on passe à la suivante
                        continue;
                    }

                    // Ouvre la fenêtre
                    CorrectionWindow window = new CorrectionWindow(originalText, baselineText);
                    IntPtr handle = commandData.Application.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        new WindowInteropHelper(window).Owner = handle;
                    }

                    bool? result = window.ShowDialog();
                    if (window.CorrectionResult == CorrectionWindow.CorrectionDialogResult.OK)
                    {
                        // On applique
                        using (Transaction t = new Transaction(doc, "Appliquer Correction"))
                        {
                            t.Start();
                            tn.Text = window.CorrectedText;
                            t.Commit();
                        }
                    }
                    else
                    {
                        // Annuler => rien
                        continue;
                    }
                }

                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        // Correction orthographique initiale
        private async Task<string> CorrectTextWithOpenAI(
            string inputText,
            bool rephrase,
            string style,
            string customInstruction
        )
        {
            // On garde un prompt minimaliste : corrige sans ajouter
            string prompt = $"Corrige les fautes et redonne la phrase sans ajouter de nouvelles informations : {inputText}";

            var requestData = new
            {
                model = "gpt-4o-mini", // à adapter
                messages = new object[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = 200
            };

            string jsonRequest = JsonConvert.SerializeObject(requestData);

            using (HttpClient client = new HttpClient())
            {
                client.BaseAddress = new Uri("https://api.openai.com/");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("/v1/chat/completions", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                dynamic result = JsonConvert.DeserializeObject(responseContent);

                if (result == null || result.choices == null || result.choices.Count == 0)
                {
                    throw new Exception("Réponse vide ou incorrecte de l'API.");
                }

                string correctedText = result.choices[0].message.content?.ToString().Trim();
                if (string.IsNullOrEmpty(correctedText))
                {
                    throw new Exception("Le texte retourné par l'API est vide.");
                }

                correctedText = correctedText.Replace("Correction:", "").Trim();
                return correctedText;
            }
        }
    }
}