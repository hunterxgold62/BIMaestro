using System;
using System.Net.Http;
using System.Text;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System.Linq;
using IA;

namespace MonPluginRevit
{
    [Transaction(TransactionMode.Manual)]
    public class TraduireParametresFamilleOpenAI : IExternalCommand
    {
        public static string apiKey = ApiKeys.OpenAIKey;
        private readonly object openAIApiKey;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("Erreur", "Ouvrez une famille avant d’exécuter ce plugin.");
                return Result.Cancelled;
            }

            FamilyManager familyManager = doc.FamilyManager;
            IList<FamilyParameter> fParams = familyManager.GetParameters();

            Dictionary<FamilyParameter, string> parametresTraduits = new Dictionary<FamilyParameter, string>();

            foreach (FamilyParameter fParam in fParams)
            {
                if (fParam?.Definition == null)
                    continue;

                string originalName = fParam.Definition.Name;
                if (string.IsNullOrEmpty(originalName))
                    continue;

                // Vérifier si c'est un paramètre créé par l'utilisateur :
                // On cast la Definition en InternalDefinition pour accéder à BuiltInParameter
                InternalDefinition internalDef = fParam.Definition as InternalDefinition;
                if (internalDef == null)
                    continue;

                BuiltInParameter bip = internalDef.BuiltInParameter;

                // On ignore les partagés et les intégrés
                if (fParam.IsShared || bip != BuiltInParameter.INVALID)
                    continue;

                // Si on veut juste s'assurer que si c'est déjà en français on ne le modifie pas,
                // le prompt OpenAI fera ce travail.

                string traduit = TraduireTexteSync(originalName, "fr");
                // Si la traduction est identique à l'original, cela veut dire qu'il était déjà en français.
                // On peut décider si on le renomme ou pas. Ici, s'il ne change pas, on ne fait rien.
                if (!string.IsNullOrWhiteSpace(traduit) && traduit != originalName)
                {
                    parametresTraduits[fParam] = traduit.Trim();
                }
            }

            using (Transaction t = new Transaction(doc, "Traduire Paramètres via OpenAI"))
            {
                t.Start();
                foreach (var kvp in parametresTraduits)
                {
                    FamilyParameter fParam = kvp.Key;
                    string newName = kvp.Value;
                    try
                    {
                        familyManager.RenameParameter(fParam, newName);
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Erreur",
                            $"Impossible de renommer '{fParam.Definition.Name}' en '{newName}': {ex.Message}");
                    }
                }
                t.Commit();
            }

            TaskDialog.Show("Terminé", "La traduction des paramètres a été effectuée avec succès.");
            return Result.Succeeded;
        }

        private string TraduireTexteSync(string text, string targetLanguage)
        {
            // Nouveau prompt plus précis :
            // On demande d'abord de vérifier si le texte est déjà en français.
            // Si oui, le renvoyer tel quel, sinon le traduire.
            // Pas de guillemets, pas de ponctuation supplémentaire, juste le texte final.
            string prompt =
                $"Le texte ou mot suivant est soit déjà en français, soit dans une autre langue.\n" +
                $"Si le texte ou mot est déjà en français, renvoie-le tel quel.\n" +
                $"Sinon, traduis-le en français.\n" +
                $"Ne retourne que le texte final, sans guillemets, sans ponctuation superflue, sans préfixe, sans 'La traduction est:', ni 'Traduction:'. Juste le texte.\n\n" +
                $"Texte: {text}";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAIApiKey}");

                    var requestData = new
                    {
                        model = "gpt-4o-mini",
                        messages = new object[]
                        {
                            new { role = "system", content = "Tu es un assistant utile qui traduit le texte en français si nécessaire, sans ajouter de guillemets ni de texte superflu."},
                            new { role = "user", content = prompt }
                        },
                        max_tokens = 500,
                       
                    };

                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(requestData);
                    StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = client.PostAsync("https://api.openai.com/v1/chat/completions", content).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        // En cas de problème, on ne modifie pas le texte
                        return text;
                    }

                    string responseString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    JObject jsonResponse = JObject.Parse(responseString);
                    string translated = (string)jsonResponse["choices"]?[0]?["message"]?["content"];
                    if (translated == null)
                        return text;

                    // Nettoyage du résultat : on supprime les guillemets au cas où
                    translated = translated.Replace("\"", "").Trim();

                    return translated;
                }
            }
            catch
            {
                // En cas d'échec (timeout, etc.) on renvoie le texte original
                return text;
            }
        }
    }
}
