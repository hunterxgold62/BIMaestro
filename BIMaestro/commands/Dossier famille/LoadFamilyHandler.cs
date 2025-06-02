using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Linq;
using System.Windows;


namespace FamilyBrowserPlugin
{
    public class LoadFamilyHandler : IExternalEventHandler
    {
        public string FamilyPath { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                if (!System.IO.File.Exists(FamilyPath))
                {
                    MessageBox.Show(FamilyBrowserCommand.MainWindowRef, $"Le fichier de famille '{FamilyPath}' n'existe pas.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Document doc = app.ActiveUIDocument.Document;
                string familyName = System.IO.Path.GetFileNameWithoutExtension(FamilyPath);

                // Vérifier si une famille du même nom existe déjà
                Family existingFamily = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (existingFamily != null)
                {
                    var result = MessageBox.Show(FamilyBrowserCommand.MainWindowRef,
                        $"La famille '{familyName}' existe déjà dans le projet.\nVoulez-vous l'écraser ?",
                        "Famille déjà présente",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                    {
                        return;
                    }
                }

                using (Transaction trans = new Transaction(doc, "Charger la Famille"))
                {
                    trans.Start();
                    if (doc.LoadFamily(FamilyPath, new FamilyLoadOption(), out Family family))
                    {
                        MessageBox.Show(FamilyBrowserCommand.MainWindowRef, $"La famille '{family.Name}' a été chargée avec succès.", "Succès", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(FamilyBrowserCommand.MainWindowRef, $"Échec du chargement de la famille '{familyName}'.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    trans.Commit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(FamilyBrowserCommand.MainWindowRef, $"Une erreur s'est produite : {ex.Message}\n{ex.StackTrace}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public string GetName()
        {
            return "LoadFamilyHandler";
        }
    }
}
