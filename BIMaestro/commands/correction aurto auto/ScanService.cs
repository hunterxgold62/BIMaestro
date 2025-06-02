using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace ScanTextRevit
{
    public class ScanService
    {
        public Dictionary<string, List<ScannedTextItem>> ScanSelectedViewsAndSheets(Document doc, List<ElementId> selectedIds)
        {
            var allTextsByViewSheet = new Dictionary<string, List<ScannedTextItem>>();

            foreach (ElementId eId in selectedIds)
            {
                Element elem = doc.GetElement(eId);

                // CAS : VUE (hors feuille)
                if (elem is View view && !view.IsTemplate && !(view is ViewSheet))
                {
                    var textsInView = ScanSingleView(doc, view);

                    string prefix = GetViewTypeLabel(view);
                    string key = $"{prefix} : {view.Name} (Id {view.Id.IntegerValue})";

                    // (A) Filtrer et dédupliquer avant de stocker
                    var filtered = FilterAndDeduplicate(textsInView);
                    allTextsByViewSheet[key] = filtered;
                }
                // CAS : FEUILLE
                else if (elem is ViewSheet sheet)
                {
                    var textsDirectlyOnSheet = new List<ScannedTextItem>();

                    // (1) TextNotes
                    var textNotesOnSheet = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TextNotes)
                        .Cast<TextNote>()
                        .ToList();
                    foreach (TextNote tn in textNotesOnSheet)
                    {
                        if (!string.IsNullOrEmpty(tn.Text))
                        {
                            textsDirectlyOnSheet.Add(new ScannedTextItem
                            {
                                Text = tn.Text,
                                ElementId = tn.Id.IntegerValue.ToString()
                            });
                        }
                    }

                    // (2) Étiquettes (IndependentTag)
                    var tagsOnSheet = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(IndependentTag))
                        .WhereElementIsNotElementType()
                        .Cast<IndependentTag>()
                        .ToList();
                    foreach (var tag in tagsOnSheet)
                    {
                        string tagText = tag.TagText;
                        if (!string.IsNullOrEmpty(tagText))
                        {
                            textsDirectlyOnSheet.Add(new ScannedTextItem
                            {
                                Text = tagText,
                                ElementId = tag.Id.IntegerValue.ToString()
                            });
                        }
                    }

                    // (3) Symboles d’annotation (GenericAnnotation)
                    var annSymbolsOnSheet = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_GenericAnnotation)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();
                    foreach (var fs in annSymbolsOnSheet)
                    {
                        var paramTexts = GetAllStringParameters(fs, doc);
                        foreach (var text in paramTexts)
                        {
                            textsDirectlyOnSheet.Add(new ScannedTextItem
                            {
                                Text = text,
                                ElementId = fs.Id.IntegerValue.ToString()
                            });
                        }
                    }

                    // (4) Cartouche (TitleBlock)
                    var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .ToList();
                    foreach (var tb in titleBlocks)
                    {
                        var paramTexts = GetAllStringParameters(tb, doc);
                        foreach (var text in paramTexts)
                        {
                            textsDirectlyOnSheet.Add(new ScannedTextItem
                            {
                                Text = text,
                                ElementId = tb.Id.IntegerValue.ToString()
                            });
                        }
                    }

                    // Ajout final
                    string sheetKey = $"Feuille : {sheet.SheetNumber} - {sheet.Name} (Id {sheet.Id.IntegerValue})";
                    allTextsByViewSheet[sheetKey] = FilterAndDeduplicate(textsDirectlyOnSheet);

                    // (5) Vues placées (Viewport)
                    var vports = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .ToList();
                    foreach (var vp in vports)
                    {
                        View placedView = doc.GetElement(vp.ViewId) as View;
                        if (placedView != null)
                        {
                            var textsInPlacedView = ScanSingleView(doc, placedView);

                            string prefix = GetViewTypeLabel(placedView);
                            string key = $"{prefix} : {placedView.Name} (Id {placedView.Id.IntegerValue})";
                            allTextsByViewSheet[key] = FilterAndDeduplicate(textsInPlacedView);
                        }
                    }

                    // (6) Nomenclatures placées (ScheduleSheetInstance)
                    var scheduleInstances = new FilteredElementCollector(doc, sheet.Id)
                        .OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .ToList();
                    foreach (var ssi in scheduleInstances)
                    {
                        ViewSchedule vsched = doc.GetElement(ssi.ScheduleId) as ViewSchedule;
                        if (vsched != null)
                        {
                            var textsInSchedule = ScanSingleView(doc, vsched);
                            string key = $"Nomenclature : {vsched.Name} (Id {vsched.Id.IntegerValue})";
                            allTextsByViewSheet[key] = FilterAndDeduplicate(textsInSchedule);
                        }
                    }
                }
            }

            return allTextsByViewSheet;
        }

        /// <summary>
        /// Scanne une vue (ou nomenclature) et retourne tous les textes.
        /// </summary>
        private List<ScannedTextItem> ScanSingleView(Document doc, View view)
        {
            var result = new List<ScannedTextItem>();

            // CAS : Nomenclature
            if (view is ViewSchedule schedule)
            {
                var scheduleTexts = ScanSchedule(doc, schedule);
                foreach (var text in scheduleTexts)
                {
                    result.Add(new ScannedTextItem { Text = text, ElementId = view.Id.IntegerValue.ToString() });
                }
                return result;
            }

            // (1) TextNotes
            var textNotes = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_TextNotes)
                .WhereElementIsNotElementType()
                .Cast<TextNote>()
                .ToList();
            foreach (var tn in textNotes)
            {
                if (!string.IsNullOrEmpty(tn.Text))
                {
                    result.Add(new ScannedTextItem { Text = tn.Text, ElementId = tn.Id.IntegerValue.ToString() });
                }
            }

            // (2) IndependentTag
            var tags = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .WhereElementIsNotElementType()
                .Cast<IndependentTag>()
                .ToList();
            foreach (var tag in tags)
            {
                string tagText = tag.TagText;
                if (!string.IsNullOrEmpty(tagText))
                {
                    result.Add(new ScannedTextItem { Text = tagText, ElementId = tag.Id.IntegerValue.ToString() });
                }
            }

            // (3) Symboles d’annotation
            var annSymbols = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_GenericAnnotation)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();
            foreach (var fs in annSymbols)
            {
                var paramTexts = GetAllStringParameters(fs, doc);
                foreach (var text in paramTexts)
                {
                    result.Add(new ScannedTextItem { Text = text, ElementId = fs.Id.IntegerValue.ToString() });
                }
            }

            return result;
        }

        /// <summary>
        /// Scanne une nomenclature et renvoie la liste brute de ses cellules
        /// (avant filtrage/dédup).
        /// </summary>
        private List<string> ScanSchedule(Document doc, ViewSchedule vsched)
        {
            var scheduleTexts = new List<string>();
            TableData tableData = vsched.GetTableData();
            if (tableData == null)
                return scheduleTexts;

            int sectionCount = tableData.NumberOfSections;
            for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                SectionType st = (SectionType)sectionIndex;
                TableSectionData sectionData = tableData.GetSectionData(st);
                if (sectionData == null)
                    continue;

                int rowCount = sectionData.NumberOfRows;
                int colCount = sectionData.NumberOfColumns;

                for (int r = 0; r < rowCount; r++)
                {
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellText = sectionData.GetCellText(r, c);
                        if (!string.IsNullOrEmpty(cellText))
                        {
                            scheduleTexts.Add(cellText);
                        }
                    }
                }
            }
            return scheduleTexts;
        }

        /// <summary>
        /// Filtre les ScannedTextItem en :
        /// - excluant ceux qui sont purement ou majoritairement numériques,
        /// - dédupliquant les textes identiques (on ne garde que la 1ère occurrence).
        /// </summary>
        private List<ScannedTextItem> FilterAndDeduplicate(List<ScannedTextItem> originalList)
        {
            var finalList = new List<ScannedTextItem>();
            var seenTexts = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var item in originalList)
            {
                if (string.IsNullOrWhiteSpace(item.Text))
                    continue;

                // Vérifie si c'est majoritairement numérique
                if (IsMostlyNumericOrEmpty(item.Text))
                    continue;

                // Déduplication (on ignore si déjà vu)
                if (!seenTexts.Add(item.Text))
                    continue;

                finalList.Add(item);
            }
            return finalList;
        }

        /// <summary>
        /// Renvoie true si la chaîne est vide ou essentiellement composée
        /// de chiffres/ponctuation (ex: "50", "60x80", "20.5", "30-40").
        /// </summary>
        private bool IsMostlyNumericOrEmpty(string text)
        {
            // On retire les espaces
            var trimmed = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (trimmed.Length == 0) return true;

            // Vérifie que tous les caractères sont digits ou . , - / + : x
            // (on peut ajuster la liste si besoin)
            return trimmed.All(ch =>
                char.IsDigit(ch)
                || ch == '.'
                || ch == '-'
                || ch == ','
                || ch == '/'
                || ch == '+'
                || ch == ':'
                || ch == 'x' // pour "60x80"
            );
        }

        /// <summary>
        /// Récupère les paramètres string d'instance et de type,
        /// en excluant ceux non désirés (IsExcludedParameter).
        /// </summary>
        private List<string> GetAllStringParameters(Element elem, Document doc)
        {
            var texts = new List<string>();

            // Paramètres d'instance
            foreach (Parameter p in elem.Parameters)
            {
                if (p.Definition != null &&
                    p.StorageType == StorageType.String &&
                    p.HasValue &&
                    !IsExcludedParameter(p.Definition.Name))
                {
                    string val = p.AsString();
                    if (!string.IsNullOrEmpty(val))
                        texts.Add($"{p.Definition.Name} (inst) : {val}");
                }
            }

            // Paramètres de type
            ElementId typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                Element typeElem = doc.GetElement(typeId);
                if (typeElem != null)
                {
                    foreach (Parameter p in typeElem.Parameters)
                    {
                        if (p.Definition != null &&
                            p.StorageType == StorageType.String &&
                            p.HasValue &&
                            !IsExcludedParameter(p.Definition.Name))
                        {
                            string val = p.AsString();
                            if (!string.IsNullOrEmpty(val))
                                texts.Add($"{p.Definition.Name} (type) : {val}");
                        }
                    }
                }
            }
            return texts;
        }

        /// <summary>
        /// Exclut certains paramètres par nom (ex: "Chemin du fichier", "Echelle", etc.).
        /// </summary>
        private bool IsExcludedParameter(string paramName)
        {
            // On se met tout en minuscule
            string lower = paramName.ToLower();

            // Exclure si commence par "CML_"
            if (lower.StartsWith("cml_")) return true;

            // Exclure si contient un de ces mots-clés (tous en minuscule)
            if (lower.Contains("lien") ||
                lower.Contains("horodatage") ||
                lower.Contains("horodate") ||
                lower.Contains("date") ||
                lower.Contains("heure") ||
                lower.Contains("temps") ||
                lower.Contains("maquette") ||
                lower.Contains("chemin du fichier") ||
                lower.Contains("quadrillage de guidage") ||
                lower.Contains("figure dans la liste des feuilles") ||
                lower.Contains("référencement de la feuille") ||
                lower.Contains("référencement du détail") ||
                lower.Contains("révision actuelle diffusée") ||
                lower.Contains("révision actuelle diffusée par") ||
                lower.Contains("révision actuelle remise à") ||
                lower.Contains("echelle") ||      // Pour "Echelle"
                lower.Contains("échelle") ||      // Si Revit renvoie "Échelle"
                lower.Contains("révisions sur feuille") ||
                lower.Contains("créateur") ||
                lower.Contains("dessiné par") ||
                lower.Contains("vérifié par") ||
                lower.Contains("conçu par") ||
                lower.Contains("approuvé par") ||
                lower.Contains("date de fin de la feuille") ||
                lower.Contains("date de révision actuelle") ||
                lower.Contains("largeur de la feuille") ||
                lower.Contains("hauteur de la feuille") ||
                lower.Contains("catégorie feuille") ||
                lower.Contains("remplacements visibilité / graphisme") ||
                lower.Contains("dépendance")
               )
            {
                return true;
            }

            // Sinon on ne l'exclut pas
            return false;
        }

        private string GetViewTypeLabel(View view)
        {
            if (view is ViewSchedule) return "Nomenclature";
            if (view.ViewType == ViewType.Legend) return "Légende";
            return "Vue";
        }
    }
}
