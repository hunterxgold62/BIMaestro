using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
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
                TaskDialog.Show("BIMaestro", UiLanguage.Get("ViewTemplateTransferCommand.NoActiveRevitProject"));
                return Result.Cancelled;
            }
            if (document.IsFamilyDocument)
            {
                TaskDialog.Show(
                    UiLanguage.Get("ViewTemplateTransferCommand.ViewTemplate"),
                    UiLanguage.Get("ViewTemplateTransferCommand.ThisCommandIsAvailableInARevitProject"));
                return Result.Cancelled;
            }
            BIMaestro.Tutorials.DemoViewTemplateGuide.OnCommandOpened(document);

            var dialog = new TaskDialog(UiLanguage.Get("ViewTemplateTransferCommand.ViewTemplateTransfer"))
            {
                MainInstruction = UiLanguage.Get("ViewTemplateTransferCommand.ExportOrImportCompleteViewSettings"),
                MainContent = UiLanguage.Get("ViewTemplateTransferCommand.TheBIMaestroFileIsIndependentOfTheProject"),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                UiLanguage.Get("ViewTemplateTransferCommand.ExportActiveView"),
                UiLanguage.Get("ViewTemplateTransferCommand.SaveItsParametersGraphicsCategoriesAndWorksets"));
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.Get("ViewTemplateTransferCommand.ImportIntoActiveView"),
                UiLanguage.Get("ViewTemplateTransferCommand.CreateANamedViewTemplateOrApplyGraphics"));

            TaskDialogResult choice = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(data.Application.MainWindowHandle, UiLanguage.Get("ViewTemplateTransferCommand.ViewTemplate"),
                    BIMaestro.Tutorials.DemoViewTemplateGuide.IsImportStage(document) ? UiLanguage.Get("ViewTemplateTransferCommand.TheTargetIsBlueNowImportTheRed") : UiLanguage.Get("ViewTemplateTransferCommand.TheSourceIsRedAndDashedExportIts"),
                    UiLanguage.Get("ViewTemplateTransferCommand.ExportActiveView"), UiLanguage.Get("ViewTemplateTransferCommand.ImportIntoActiveView"),
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
            var cancel = new System.Windows.Controls.Button { Content = UiLanguage.Get("ViewTemplateTransferCommand.Cancel"), Padding = new System.Windows.Thickness(10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
            cancel.Click += (_, __) => window.Close();
            panel.Children.Add(cancel);
            window.ShowDialog();
            return result;
        }

        private static string GuidedName(IntPtr owner, string suggested)
        {
            var window = new System.Windows.Window { Title = "BIMaestro — " + UiLanguage.Get("ViewTemplateTransferCommand.TemplateName"), Width = 500,
                SizeToContent = System.Windows.SizeToContent.Height, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, ResizeMode = System.Windows.ResizeMode.NoResize, Background = System.Windows.Media.Brushes.White };
            new System.Windows.Interop.WindowInteropHelper(window).Owner = owner;
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(22) };
            window.Content = panel;
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = UiLanguage.Get("ViewTemplateTransferCommand.NameTheTemplate"), FontSize = 20,
                Foreground = BIMaestro.Tutorials.DemoTourPalette.Accent });
            var input = new System.Windows.Controls.TextBox { Text = suggested, Padding = new System.Windows.Thickness(10),
                Margin = new System.Windows.Thickness(0, 14, 0, 14), BorderBrush = BIMaestro.Tutorials.DemoTourPalette.Accent,
                BorderThickness = new System.Windows.Thickness(3) };
            panel.Children.Add(input);
            string result = null;
            var apply = new System.Windows.Controls.Button { Content = UiLanguage.Get("ViewTemplateTransferCommand.CreateAndAssignToTarget"), Padding = new System.Windows.Thickness(12),
                Background = BIMaestro.Tutorials.DemoTourPalette.Accent, Foreground = System.Windows.Media.Brushes.White };
            apply.Click += (_, __) => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); window.Close(); } };
            panel.Children.Add(apply);
            window.ShowDialog();
            return result;
        }

        private Result Export(ExternalCommandData data, Document document, View activeView)
        {
            if (!CanUseView(activeView)) return ShowUnsupportedView();

            var filterDialog = new TaskDialog(UiLanguage.Get("ViewTemplateTransferCommand.ExportOptions"))
            {
                MainInstruction = UiLanguage.Get("ViewTemplateTransferCommand.IncludeFilterConfiguration"),
                MainContent = UiLanguage.Get("ViewTemplateTransferCommand.FilterRulesCategoriesVisibilityEnabledStateAndGraphic"),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            filterDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                UiLanguage.Get("ViewTemplateTransferCommand.YesExportFilters"));
            filterDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.Get("ViewTemplateTransferCommand.NoKeepTargetProjectFilters"));
            TaskDialogResult filterChoice = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(data.Application.MainWindowHandle, UiLanguage.Get("ViewTemplateTransferCommand.ExportOptions"), UiLanguage.Get("ViewTemplateTransferCommand.ChooseTheHighlightedOptionToAlsoIncludeFilters"),
                    UiLanguage.Get("ViewTemplateTransferCommand.YesExportFilters"), UiLanguage.Get("ViewTemplateTransferCommand.NoKeepTargetProjectFilters"), 1)
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
                Title = UiLanguage.Get("ViewTemplateTransferCommand.ExportViewTemplate"),
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
            RecordOperationResult(ButtonId, "export_view_template", false,
                new Dictionary<string, int> { ["files_exported"] = 1 });

            TaskDialog.Show(
                UiLanguage.Get("ViewTemplateTransferCommand.ExportComplete"),
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
                Title = UiLanguage.Get("ViewTemplateTransferCommand.ImportViewTemplate"),
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
                    throw new InvalidDataException(UiLanguage.Get("ViewTemplateTransferCommand.UnrecognizedFileFormat"));
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    UiLanguage.Get("ViewTemplateTransferCommand.ImportFailed"),
                    UiLanguage.Get("ViewTemplateTransferCommand.TheFileCannotBeRead") + ex.Message);
                return Result.Failed;
            }

            var modeDialog = new TaskDialog(UiLanguage.Get("ViewTemplateTransferCommand.ImportMode"))
            {
                MainInstruction = UiLanguage.Get("ViewTemplateTransferCommand.HowShouldThisFileBeApplied"),
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
                UiLanguage.Get("ViewTemplateTransferCommand.CreateUpdateANamedViewTemplate"),
                UiLanguage.Get("ViewTemplateTransferCommand.TheTemplateWillBeSavedInTheProject"));
            modeDialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                UiLanguage.Get("ViewTemplateTransferCommand.CustomizeOnlyTheActiveView"),
                UiLanguage.Get("ViewTemplateTransferCommand.TheCurrentTemplateWillBeDetachedAndThe"));
            TaskDialogResult mode = BIMaestro.Tutorials.DemoViewTemplateGuide.IsGuided(document)
                ? GuidedChoice(app.MainWindowHandle, UiLanguage.Get("ViewTemplateTransferCommand.ImportMode"), UiLanguage.Get("ViewTemplateTransferCommand.ChooseTheHighlightedOptionTheTemplateWillBe"),
                    UiLanguage.Get("ViewTemplateTransferCommand.CreateUpdateANamedViewTemplate"), UiLanguage.Get("ViewTemplateTransferCommand.CustomizeOnlyTheActiveView"), 1)
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
                    UiLanguage.Get("ViewTemplateTransferCommand.NameOfTheRevitViewTemplateToCreate"),
                    UiLanguage.Get("ViewTemplateTransferCommand.TemplateName"),
                    string.IsNullOrWhiteSpace(package.SuggestedName) ? UiLanguage.Get("ViewTemplateTransferCommand.BIMaestroTemplate") : package.SuggestedName);
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
                    UiLanguage.Get("ViewTemplateTransferCommand.ImportFailed"),
                    ex.Message);
                return Result.Failed;
            }

            var summary = new StringBuilder();
            summary.AppendLine(createTemplate
                ? UiLanguage.Get("ViewTemplateTransferCommand.TemplateSavedAndAssigned") + report.TargetName
                : UiLanguage.Get("ViewTemplateTransferCommand.ViewCustomized") + report.TargetName);
            summary.AppendLine();
            summary.AppendLine(UiLanguage.Get("ViewTemplateTransferCommand.ParametersApplied") + report.ParametersApplied);
            summary.AppendLine(UiLanguage.Get("ViewTemplateTransferCommand.CategoriesApplied") + report.CategoriesApplied);
            summary.AppendLine(UiLanguage.Get("ViewTemplateTransferCommand.WorksetsApplied") + report.WorksetsApplied);
            summary.AppendLine(UiLanguage.Get("ViewTemplateTransferCommand.FiltersApplied") + report.FiltersApplied);
            if (report.Warnings.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine(UiLanguage.Get("ViewTemplateTransferCommand.ItemsSkippedBecauseTheyAreMissingOrIncompatible"));
                foreach (string warning in report.Warnings.Take(8)) summary.AppendLine("• " + warning);
                if (report.Warnings.Count > 8)
                    summary.AppendLine("• … +" + (report.Warnings.Count - 8) + UiLanguage.Get("ViewTemplateTransferCommand.More"));
            }

            TaskDialog.Show(UiLanguage.Get("ViewTemplateTransferCommand.ImportComplete"), summary.ToString());
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
                UiLanguage.Get("ViewTemplateTransferCommand.UnsupportedView"),
                UiLanguage.Get("ViewTemplateTransferCommand.ThisRevitViewDoesNotSupportTemplatesOr"));
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
