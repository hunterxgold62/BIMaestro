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
            BIMaestro.Tutorials.DemoViewTemplateGuide.OnCommandOpened(document);

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

            TaskDialogResult choice = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(data.Application.MainWindowHandle, "Gabarit de vue",
                    BIMaestro.Tutorials.DemoViewTemplateGuide.IsImportStage(document) ? "La cible est bleue. Importe maintenant les réglages rouges de la source." : "La source est rouge et pointillée. Exporte ses réglages pour les transférer.",
                    "Exporter la vue active", "Importer dans la vue active",
                    BIMaestro.Tutorials.DemoViewTemplateGuide.IsImportStage(document) ? 2 : 1)
                : dialog.Show();
            if (choice == TaskDialogResult.CommandLink1)
                return Export(data, document, activeView);
            if (choice == TaskDialogResult.CommandLink2)
                return Import(data.Application, document, activeView);
            return Result.Cancelled;
        }

        private static TaskDialogResult GuidedChoice(IntPtr owner, string title, string instruction,
            string first, string second, int recommended)
        {
            var window = new System.Windows.Window { Title = "BIMaestro — " + title, Width = 560,
                SizeToContent = System.Windows.SizeToContent.Height, ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.White };
            new System.Windows.Interop.WindowInteropHelper(window).Owner = owner;
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(22) };
            window.Content = panel;
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = title, FontSize = 22,
                Foreground = BIMaestro.Tutorials.DemoTourPalette.Accent });
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = instruction,
                TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 12, 0, 16) });
            TaskDialogResult result = TaskDialogResult.Cancel;
            for (int i = 1; i <= 2; i++)
            {
                int selected = i;
                var button = new System.Windows.Controls.Button { Content = i == 1 ? first : second,
                    Padding = new System.Windows.Thickness(14), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new System.Windows.Thickness(0, 5, 0, 5), Background = System.Windows.Media.Brushes.White,
                    Foreground = BIMaestro.Tutorials.DemoTourPalette.Accent,
                    BorderBrush = i == recommended ? BIMaestro.Tutorials.DemoTourPalette.Accent : System.Windows.Media.Brushes.LightGray,
                    BorderThickness = new System.Windows.Thickness(i == recommended ? 3 : 1) };
                button.Click += (_, __) => { result = selected == 1 ? TaskDialogResult.CommandLink1 : TaskDialogResult.CommandLink2; window.Close(); };
                panel.Children.Add(button);
            }
            var cancel = new System.Windows.Controls.Button { Content = "Annuler", Padding = new System.Windows.Thickness(10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
            cancel.Click += (_, __) => window.Close();
            panel.Children.Add(cancel);
            window.ShowDialog();
            return result;
        }

        private static string GuidedName(IntPtr owner, string suggested)
        {
            var window = new System.Windows.Window { Title = "BIMaestro — Nom du gabarit", Width = 500,
                SizeToContent = System.Windows.SizeToContent.Height, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, ResizeMode = System.Windows.ResizeMode.NoResize, Background = System.Windows.Media.Brushes.White };
            new System.Windows.Interop.WindowInteropHelper(window).Owner = owner;
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(22) };
            window.Content = panel;
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Donne un nom au gabarit", FontSize = 20,
                Foreground = BIMaestro.Tutorials.DemoTourPalette.Accent });
            var input = new System.Windows.Controls.TextBox { Text = suggested, Padding = new System.Windows.Thickness(10),
                Margin = new System.Windows.Thickness(0, 14, 0, 14), BorderBrush = BIMaestro.Tutorials.DemoTourPalette.Accent,
                BorderThickness = new System.Windows.Thickness(3) };
            panel.Children.Add(input);
            string result = null;
            var apply = new System.Windows.Controls.Button { Content = "Créer et affecter à la cible", Padding = new System.Windows.Thickness(12),
                Background = BIMaestro.Tutorials.DemoTourPalette.Accent, Foreground = System.Windows.Media.Brushes.White };
            apply.Click += (_, __) => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); window.Close(); } };
            panel.Children.Add(apply);
            window.ShowDialog();
            return result;
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
            TaskDialogResult filterChoice = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(data.Application.MainWindowHandle, "Options d’export", "Choisis l’option encadrée pour inclure aussi les filtres dans le fichier.",
                    "Oui, exporter les filtres", "Non, conserver les filtres du projet cible", 1)
                : filterDialog.Show();
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
            BIMaestro.Tutorials.DemoViewTemplateGuide.OnExportCompleted(data.Application, saveDialog.FileName);
            return Result.Succeeded;
        }

        private static Result Import(UIApplication app, Document document, View activeView)
        {
            if (!CanUseView(activeView)) return ShowUnsupportedView();

            var openDialog = new OpenFileDialog
            {
                Title = UiLanguage.T("Importer un gabarit de vue", "Import View Template"),
                Filter = "Gabarit de vue BIMaestro (*.bimaestro-view.json;*.json)|*.bimaestro-view.json;*.json|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            string guidedFile = BIMaestro.Tutorials.DemoViewTemplateGuide.ExportedFileFor(document);
            if (!string.IsNullOrWhiteSpace(guidedFile) && File.Exists(guidedFile))
            {
                openDialog.InitialDirectory = Path.GetDirectoryName(guidedFile);
                openDialog.FileName = Path.GetFileName(guidedFile);
            }
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
            TaskDialogResult mode = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(app.MainWindowHandle, "Mode d’import", "Choisis l’option encadrée : le gabarit sera enregistré dans le projet et affecté à la vue cible. Ses couleurs et ses traits doivent remplacer le style bleu.",
                    "Créer / mettre à jour un vrai gabarit nommé", "Personnaliser uniquement la vue active", 1)
                : modeDialog.Show();
            if (mode != TaskDialogResult.CommandLink1 && mode != TaskDialogResult.CommandLink2)
                return Result.Cancelled;

            bool createTemplate = mode == TaskDialogResult.CommandLink1;
            string templateName = package.SuggestedName;
            if (createTemplate)
            {
                templateName = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                    ? GuidedName(app.MainWindowHandle, "BIMaestro - Démo rouge pointillée")
                    : Microsoft.VisualBasic.Interaction.InputBox(
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
            BIMaestro.Tutorials.DemoViewTemplateGuide.OnImportCompleted(document, app.MainWindowHandle);
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
