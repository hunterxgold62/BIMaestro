using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace BIMaestro.Codex
{
    internal static class CodexRepresentationBuilder
    {
        internal static object Build(Document doc, FamilyRepresentationSpec spec, IEnumerable<Element> model, Dictionary<string, FamilySymbol> regions)
        {
            if (spec == null) return null;
            HideModel(spec, model);
            var ids = new List<string>();
            foreach (var drawing in spec.Drawings)
            {
                try
                {
                    int axis = drawing.Plane == "xy" ? 2 : drawing.Plane == "xz" ? 1 : 0;
                    var uv = Enumerable.Range(0, 3).Where(a => a != axis).ToArray();
                    var origin = Basis(axis) * (drawing.Offset / 304.8);
                    var work = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(Basis(axis), origin));
                    var curves = drawing.Curves.SelectMany(c => Curves(c, origin, Basis(uv[0]), Basis(uv[1]))).ToList();
                    if (drawing.Mode == "filled" || drawing.Mode == "masking")
                    {
                        var symbol = regions[drawing.Name];
                        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                        FamilyInstance region;
                        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.ViewBased)
                        {
                            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate &&
                                (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section) &&
                                v.RightDirection.DotProduct(Basis(uv[0])) > .999 && v.UpDirection.DotProduct(Basis(uv[1])) > .999);
                            if (view == null) throw new InvalidOperationException("Aucune vue compatible avec le plan de la region 2D.");
                            region = doc.FamilyCreate.NewFamilyInstance(origin, symbol, view);
                        }
                        else
                        {
                            region = doc.FamilyCreate.NewFamilyInstance(origin, symbol, work, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        }
                        ids.Add(region.Id.ToString());
                    }
                    else
                    {
                        foreach (var curve in curves)
                        {
                            if (drawing.Mode == "symbolic")
                            {
                                var element = doc.FamilyCreate.NewSymbolicCurve(curve, work);
                                using (var visibility = new FamilyElementVisibility(FamilyElementVisibilityType.ViewSpecific)
                                { IsShownInCoarse = true, IsShownInMedium = true, IsShownInFine = true }) element.SetVisibility(visibility);
                                ids.Add(element.Id.ToString());
                            }
                            else
                            {
                                var element = doc.FamilyCreate.NewModelCurve(curve, work);
                                using (var visibility = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                                { IsShownInCoarse = true, IsShownInMedium = true, IsShownInFine = true }) element.SetVisibility(visibility);
                                ids.Add(element.Id.ToString());
                            }
                        }
                    }
                }
                catch (Exception ex) { throw new InvalidOperationException("Représentation 2D « " + drawing.Name + " » : " + ex.Message, ex); }
            }
            doc.Regenerate();
            return new { created_elements = ids, model_hidden_in = spec.HideModelIn, geometry = "fixed",
                drawings = spec.Drawings.Select(d => new { name = d.Name, mode = d.Mode, plane = d.Plane }).ToArray(),
                note = "Les courbes symboliques et régions sont des représentations planes ; les lignes de modèle restent visibles en 3D. Les tracés libres ne suivent pas les paramètres de dimensions." };
        }
        // In Revit 2023, model templates have no filled-region types and cannot
        // receive project types by copy. Native nested detail items provide the
        // supported 2D filled/masking representation in a model family.
        internal static Dictionary<string, FamilySymbol> PrepareRegions(Document parent, FamilyRepresentationSpec spec)
        {
            var result = new Dictionary<string, FamilySymbol>();
            if (spec == null || !spec.Drawings.Any(d => d.Mode == "filled" || d.Mode == "masking")) return result;
            var roots = new[] { parent.Application.FamilyTemplatePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + parent.Application.VersionNumber, "Family Templates") };
            var names = new[] { "Eléments de détail métrique", "Éléments de détail métrique", "Metric Detail Item", "Metric Detail Component" };
            string template = roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.rft", SearchOption.AllDirectories))
                .FirstOrDefault(p => names.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase));
            if (template == null) throw new InvalidOperationException("Installer le gabarit Eléments de détail métrique / Metric Detail Item pour les pochages et masques.");
            foreach (var drawing in spec.Drawings.Where(d => d.Mode == "filled" || d.Mode == "masking"))
            {
                Document child = null;
                try
                {
                    child = parent.Application.NewFamilyDocument(template);
                    using (var t = new Transaction(child, "BIMaestro — région 2D native"))
                    {
                        t.Start();
                        if (child.FamilyManager.CurrentType == null) child.FamilyManager.NewType("Standard");
                        var view = new FilteredElementCollector(child).OfClass(typeof(View)).Cast<View>().First(v => !v.IsTemplate &&
                            (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Detail));
                        var curves = drawing.Curves.SelectMany(c => Curves(c, XYZ.Zero, XYZ.BasisX, XYZ.BasisY)).ToList();
                        var type = RegionType(child, drawing);
                        FilledRegion.Create(child, type.Id, view.Id, new[] { CurveLoop.Create(curves) });
                        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Région 2D annulée par Revit.");
                    }
                    var loaded = child.LoadFamily(parent);
                    if (loaded == null) throw new InvalidOperationException("Chargement de la région 2D refusé.");
                    using (var t = new Transaction(parent, "BIMaestro — nom de région imbriquée"))
                    {
                        t.Start(); loaded.Name = "BIM_2D_" + CodexFamilyBuilder.SafeName(drawing.Name) + "_" + Guid.NewGuid().ToString("N").Substring(0,8);
                        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Nom de région 2D non validé.");
                    }
                    result.Add(drawing.Name, loaded.GetFamilySymbolIds().Select(parent.GetElement).OfType<FamilySymbol>().First());
                }
                finally { if (child != null && child.IsValidObject) child.Close(false); }
            }
            return result;
        }
        internal static void HideModel(FamilyRepresentationSpec spec, IEnumerable<Element> model)
        {
            if (spec == null || spec.HideModelIn.Length == 0) return;
            foreach (var form in model.OfType<GenericForm>())
            {
                using (var visibility = form.GetVisibility())
                {
                    if (spec.HideModelIn.Contains("xy")) { visibility.IsShownInTopBottom = false; visibility.IsShownInPlanRCPCut = false; }
                    if (spec.HideModelIn.Contains("xz")) visibility.IsShownInFrontBack = false;
                    if (spec.HideModelIn.Contains("yz")) visibility.IsShownInLeftRight = false;
                    form.SetVisibility(visibility);
                }
                using (var actual = form.GetVisibility())
                    if (spec.HideModelIn.Contains("xy") && actual.IsShownInTopBottom || spec.HideModelIn.Contains("xz") && actual.IsShownInFrontBack || spec.HideModelIn.Contains("yz") && actual.IsShownInLeftRight)
                        throw new InvalidOperationException("Le solide ne respecte pas la visibilité 2D demandée.");
            }
        }
        private static FilledRegionType RegionType(Document doc, FamilyDrawingSpec spec)
        {
            var original = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault();
            if (original == null) throw new InvalidOperationException("Le gabarit d'élément de détail ne contient aucun type de région.");
            var type = (FilledRegionType)original.Duplicate("BIM_2D_" + CodexFamilyBuilder.SafeName(spec.Name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            type.IsMasking = true;
            type.BackgroundPatternId = ElementId.InvalidElementId;
            type.ForegroundPatternId = ElementId.InvalidElementId;
            if (spec.Mode == "filled")
            {
                FillPatternElement pattern;
                if (spec.Pattern == "solid") pattern = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                else pattern = FillPatternElement.Create(doc, new FillPattern("BIM_2D_Hachure_" + Guid.NewGuid().ToString("N"), FillPatternTarget.Drafting, FillPatternHostOrientation.ToView, Math.PI / 4, 3 / 304.8));
                if (pattern == null) throw new InvalidOperationException("Motif de remplissage uni absent du gabarit.");
                type.ForegroundPatternId = pattern.Id; type.ForegroundPatternColor = new Color(spec.Rgb[0], spec.Rgb[1], spec.Rgb[2]);
            }
            return type;
        }
        private static IEnumerable<Curve> Curves(FamilyDrawingCurve curve, XYZ origin, XYZ u, XYZ v)
        {
            var p = curve.Points.Select(a => origin + u * (a[0] / 304.8) + v * (a[1] / 304.8)).ToArray();
            if (curve.Kind == "line") yield return Line.CreateBound(p[0], p[1]);
            else if (curve.Kind == "arc") yield return Arc.Create(p[0], p[1], p[2]);
            else
            {
                yield return Arc.Create(p[0], curve.Radius / 304.8, 0, Math.PI, u, v);
                yield return Arc.Create(p[0], curve.Radius / 304.8, Math.PI, 2 * Math.PI, u, v);
            }
        }
        private static XYZ Basis(int a) => a == 0 ? XYZ.BasisX : a == 1 ? XYZ.BasisY : XYZ.BasisZ;
    }
}
