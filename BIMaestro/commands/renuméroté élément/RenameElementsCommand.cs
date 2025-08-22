using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class RenameElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            var selIds = uiDoc.Selection.GetElementIds();
            if (selIds == null || selIds.Count == 0)
            {
                TaskDialog.Show("Sélection", "Sélectionne au moins un élément.");
                return Result.Cancelled;
            }

            // Écarter les types / liens
            var targets = selIds.Select(doc.GetElement)
                                .Where(e => e != null && !(e is ElementType) && !(e is RevitLinkInstance))
                                .ToList();
            if (targets.Count == 0)
            {
                TaskDialog.Show("Info", "Aucun élément renommable trouvé.");
                return Result.Cancelled;
            }

            // Paramètres proposés d’après le 1er élément (+ “Nom” pour déclencher le .Name)
            var first = targets.First();
            var parameters = GetWritableTextParameters(first);
            if (!parameters.Contains("Nom")) parameters.Add("Nom");
            parameters.Sort(StringComparer.CurrentCultureIgnoreCase);

            var win = new ElementRenamerWindow(parameters);
            if (win.ShowDialog() != true) return Result.Cancelled;

            string selectedParam = win.SelectedParameter ?? "Nom";

            // Tri (bande → Y, puis X) + unités
            double bandHeightInternal = ParseBandHeightToInternal(doc, win.BandHeight);
            var locs = GetElementsWithLocations(doc, targets.Select(t => t.Id).ToList(), uiDoc.ActiveView);
            var ordered = SortElementsByGridLocation(locs, bandHeightInternal);

            // Worksharing : checkout
            TryCheckout(doc, ordered.Select(o => o.Element.Id).ToList());

            // Numérotation
            string prefix = win.Prefix ?? "";
            string suffix = win.Suffix ?? "";
            int current = 1;
            bool isNumeric = win.SelectedNumberFormat == "1,2,3..." ||
                             win.SelectedNumberFormat == "001,002,003..." ||
                             win.SelectedNumberFormat == "0001,0002,0003...";
            bool isAlpha = win.SelectedNumberFormat == "A,B,C...";

            if (isNumeric)
            {
                if (!int.TryParse(win.StartNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
                {
                    TaskDialog.Show("Erreur", "Le numéro de départ doit être un entier.");
                    return Result.Failed;
                }
            }
            else if (isAlpha)
            {
                current = LettersToNumber((win.StartNumber ?? "A").ToUpperInvariant());
                if (current <= 0)
                {
                    TaskDialog.Show("Erreur", "Le départ alphabétique doit être une lettre/séquence valide (A, AA...).");
                    return Result.Failed;
                }
            }

            var notes = new List<string>();

            using (Transaction tx = new Transaction(doc, "Renommer éléments"))
            {
                tx.Start();

                // RESET ?
                if (win.IsReset)
                {
                    foreach (var el in ordered.Select(o => o.Element))
                    {
                        // Ne pas vider les éléments qui exigent un Name non vide
                        if (SupportsNameSetter(el) || IsGridLike(el)) continue;

                        Parameter p = el.LookupParameter(selectedParam);
                        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                        {
                            using (SubTransaction st = new SubTransaction(doc))
                            {
                                try { st.Start(); p.Set(string.Empty); st.Commit(); }
                                catch { st.RollBack(); }
                            }
                        }
                    }
                    tx.Commit();
                    return Result.Succeeded;
                }

                foreach (var el in ordered.Select(o => o.Element))
                {
                    string num = "";
                    if (isNumeric)
                    {
                        if (win.SelectedNumberFormat == "0001,0002,0003...") num = current.ToString("D4", CultureInfo.InvariantCulture);
                        else if (win.SelectedNumberFormat == "001,002,003...") num = current.ToString("D3", CultureInfo.InvariantCulture);
                        else num = current.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (isAlpha)
                    {
                        num = NumberToLetters(current);
                    }

                    string proposed = prefix + num + suffix;
                    if (string.IsNullOrWhiteSpace(proposed)) proposed = isAlpha ? "A" : "1";

                    using (SubTransaction st = new SubTransaction(doc))
                    {
                        try
                        {
                            st.Start();
                            bool ok = false;

                            // === 1) Quadrillages : DATUM_TEXT — c’est LA voie fiable en 2023 ===
                            if (IsGridLike(el))
                            {
                                ok = RenameGridByDatumText(doc, el, proposed); // unicité incluse
                            }
                            // === 2) Niveau / Vue / Feuille / Plan réf. : .Name (+ unicité si besoin) ===
                            else if (el is Level lvl)
                                ok = RenameLevelWithUniqueness(doc, lvl, proposed);
                            else if (el is View v)
                                ok = RenameViewWithUniqueness(doc, v, proposed);
                            else if (el is ViewSheet vs)
                                ok = RenameViewSheetWithUniqueness(doc, vs, proposed);
                            else if (el is ReferencePlane rp)
                            {
                                rp.Name = proposed; ok = true;
                            }
                            // === 3) Sinon : paramètre texte choisi ===
                            if (!ok)
                                ok = TrySetStringParameter(el, selectedParam, proposed);

                            if (!ok)
                            {
                                notes.Add($"Élément {el.Id.IntegerValue} : non modifié (Grid/Name/param).");
                                st.RollBack();
                            }
                            else
                            {
                                st.Commit();
                                current++;
                            }
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"Élément {el.Id.IntegerValue} : {ex.Message}");
                            st.RollBack();
                            current++;
                        }
                    }
                }

                if (notes.Count > 0)
                {
                    TaskDialog.Show("Terminé (avec avertissements)",
                        string.Join(Environment.NewLine, notes.Take(25)) +
                        (notes.Count > 25 ? $"\n... (+{notes.Count - 25} autres)" : ""));
                }

                tx.Commit();
            }

            return Result.Succeeded;
        }

        // ---------- Helpers principaux ----------

        private static bool IsGridLike(Element e) => (e is Grid) || (e is MultiSegmentGrid);
        private static bool SupportsNameSetter(Element e) =>
            (e is Level) || (e is View) || (e is ViewSheet) || (e is ReferencePlane);

        // *** Grids via DATUM_TEXT (2023 fiable) + unicité ***
        private bool RenameGridByDatumText(Document doc, Element e, string proposed)
        {
            // 1) Obtenir le paramètre DATUM_TEXT sur Grid ou MultiSegmentGrid
            Parameter GetDatum(Element el) => el.get_Parameter(BuiltInParameter.DATUM_TEXT);

            // 2) unicité : aucun autre Grid/MultiSegmentGrid ne doit porter ce nom
            bool NameFree(string name) =>
                !new FilteredElementCollector(doc)
                    .WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(Grid)), new ElementClassFilter(typeof(MultiSegmentGrid))))
                    .Cast<Element>()
                    .Any(x =>
                    {
                        var p = GetDatum(x);
                        return p != null && string.Equals(p.AsString(), name, StringComparison.InvariantCultureIgnoreCase);
                    });

            string candidate = proposed;
            if (!NameFree(candidate))
            {
                string baseName = proposed;
                int bump = 1;
                while (bump < 10000 && !NameFree(candidate = $"{baseName}-{bump}")) bump++;
                if (bump == 10000) return false;
            }

            var datum = GetDatum(e);
            if (datum == null || datum.IsReadOnly) return false;

            return datum.Set(candidate);
        }

        // Param string générique
        private bool TrySetStringParameter(Element e, string paramName, string value)
        {
            Parameter p = e.LookupParameter(paramName);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                return p.Set(value);
            return false;
        }

        // Levels
        private bool RenameLevelWithUniqueness(Document doc, Level lvl, string proposed)
        {
            bool Free(string name) => !new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                                       .Any(l => string.Equals(l.Name, name, StringComparison.InvariantCultureIgnoreCase));
            string candidate = proposed;
            if (!Free(candidate))
            {
                string baseName = proposed;
                int i = 1;
                while (i < 10000 && !Free(candidate = $"{baseName}-{i}")) i++;
                if (i == 10000) return false;
            }
            lvl.Name = candidate;
            return true;
        }

        // Views
        private bool RenameViewWithUniqueness(Document doc, View v, string proposed)
        {
            bool Free(string name) => !new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                                       .Any(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
            string candidate = proposed;
            if (!Free(candidate))
            {
                string baseName = proposed;
                int i = 1;
                while (i < 10000 && !Free(candidate = $"{baseName}-{i}")) i++;
                if (i == 10000) return false;
            }
            v.Name = candidate;
            return true;
        }

        // ViewSheets
        private bool RenameViewSheetWithUniqueness(Document doc, ViewSheet vs, string proposed)
        {
            bool Free(string name) => !new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                                       .Any(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
            string candidate = proposed;
            if (!Free(candidate))
            {
                string baseName = proposed;
                int i = 1;
                while (i < 10000 && !Free(candidate = $"{baseName}-{i}")) i++;
                if (i == 10000) return false;
            }
            vs.Name = candidate;
            return true;
        }

        // ---------- Tri / localisations / unités ----------

        private class ElementLocation { public Element Element; public XYZ Location; }

        private List<ElementLocation> GetElementsWithLocations(Document doc, ICollection<ElementId> ids, View view)
        {
            var list = new List<ElementLocation>();
            Transform t = Transform.Identity;
            t.BasisX = view.RightDirection; t.BasisY = view.UpDirection; t.BasisZ = view.ViewDirection;
            Transform w2v = t.Inverse;

            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;

                if (e.Location is LocationPoint lp)
                {
                    list.Add(new ElementLocation { Element = e, Location = w2v.OfPoint(lp.Point) });
                    continue;
                }
                if (e.Location is LocationCurve lc && lc.Curve != null)
                {
                    list.Add(new ElementLocation { Element = e, Location = w2v.OfPoint(lc.Curve.Evaluate(0.5, true)) });
                    continue;
                }
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    list.Add(new ElementLocation { Element = e, Location = w2v.OfPoint((bb.Min + bb.Max) / 2.0) });
                }
            }
            return list;
        }

        private List<ElementLocation> SortElementsByGridLocation(List<ElementLocation> elems, double grid = 1.0)
        {
            var grouped = elems.GroupBy(e => (int)Math.Floor(e.Location.Y / grid)).OrderByDescending(g => g.Key);
            var res = new List<ElementLocation>();
            foreach (var g in grouped) res.AddRange(g.OrderBy(e => e.Location.X));
            return res;
        }

        // (non utilisé ici par défaut, mais dispo)
        private ElementId GetElementLevelId(Element e)
        {
            if (e is FamilyInstance fi && fi.LevelId != ElementId.InvalidElementId) return fi.LevelId;
            if (e is Wall w) return w.LevelId;
            if (e is Floor f) return f.LevelId;
            if (e is Ceiling c) return c.LevelId;
            if (e is RoofBase r) return r.LevelId;

            var p = e.get_Parameter(BuiltInParameter.LEVEL_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                 ?? e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            return (p != null && p.HasValue) ? p.AsElementId() : ElementId.InvalidElementId;
        }

        private double ParseBandHeightToInternal(Document doc, string text)
        {
            double u;
            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out u) &&
                !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out u))
                u = 1.0;

            var fo = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
            var du = fo.GetUnitTypeId();
            double internalVal = UnitUtils.ConvertToInternalUnits(u, du);
            return internalVal > 0 ? internalVal : 1.0;
        }

        private void TryCheckout(Document doc, List<ElementId> ids)
        {
            try { if (doc.IsWorkshared && ids.Count > 0) WorksharingUtils.CheckoutElements(doc, ids); }
            catch { /* non bloquant */ }
        }

        private List<string> GetWritableTextParameters(Element e)
        {
            var list = new List<string>();
            foreach (Parameter p in e.Parameters)
                if (p.StorageType == StorageType.String && !p.IsReadOnly)
                    if (!string.IsNullOrEmpty(p.Definition?.Name)) list.Add(p.Definition.Name);
            return list.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private string NumberToLetters(int n)
        {
            string r = string.Empty;
            while (n > 0) { n--; r = (char)('A' + (n % 26)) + r; n /= 26; }
            return r;
        }
        private int LettersToNumber(string s)
        {
            int n = 0;
            foreach (char c in s) { if (c < 'A' || c > 'Z') return -1; n = n * 26 + (c - 'A' + 1); }
            return n;
        }
    }
}
