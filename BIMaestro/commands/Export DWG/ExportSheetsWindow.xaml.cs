using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MessageBox = System.Windows.MessageBox;

namespace Visualisation
{
    public partial class ExportWindow : Window
    {
        private readonly Document _doc;
        private readonly List<ViewSheetSet> _sheetSets;

        public ExportWindow(ExternalCommandData cmdData)
        {
            InitializeComponent();
            _doc = cmdData.Application.ActiveUIDocument.Document;

            _sheetSets = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSheetSet))
                .Cast<ViewSheetSet>()
                .ToList();

            SheetSetComboBox.ItemsSource = _sheetSets.Select(s => s.Name).OrderBy(n => n);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CommonOpenFileDialog
            {
                Title = "Choisissez le dossier d’export DWG",
                IsFolderPicker = true,
                AllowNonFileSystemItems = false,
                EnsurePathExists = true,
                EnsureValidNames = true
            };
            if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
                FolderTextBox.Text = dlg.FileName;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            string setName = SheetSetComboBox.Text?.Trim();
            if (string.IsNullOrEmpty(setName))
            {
                MessageBox.Show("Veuillez saisir un nom de jeu de feuilles.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sheetSet = _sheetSets.FirstOrDefault(s => s.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
            if (sheetSet == null)
            {
                MessageBox.Show($"Le jeu '{setName}' n'existe pas.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string exportDir = FolderTextBox.Text;
            if (string.IsNullOrEmpty(exportDir) || !Directory.Exists(exportDir))
            {
                MessageBox.Show("Veuillez choisir un dossier d’export valide.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExportButton.IsEnabled = false;
            CancelButton.IsEnabled = false;

            var sheets = sheetSet.Views.OfType<ViewSheet>().ToList();
            if (sheets.Count == 0)
            {
                MessageBox.Show("Aucune feuille à exporter dans ce jeu.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            var options = new DWGExportOptions
            {
                MergedViews = true,
                TargetUnit = ExportUnit.Meter,
                Colors = ExportColorMode.TrueColorPerView
            };

            var pdfRule = TryGetActivePdfNamingRule(_doc);

            var sw = Stopwatch.StartNew();
            foreach (var vs in sheets)
            {
                string nameFromPdf = pdfRule != null ? BuildNameFromPdfRule(_doc, vs, pdfRule) : null;

                string candidate = !string.IsNullOrWhiteSpace(nameFromPdf)
                    ? nameFromPdf
                    : BuildNameFromCmlParams(vs); // fallback

                candidate = SanitizeFileName(candidate);
                string unique = EnsureUnique(exportDir, candidate, "dwg");

                bool success = TryExport(vs, exportDir, unique, options);
                if (!success)
                {
                    string fallback = EnsureUnique(exportDir, SanitizeFileName(vs.SheetNumber), "dwg");
                    TryExport(vs, exportDir, fallback, options);
                }
            }
            sw.Stop();

            MessageBox.Show(
                $"Export de {sheets.Count} feuilles réalisé en {sw.Elapsed:mm\\:ss}.",
                "Terminé", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }

        private bool TryExport(ViewSheet vs, string dir, string nameNoExt, DWGExportOptions options)
        {
            try
            {
                _doc.Export(dir, nameNoExt, new List<ElementId> { vs.Id }, options);
                string pcp = Path.Combine(dir, nameNoExt + ".pcp");
                if (File.Exists(pcp)) File.Delete(pcp);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Export failed '{nameNoExt}' : {ex.Message}");
                return false;
            }
        }

        private static string EnsureUnique(string dir, string baseNameNoExt, string extNoDot)
        {
            string path = Path.Combine(dir, baseNameNoExt + "." + extNoDot);
            if (!File.Exists(path)) return baseNameNoExt;
            int i = 1;
            while (File.Exists(Path.Combine(dir, $"{baseNameNoExt} ({i}).{extNoDot}"))) i++;
            return $"{baseNameNoExt} ({i})";
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var regex = new Regex($"[{Regex.Escape(new string(invalid))}]");
            var clean = regex.Replace(name, "_").Trim().TrimEnd('.');
            return clean.Length <= 120 ? clean : clean.Substring(0, 120);
        }

        // ----------- RÈGLE PDF -----------
        private static IList<TableCellCombinedParameterData> TryGetActivePdfNamingRule(Document doc)
        {
            ExportPDFSettings settings = ExportPDFSettings.GetActivePredefinedSettings(doc);
            if (settings == null)
            {
                var names = ExportPDFSettings.ListNames(doc);
                if (names != null && names.Count > 0)
                    settings = ExportPDFSettings.FindByName(doc, names[0]);
            }
            if (settings == null) return null;

            var opts = settings.GetOptions();
            var rule = opts.GetNamingRule();
            return (rule != null && rule.Count > 0) ? rule : null;
        }

        private static string BuildNameFromPdfRule(Document doc, ViewSheet sheet, IList<TableCellCombinedParameterData> rule)
        {
            // Vue principale si la règle cible "Vues"
            View primaryView = null;
            Viewport viewport = null;
            var vpId = sheet.GetAllViewports().FirstOrDefault();
            if (vpId != ElementId.InvalidElementId)
            {
                viewport = doc.GetElement(vpId) as Viewport;
                if (viewport != null)
                    primaryView = doc.GetElement(viewport.ViewId) as View;
            }

            var sb = new StringBuilder();

            for (int i = 0; i < rule.Count; i++)
            {
                var cell = rule[i];
                string value = "";

                Element target = null;
                if (cell.CategoryId != null && cell.CategoryId != ElementId.InvalidElementId)
                {
                    var bic = (BuiltInCategory)cell.CategoryId.IntegerValue;
                    if (bic == BuiltInCategory.OST_Sheets) target = sheet;
                    else if (bic == BuiltInCategory.OST_ProjectInformation) target = doc.ProjectInformation;
                    else if (bic == BuiltInCategory.OST_Views) target = (Element)primaryView ?? sheet;
                }

                if (target != null && cell.ParamId != null && cell.ParamId != ElementId.InvalidElementId)
                {
                    // Pour les paramètres de "Vue", privilégier la valeur affichée sur le viewport
                    // (ex : "Titre sur feuille") puis retomber sur la vue elle-même.
                    if (target is View)
                        value = GetParamValue(doc, target, cell.ParamId, viewport);
                    else
                        value = GetParamValue(doc, target, cell.ParamId);
                }

                if (string.IsNullOrWhiteSpace(value))
                    value = cell.SampleValue ?? "";

                if (!string.IsNullOrEmpty(cell.Prefix)) sb.Append(cell.Prefix);
                sb.Append(value);
                if (!string.IsNullOrEmpty(cell.Suffix)) sb.Append(cell.Suffix);
                if (!string.IsNullOrEmpty(cell.Separator) && i < rule.Count - 1)
                    sb.Append(cell.Separator);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Résout un ParamId de la règle PDF : BuiltInParameter ou ParameterElement.
        /// Essaie sur l’instance puis sur le type. Retourne AsString() ou AsValueString().
        /// </summary>
        private static string GetParamValue(Document doc, Element target, ElementId paramId, Viewport viewport = null)
        {
            // Vue : d'abord ce qui est réellement affiché sur la feuille (viewport), puis la vue
            if (viewport != null && target is View)
            {
                string fromViewport = ResolveParam(doc, viewport, paramId);
                if (!string.IsNullOrWhiteSpace(fromViewport))
                    return fromViewport;
            }

            return ResolveParam(doc, target, paramId);
        }

        private static string ResolveParam(Document doc, Element target, ElementId paramId)
        {
            // 1) – Cas BuiltInParameter (enum)
            try
            {
                var bip = (BuiltInParameter)paramId.IntegerValue;
                // Enum.IsDefined n’est pas obligatoire : certains BIP ne sont pas déclarés mais restent valides.
                Parameter p = target.get_Parameter(bip);
                if (p == null)
                {
                    var type = doc.GetElement(target.GetTypeId()) as ElementType;
                    if (type != null) p = type.get_Parameter(bip);
                }
                if (p != null) return p.AsString() ?? p.AsValueString() ?? "";
            }
            catch { /* pas un BIP valide → on tente ParameterElement */ }

            // 2) – Cas ParameterElement (paramètre partagé/projet)
            if (doc.GetElement(paramId) is ParameterElement pe)
            {
                Definition def = pe.GetDefinition();
                if (def != null)
                {
                    Parameter p = target.get_Parameter(def);
                    if (p == null)
                    {
                        var type = doc.GetElement(target.GetTypeId()) as ElementType;
                        if (type != null) p = type.get_Parameter(def);
                    }
                    if (p != null) return p.AsString() ?? p.AsValueString() ?? "";

                    // dernier filet : Lookup par nom (selon def.Name)
                    var byName = target.LookupParameter(def.Name) ??
                                 (doc.GetElement(target.GetTypeId()) as ElementType)?.LookupParameter(def.Name);
                    if (byName != null) return byName.AsString() ?? byName.AsValueString() ?? "";
                }
            }

            return "";
        }

        // ----------- Fallback CML_* -----------
        private static string BuildNameFromCmlParams(ViewSheet sheet)
        {
            string P(string n) =>
                sheet.LookupParameter(n) is Parameter p && p.HasValue ? (p.AsString() ?? "").Trim() : "";

            var parts = new[]
            {
                P("CML_Projet"),
                P("CML_Phase"),
                P("CML_Emetteur"),
                P("CML_Lot"),
                P("CML_Nature"),
                P("Numéro de la feuille"),
                !string.IsNullOrEmpty(P("Révisions sur feuille")) ? P("Révisions sur feuille") : P("Révision actuelle"),
                sheet.Name
            }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            return string.Join("-", parts);
        }
    }
}
