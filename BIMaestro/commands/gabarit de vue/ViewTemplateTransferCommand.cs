using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMaestro.ViewTemplates
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ViewTemplateTransferCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ViewTemplateTransfer";

        protected override Result OnExecute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = data.Application.ActiveUIDocument;
            Document document = uiDocument?.Document;
            View activeView = uiDocument?.ActiveView;
            if (document == null || activeView == null)
            {
                TaskDialog.Show("BIMaestro", UiLanguage.T("Aucun projet Revit actif.", "No Active Revit Project."));
                return Result.Cancelled;
            }
            if (document.IsFamilyDocument)
            {
                TaskDialog.Show(
                    UiLanguage.T("Gabarit de vue", "View Template"),
                    UiLanguage.T(
                        "Cette commande s’utilise dans un projet Revit (.rvt), pas dans une famille (.rfa).",
                        "This Command Is Available in a Revit Project (.rvt), Not in a Family (.rfa)."));
                return Result.Cancelled;
            }

            var dialog = new TaskDialog(UiLanguage.T("Transfert de gabarit de vue", "View Template Transfer"))
            {
                MainInstruction = UiLanguage.T(
                    "Exporter ou importer les réglages complets d’une vue",
                    "Export or Import Complete View Settings"),
                MainContent = UiLanguage.T(
                    "Le fichier BIMaestro est indépendant du projet et des identifiants internes Revit.",
                    "The BIMaestro File Is Independent of the Project and Revit Internal IDs."),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                UiLanguage.T("Exporter la vue active", "Export Active View"),
                UiLanguage.T("Enregistrer ses paramètres, graphismes, catégories et sous-projets.", "Save Its Parameters, Graphics, Categories, and Worksets."));
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.T("Importer dans la vue active", "Import into Active View"),
                UiLanguage.T("Créer un vrai gabarit nommé ou appliquer les graphismes directement à la vue.", "Create a Named View Template or Apply Graphics Directly to the View."));

            TaskDialogResult choice = dialog.Show();
            if (choice == TaskDialogResult.CommandLink1)
                return Export(data, document, activeView);
            if (choice == TaskDialogResult.CommandLink2)
                return Import(document, activeView);
            return Result.Cancelled;
        }

        private static Result Export(ExternalCommandData data, Document document, View activeView)
        {
            if (!CanUseView(activeView)) return ShowUnsupportedView();

            var filterDialog = new TaskDialog(UiLanguage.T("Options d’export", "Export Options"))
            {
                MainInstruction = UiLanguage.T(
                    "Inclure la configuration des filtres ?",
                    "Include Filter Configuration?"),
                MainContent = UiLanguage.T(
                    "Les règles, catégories, visibilité, activation et surcharges graphiques des filtres peuvent être transportées avec le gabarit.",
                    "Filter Rules, Categories, Visibility, Enabled State, and Graphic Overrides Can Be Transferred with the Template."),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            filterDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                UiLanguage.T("Oui, exporter les filtres", "Yes, Export Filters"));
            filterDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.T("Non, conserver les filtres du projet cible", "No, Keep Target Project Filters"));
            TaskDialogResult filterChoice = filterDialog.Show();
            if (filterChoice != TaskDialogResult.CommandLink1 && filterChoice != TaskDialogResult.CommandLink2)
                return Result.Cancelled;

            bool includeFilters = filterChoice == TaskDialogResult.CommandLink1;
            ViewTemplatePackage package = ViewTemplateTransferService.Capture(
                document,
                activeView,
                includeFilters,
                data.Application.Application.VersionNumber);

            var saveDialog = new SaveFileDialog
            {
                Title = UiLanguage.T("Exporter le gabarit de vue", "Export View Template"),
                Filter = "Gabarit de vue BIMaestro (*.bimaestro-view.json)|*.bimaestro-view.json|JSON (*.json)|*.json",
                AddExtension = true,
                DefaultExt = ".bimaestro-view.json",
                FileName = SanitizeFileName(package.SuggestedName) + ".bimaestro-view.json"
            };
            if (saveDialog.ShowDialog() != true) return Result.Cancelled;

            File.WriteAllText(
                saveDialog.FileName,
                JsonConvert.SerializeObject(package, Formatting.Indented),
                new UTF8Encoding(false));

            TaskDialog.Show(
                UiLanguage.T("Export terminé", "Export Complete"),
                UiLanguage.T(
                    "Le gabarit a été exporté avec " + package.Parameters.Count + " paramètre(s), " +
                    package.Categories.Count + " catégorie(s) et " + package.Filters.Count + " filtre(s).\n\n" + saveDialog.FileName,
                    "The Template Was Exported with " + package.Parameters.Count + " Parameter(s), " +
                    package.Categories.Count + " Category/Categories, and " + package.Filters.Count + " Filter(s).\n\n" + saveDialog.FileName));
            return Result.Succeeded;
        }

        private static Result Import(Document document, View activeView)
        {
            if (!CanUseView(activeView)) return ShowUnsupportedView();

            var openDialog = new OpenFileDialog
            {
                Title = UiLanguage.T("Importer un gabarit de vue", "Import View Template"),
                Filter = "Gabarit de vue BIMaestro (*.bimaestro-view.json;*.json)|*.bimaestro-view.json;*.json|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (openDialog.ShowDialog() != true) return Result.Cancelled;

            ViewTemplatePackage package;
            try
            {
                package = JsonConvert.DeserializeObject<ViewTemplatePackage>(File.ReadAllText(openDialog.FileName, Encoding.UTF8));
                if (package == null || !string.Equals(package.Format, "BIMaestro.ViewTemplate", StringComparison.Ordinal))
                    throw new InvalidDataException(UiLanguage.T("Format de fichier non reconnu.", "Unrecognized File Format."));
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    UiLanguage.T("Import impossible", "Import Failed"),
                    UiLanguage.T("Le fichier ne peut pas être lu : ", "The File Cannot Be Read: ") + ex.Message);
                return Result.Failed;
            }

            var modeDialog = new TaskDialog(UiLanguage.T("Mode d’import", "Import Mode"))
            {
                MainInstruction = UiLanguage.T("Comment appliquer ce fichier ?", "How Should This File Be Applied?"),
                MainContent = UiLanguage.T(
                    "Source : " + package.SourceView +
                    (string.IsNullOrWhiteSpace(package.SourceTemplate) ? string.Empty : "\nGabarit source : " + package.SourceTemplate) +
                    "\nType : " + package.ViewType +
                    "\nFiltres inclus : " + (package.IncludesFilters ? "oui" : "non"),
                    "Source: " + package.SourceView +
                    (string.IsNullOrWhiteSpace(package.SourceTemplate) ? string.Empty : "\nSource Template: " + package.SourceTemplate) +
                    "\nType: " + package.ViewType +
                    "\nFilters Included: " + (package.IncludesFilters ? "Yes" : "No")),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            modeDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                UiLanguage.T("Créer / mettre à jour un vrai gabarit nommé", "Create / Update a Named View Template"),
                UiLanguage.T("Le gabarit sera enregistré dans le projet puis affecté à la vue active.", "The Template Will Be Saved in the Project and Assigned to the Active View."));
            modeDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.T("Personnaliser uniquement la vue active", "Customize Only the Active View"),
                UiLanguage.T("Le gabarit actuel sera détaché et les réglages deviendront propres à cette vue.", "The Current Template Will Be Detached and the Settings Will Become Specific to This View."));
            TaskDialogResult mode = modeDialog.Show();
            if (mode != TaskDialogResult.CommandLink1 && mode != TaskDialogResult.CommandLink2)
                return Result.Cancelled;

            bool createTemplate = mode == TaskDialogResult.CommandLink1;
            string templateName = package.SuggestedName;
            if (createTemplate)
            {
                templateName = Microsoft.VisualBasic.Interaction.InputBox(
                    UiLanguage.T("Nom du gabarit Revit à créer ou mettre à jour :", "Name of the Revit View Template to Create or Update:"),
                    UiLanguage.T("Nom du gabarit", "Template Name"),
                    string.IsNullOrWhiteSpace(package.SuggestedName) ? "Gabarit BIMaestro" : package.SuggestedName);
                if (string.IsNullOrWhiteSpace(templateName)) return Result.Cancelled;
            }

            ViewTemplateImportReport report;
            try
            {
                report = ViewTemplateTransferService.Apply(
                    document,
                    activeView,
                    package,
                    createTemplate,
                    templateName);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    UiLanguage.T("Import impossible", "Import Failed"),
                    ex.Message);
                return Result.Failed;
            }

            var summary = new StringBuilder();
            summary.AppendLine(createTemplate
                ? UiLanguage.T("Gabarit enregistré et affecté : ", "Template Saved and Assigned: ") + report.TargetName
                : UiLanguage.T("Vue personnalisée : ", "View Customized: ") + report.TargetName);
            summary.AppendLine();
            summary.AppendLine(UiLanguage.T("Paramètres appliqués : ", "Parameters Applied: ") + report.ParametersApplied);
            summary.AppendLine(UiLanguage.T("Catégories appliquées : ", "Categories Applied: ") + report.CategoriesApplied);
            summary.AppendLine(UiLanguage.T("Sous-projets appliqués : ", "Worksets Applied: ") + report.WorksetsApplied);
            summary.AppendLine(UiLanguage.T("Filtres appliqués : ", "Filters Applied: ") + report.FiltersApplied);
            if (report.Warnings.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine(UiLanguage.T(
                    "Éléments ignorés car absents ou incompatibles dans ce projet :",
                    "Items Skipped Because They Are Missing or Incompatible in This Project:"));
                foreach (string warning in report.Warnings.Take(8)) summary.AppendLine("• " + warning);
                if (report.Warnings.Count > 8)
                    summary.AppendLine("• … +" + (report.Warnings.Count - 8) + UiLanguage.T(" autre(s)", " More"));
            }

            TaskDialog.Show(UiLanguage.T("Import terminé", "Import Complete"), summary.ToString());
            return Result.Succeeded;
        }

        private static bool CanUseView(View view)
        {
            if (view == null) return false;
            if (view.IsTemplate) return true;
            try
            {
                return view.AreGraphicsOverridesAllowed() || view.IsViewValidForTemplateCreation();
            }
            catch
            {
                return false;
            }
        }

        private static Result ShowUnsupportedView()
        {
            TaskDialog.Show(
                UiLanguage.T("Vue non compatible", "Unsupported View"),
                UiLanguage.T(
                    "Cette vue Revit ne prend pas en charge les gabarits ou les personnalisations graphiques. Ouvrez une vue de plan, coupe, élévation, 3D, dessin ou nomenclature.",
                    "This Revit View Does Not Support Templates or Graphic Customization. Open a Plan, Section, Elevation, 3D, Drafting, or Schedule View."));
            return Result.Cancelled;
        }

        private static string SanitizeFileName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "Gabarit de vue" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return name;
        }
    }
}
