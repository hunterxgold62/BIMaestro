using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System;
using Autodesk.Revit.DB;
using MyPluginNamespace;

[Transaction(TransactionMode.Manual)]
public class MiseAJourCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            // Crée et affiche la fenêtre de mise à jour
            var updateWindow = new UpdateWindow();
            updateWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Erreur", $"Une erreur s'est produite lors de l'exécution de la commande : {ex.Message}");
            return Result.Failed;
        }

        return Result.Succeeded;
    }
}