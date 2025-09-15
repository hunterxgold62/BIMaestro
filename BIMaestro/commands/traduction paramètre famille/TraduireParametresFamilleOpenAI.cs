using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;                
using System;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Linq;



namespace Famille
{
    [Transaction(TransactionMode.Manual)]
    public class TraduireParametresFamilleOpenAI : BaseTrackedCommand
    {

        protected override string ButtonId => "TraduireParametresFamilleOpenAI";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("Erreur",
                    "Ouvrez une famille avant d’exécuter ce plugin.");
                return Result.Cancelled;
            }

            // 1) Récupère le JWT de licence
            // Remplacez ces deux lignes par votre logique réelle
            string jwt = App.LicenseJwt;

            // 2) Parcours des paramètres
            var familyManager = doc.FamilyManager;
            var fParams = familyManager.GetParameters();
            var parametresTraduits = new Dictionary<FamilyParameter, string>();

            foreach (var fParam in fParams)
            {
                if (fParam?.Definition == null) continue;
                string originalName = fParam.Definition.Name;
                if (string.IsNullOrWhiteSpace(originalName)) continue;

                // Ne traduire que les paramètres utilisateur
                if (fParam.IsShared) continue;
                if (fParam.Definition is InternalDefinition def
                    && def.BuiltInParameter != BuiltInParameter.INVALID)
                    continue;

                // 3) Appel synchrone à l’IA via AiClient
                string prompt =
                  $"Le texte suivant est soit déjà en français, soit dans une autre langue. " +
                  $"Si c’est déjà du français, renvoie-le tel quel. Sinon, traduis-le. " +
                  $"Ne renvoie que le texte final, sans guillemets ni texte superflu.\n\n" +
                  $"Texte : {originalName}";

                string traduit;
                try
                {
                    // Envoie le prompt et récupère la réponse
                    JObject json = AiClient.SendOpenAI(jwt, "gpt-4o-mini", prompt);
                    traduit = json["choices"]?[0]?["message"]?["content"]?.ToString() ?? originalName;
                }
               catch (InvalidOperationException ex) when (ex.Message == AiClient.QuotaExceededMessage)
                {
                    // Quota dépassé → on arrête tout et on informe l'utilisateur
                    TaskDialog.Show(
                        "Quota dépassé",
                        AiClient.QuotaExceededMessage
                    );
                    return Result.Cancelled;
                }
                catch (Exception ex)
                {
                    // Autre erreur IA → on garde le nom original et on affiche
                    traduit = originalName;
                    TaskDialog.Show("Erreur IA", ex.Message);
                }

                if (!string.Equals(traduit, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    parametresTraduits[fParam] = traduit;
                }
            }

            // 4) Renommage des paramètres
            if (parametresTraduits.Count > 0)
            {
                using (var tx = new Transaction(doc, "Traduire paramètres via OpenAI"))
                {
                    tx.Start();
                    foreach (var kvp in parametresTraduits)
                    {
                        try
                        {
                            familyManager.RenameParameter(kvp.Key, kvp.Value);
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Erreur",
                                $"Impossible de renommer '{kvp.Key.Definition.Name}' en '{kvp.Value}' : {ex.Message}");
                        }
                    }
                    tx.Commit();
                }

                TaskDialog.Show("Terminé", "La traduction des paramètres a été effectuée avec succès.");
            }
            else
            {
                TaskDialog.Show("Information", "Aucun paramètre à traduire.");
            }

            return Result.Succeeded;
        }
    }
}
