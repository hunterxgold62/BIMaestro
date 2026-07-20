#region Imports
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Dynamo.Applications;
using Dynamo.Applications.Properties;
using Licensing;
#endregion

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ReservationAutoV3Command : BaseTrackedCommand
    {
        protected override string ButtonId => "ReservationAutoV3Command";

        // Evite de spammer des warnings
        private static bool _voidCutWarnShown = false;

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIApplication uiApp = data.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // Load config
                var cfg = ReservationAutoV3ConfigStore.LoadOrDefault();

                // Auto-détection : si V1/V2 déjà dans la maquette, on s’en sert
                EnsureAutoConfigFromProject(doc, cfg);
                ReservationAutoV3ConfigStore.Save(cfg, out _);

                // UI
                var win = new ReservationAutoV3Window(doc, cfg);
                if (win.ShowDialog() != true)
                    return Result.Cancelled;

                cfg = win.Config ?? cfg;

                // Resolve profile according to run
                var prof = win.SelectedExecutionProfile ?? GetProfileForRun(cfg, win.SelectedHost, win.SelectedShape);
                if (prof == null || string.IsNullOrWhiteSpace(prof.FamilyName))
                {
                    TaskDialog.Show("BIMaestro",
                        "Aucune famille configurée pour ce cas.\nVa dans Configuration et charge un .RFA puis mappe les paramètres.");
                    return Result.Cancelled;
                }

                if (!TryResolveSymbol(doc, prof, out var reservationSymbol))
                {
                    TaskDialog.Show("BIMaestro",
                        "La famille configurée n’est pas chargée dans ce projet.\nCharge-la (onglet Configuration) ou corrige le mapping.");
                    return Result.Cancelled;
                }

                bool isWall = win.SelectedHost == ReservationAutoV3Window.HostTarget.Mur;
                bool isRect = win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire;

                if (win.AutomatiqueEnabled && !isWall)
                {
                    TaskDialog.Show("BIMaestro", "Mode automatique : disponible uniquement pour les murs.");
                    return Result.Cancelled;
                }

                using (var t = new Transaction(doc, "Réservations Auto V3"))
                {
                    t.Start();
                    if (!reservationSymbol.IsActive) reservationSymbol.Activate();

                    if (!win.AutomatiqueEnabled)
                        RunManual(uiDoc, doc, win, cfg, prof, reservationSymbol);
                    else
                        RunAutomatic(doc, win, cfg, prof, reservationSymbol);

                    t.Commit();
                }

                // Dynamo
                if (win.DynamoAutoEnabled && !string.IsNullOrWhiteSpace(cfg.DynamoPath))
                    TryRunDynamo(data, cfg.DynamoPath);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // =========================
        // AUTO CONFIG (V1/V2 déjà dans maquette)
        // =========================
        private static void EnsureAutoConfigFromProject(Document doc, ReservationAutoV3Config cfg)
        {
            if (doc == null || cfg == null) return;

            EnsureProfile(doc, cfg.WallRect, new[]
            {
                "CML_Réservation rectangulaire verticale",
                "CML_Réservation rectangulaire murale",
                "Réservation rectangulaire murale"
            }, ProfileKind.WallRect);

            EnsureProfile(doc, cfg.FloorRect, new[]
            {
                "CML_Réservation rectangulaire horizontale",
                "CML_Réservation rectangulaire sol",
                "Réservation rectangulaire sol"
            }, ProfileKind.FloorRect);

            EnsureProfile(doc, cfg.WallCirc, new[]
            {
                "CML_Réservation circulaire verticale",
                "CML_Réservation circulaire murale",
                "Réservation circulaire murale"
            }, ProfileKind.WallCirc);

            EnsureProfile(doc, cfg.FloorCirc, new[]
            {
                "CML_Réservation circulaire horizontale",
                "CML_Réservation circulaire sol",
                "Réservation circulaire sol"
            }, ProfileKind.FloorCirc);
        }

        private enum ProfileKind { WallRect, WallCirc, FloorRect, FloorCirc }

        private static void EnsureProfile(Document doc, ProfileConfig p, string[] familyCandidates, ProfileKind kind)
        {
            if (p == null) return;

            if (!string.IsNullOrWhiteSpace(p.FamilyName))
            {
                if (TryResolveSymbol(doc, p, out _))
                    return;
            }

            foreach (var famName in familyCandidates)
            {
                var sym = FindFirstSymbolByFamilyName(doc, famName);
                if (sym == null) continue;

                p.FamilyName = sym.Family.Name;
                p.TypeName = sym.Name;

                AutoMapParameters(sym, p, kind);
                return;
            }
        }

        private static FamilySymbol FindFirstSymbolByFamilyName(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(familyName)) return null;

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family?.Name != null)
                .Where(s => FamilyNameContains(s.Family.Name, familyName))
                .ToList();

            return symbols.FirstOrDefault();
        }

        private static bool FamilyNameContains(string currentFamilyName, string expectedName)
        {
            string current = RemoveCmlPrefix(currentFamilyName);
            string expected = RemoveCmlPrefix(expectedName);
            return current.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoveCmlPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.StartsWith("CML_", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(4)
                : value;
        }

        private static void AutoMapParameters(FamilySymbol sym, ProfileConfig p, ProfileKind kind)
        {
            if (sym == null || p == null) return;

            var names = sym.Parameters
                .Cast<Parameter>()
                .Select(x => x.Definition?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            string Pick(params string[] keys)
            {
                foreach (var k in keys)
                {
                    var hit = names.FirstOrDefault(n => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(hit))
                        return hit;
                }
                return "";
            }

            p.ParamDepth = string.IsNullOrWhiteSpace(p.ParamDepth) ? Pick("prof", "depth", "épais", "epais") : p.ParamDepth;

            if (kind == ProfileKind.WallCirc || kind == ProfileKind.FloorCirc)
            {
                p.ParamDiameter = string.IsNullOrWhiteSpace(p.ParamDiameter) ? Pick("diam", "ø") : p.ParamDiameter;
                return;
            }

            if (string.IsNullOrWhiteSpace(p.ParamLength))
            {
                p.ParamLength = Pick("long", "length");
                if (string.IsNullOrWhiteSpace(p.ParamLength))
                    p.ParamLength = Pick("larg", "width");
            }

            if (kind == ProfileKind.WallRect)
            {
                p.ParamHeight = string.IsNullOrWhiteSpace(p.ParamHeight) ? Pick("haut", "height") : p.ParamHeight;
            }
            else
            {
                p.ParamWidth = string.IsNullOrWhiteSpace(p.ParamWidth) ? Pick("larg", "width") : p.ParamWidth;
                if (string.IsNullOrWhiteSpace(p.ParamWidth))
                    p.ParamWidth = Pick("long", "length");
            }
        }

        // =========================
        // PROFILE / SYMBOL
        // =========================
        private static ProfileConfig GetProfileForRun(ReservationAutoV3Config cfg,
            ReservationAutoV3Window.HostTarget host, ReservationAutoV3Window.ShapeTarget shape)
        {
            if (cfg == null) return null;

            return (host, shape) switch
            {
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => cfg.WallRect,
                (ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.ShapeTarget.Circulaire) => cfg.WallCirc,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Rectangulaire) => cfg.FloorRect,
                (ReservationAutoV3Window.HostTarget.Sol, ReservationAutoV3Window.ShapeTarget.Circulaire) => cfg.FloorCirc,
                _ => null
            };
        }

        private static bool TryResolveSymbol(Document doc, ProfileConfig prof, out FamilySymbol symbol)
        {
            symbol = null;
            if (doc == null || prof == null) return false;
            if (string.IsNullOrWhiteSpace(prof.FamilyName)) return false;

            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s?.Family?.Name != null)
                .Where(s => string.Equals(s.Family.Name, prof.FamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!candidates.Any())
                return false;

            if (!string.IsNullOrWhiteSpace(prof.TypeName))
            {
                symbol = candidates.FirstOrDefault(s =>
                    string.Equals(s.Name, prof.TypeName, StringComparison.OrdinalIgnoreCase));
            }

            symbol ??= candidates.FirstOrDefault();
            return symbol != null;
        }

        // =========================
        // MANUAL
        // =========================
        private static bool IsLinkSource(ReservationAutoV3Window.PipeSource source)
        {
            return source == ReservationAutoV3Window.PipeSource.LienIFC
                   || source == ReservationAutoV3Window.PipeSource.LienRVT;
        }

        private void RunManual(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window win,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            FamilySymbol reservationSymbol)
        {
            bool isWall = win.SelectedHost == ReservationAutoV3Window.HostTarget.Mur;
            bool isRect = win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire;

            string linkScope = win.DoubleLinkEnabled ? " Les deux éléments doivent appartenir à un lien." : "";
            TaskDialog.Show("BIMaestro",
                $"Mode manuel : sélectionne l’objet, puis le {(isWall ? "mur" : "sol")}.{linkScope}\nECHAP pour arrêter.");

            while (true)
            {
                List<MepSelection> picked;
                try
                {
                    picked = PickElements(uiDoc, doc, win, multi: win.MultiEnabled && isRect);
                }
                catch
                {
                    break;
                }

                if (picked == null || picked.Count == 0)
                    break;

                bool pickComesFromLink = picked.Any(x => x.IsLinked);
                bool pickComesFromHost = picked.All(x => !x.IsLinked);
                bool linkSourceSelected = IsLinkSource(win.SelectedPipeSource);
                bool useLinkedHost = win.DoubleLinkEnabled || (linkSourceSelected && pickComesFromHost);

                if (useLinkedHost)
                {
                    var linkedHost = PickLinkedHostCandidate(
                        uiDoc,
                        doc,
                        win.SelectedPipeSource,
                        win.SelectedHost,
                        allowAnyLink: win.DoubleLinkEnabled);
                    if (linkedHost == null)
                        break;

                    if (picked.Count > 1 && isRect)
                    {
                        if (isWall)
                        {
                            CreateRectReservation_MultiAgainstCandidate(
                                doc,
                                linkedHost,
                                reservationSymbol,
                                win.SelectedObject,
                                picked,
                                cfg,
                                prof,
                                win.NormeEnabled);
                        }
                        else
                        {
                            CreateRectReservation_MultiAgainstFloorCandidate(
                                doc,
                                linkedHost,
                                reservationSymbol,
                                win.SelectedObject,
                                picked,
                                cfg,
                                prof,
                                win.NormeEnabled);
                        }
                    }
                    else
                    {
                        CreateReservation_SingleAgainstCandidate(
                            doc,
                            linkedHost,
                            reservationSymbol,
                            win.SelectedObject,
                            picked.First(),
                            cfg,
                            prof,
                            isWall,
                            isRect,
                            win.NormeEnabled);
                    }

                    var tdLinked = new TaskDialog("BIMaestro")
                    {
                        MainInstruction = "Créer une autre réservation ?",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                    };
                    if (tdLinked.Show() != TaskDialogResult.Yes)
                        break;

                    continue;
                }

                Element host;
                try
                {
                    string hostPrompt = pickComesFromLink
                        ? (isWall ? "Sélectionne le mur de ta maquette" : "Sélectionne le sol de ta maquette")
                        : (isWall ? "Sélectionne le mur" : "Sélectionne le sol");

                    var rHost = uiDoc.Selection.PickObject(ObjectType.Element,
                        new LocalHostSelectionFilter(win.SelectedHost),
                        hostPrompt + " (ESC pour annuler)");
                    host = doc.GetElement(rHost);
                }
                catch
                {
                    break;
                }

                if (isWall && host is not Wall)
                {
                    TaskDialog.Show("Erreur", "Ce n’est pas un mur.");
                    continue;
                }
                if (!isWall && host is not Floor)
                {
                    TaskDialog.Show("Erreur", "Ce n’est pas un sol.");
                    continue;
                }

                var level = doc.GetElement(host.LevelId) as Level
                            ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                if (picked.Count > 1 && isRect)
                {
                    CreateRectReservation_Multi(
                        doc,
                        host,
                        level,
                        reservationSymbol,
                        win.SelectedObject,
                        picked.Select(x => (x.Element, x.TransformToCurrentDocument)).ToList(),
                        cfg,
                        prof,
                        isWall,
                        win.NormeEnabled);
                }
                else
                {
                    var selected = picked.First();
                    CreateReservation_Single(
                        doc,
                        host,
                        level,
                        reservationSymbol,
                        win.SelectedObject,
                        selected.Element,
                        selected.TransformToCurrentDocument,
                        cfg,
                        prof,
                        isWall,
                        isRect,
                        win.NormeEnabled);
                }

                var td = new TaskDialog("BIMaestro")
                {
                    MainInstruction = "Créer une autre réservation ?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                if (td.Show() != TaskDialogResult.Yes)
                    break;
            }
        }

        // =========================
        // AUTO (mur only)
        // =========================
        private void RunAutomatic(Document doc,
            ReservationAutoV3Window win,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            FamilySymbol reservationSymbol)
        {
            var walls = GetAutomaticWallCandidates(doc);

            if (!walls.Any())
            {
                TaskDialog.Show("BIMaestro", "Aucun mur trouvé.");
                return;
            }

            List<Element> targets = win.SelectedObject switch
            {
                ReservationAutoV3Window.ObjectType.Canalisation => new FilteredElementCollector(doc)
                    .OfClass(typeof(Pipe))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Gaine => new FilteredElementCollector(doc)
                    .OfClass(typeof(Duct))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Porte => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<Element>()
                    .ToList(),

                ReservationAutoV3Window.ObjectType.Fenetre => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<Element>()
                    .ToList(),

                _ => new List<Element>()
            };

            if (!targets.Any())
            {
                TaskDialog.Show("BIMaestro", "Aucun objet trouvé.");
                return;
            }

            int created = 0;
            int failedPlacements = 0;

            foreach (var wall in walls)
            {
                var bbWall = wall.BoundingBoxInCurrentDocument;
                if (bbWall == null) continue;

                foreach (var el in targets)
                {
                    var bbEl = el.get_BoundingBox(null);
                    if (bbEl == null) continue;

                    var bbInt = IntersectBoundingBoxes(bbWall, bbEl);
                    if (bbInt == null) continue;

                    XYZ center = (bbInt.Min + bbInt.Max) * 0.5;
                    center = ProjectPointOntoWallPlane(wall, center);

                    var level = wall.CanHost
                        ? doc.GetElement(((Wall)wall.Element).LevelId) as Level
                        : GetNearestLevel(doc, center.Z);

                    level = level ?? new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault();

                    FamilyInstance fi = wall.CanHost
                        ? doc.Create.NewFamilyInstance(
                            center,
                            reservationSymbol,
                            wall.Element,
                            level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                        : CreateUnhostedFamilyInstance(doc, center, reservationSymbol, level);

                    if (fi == null)
                    {
                        failedPlacements++;
                        continue;
                    }

                    AlignReservationOrientationIfNeeded(doc, fi, reservationSymbol, wall.Element, center, wall.AxisX);

                    ApplySizing(
                        fi,
                        wall.Element,
                        bbInt,
                        cfg,
                        prof,
                        win.SelectedObject,
                        el,
                        Transform.Identity,
                        win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire,
                        win.NormeEnabled,
                        wall.AxisX,
                        wall.DepthFt);

                    ApplyVerticalPlacementCorrection(
                        doc,
                        fi,
                        reservationSymbol,
                        wall.Element,
                        prof,
                        bbInt,
                        cfg,
                        win.SelectedObject,
                        el,
                        win.SelectedShape == ReservationAutoV3Window.ShapeTarget.Rectangulaire,
                        win.NormeEnabled);

                    if (wall.CanHost)
                        ForceVoidCutSafe(doc, wall.Element, fi);

                    created++;
                }
            }

            string message = $"Réservations créées : {created}";
            if (failedPlacements > 0)
            {
                message += "\n\nNon créées : " + failedPlacements +
                           "\nLa famille configurée semble nécessiter un hôte. Pour les murs d'un lien, utilise une famille de réservation non hébergée.";
            }

            TaskDialog.Show("BIMaestro", message);
        }

        // =========================
        // Picking helpers
        // =========================
        private class MepSelection
        {
            public Element Element { get; set; }
            public Transform TransformToCurrentDocument { get; set; } = Transform.Identity;
            public bool IsLinked { get; set; }
        }

        private List<MepSelection> PickElements(UIDocument uiDoc, Document doc, ReservationAutoV3Window win, bool multi)
        {
            if (!multi)
            {
                Reference r;
                if (win.SelectedObject == ReservationAutoV3Window.ObjectType.Canalisation
                    || win.SelectedObject == ReservationAutoV3Window.ObjectType.Gaine)
                    r = PickSingleMepCurveBySource(
                        uiDoc,
                        doc,
                        win.SelectedPipeSource,
                        win.SelectedObject,
                        win.DoubleLinkEnabled);
                else
                    r = uiDoc.Selection.PickObject(ObjectType.Element, "Sélectionne l’objet (ESC pour annuler)");

                if (!TryResolveReference(uiDoc, r, out var el, out var tr, out bool isLinked))
                    return new List<MepSelection>();

                return new List<MepSelection>
                {
                    new MepSelection
                    {
                        Element = el,
                        TransformToCurrentDocument = tr,
                        IsLinked = isLinked
                    }
                };
            }
            else
            {
                IList<Reference> refs = GetMepCurveReferencesBySource(
                    uiDoc,
                    doc,
                    win.SelectedPipeSource,
                    win.SelectedObject,
                    win.DoubleLinkEnabled);

                var list = new List<MepSelection>();
                foreach (var rr in refs)
                {
                    if (TryResolveReference(uiDoc, rr, out var el, out var tr, out bool isLinked))
                    {
                        list.Add(new MepSelection
                        {
                            Element = el,
                            TransformToCurrentDocument = tr,
                            IsLinked = isLinked
                        });
                    }
                }
                return list;
            }
        }

        private class WallScanCandidate
        {
            public Element Element { get; set; }
            public BoundingBoxXYZ BoundingBoxInCurrentDocument { get; set; }
            public Transform TransformToCurrentDocument { get; set; } = Transform.Identity;
            public bool IsLinked { get; set; }
            public XYZ AxisX { get; set; } = XYZ.BasisX;
            public double DepthFt { get; set; }
            public XYZ PickPointInCurrentDocument { get; set; }
            public bool IsFloorLike { get; set; }

            public bool CanHost => !IsLinked && Element is Wall;
        }

        private List<WallScanCandidate> GetAutomaticWallCandidates(Document doc)
        {
            return CollectWallCandidates(doc, Transform.Identity, isLinked: false);
        }

        private static List<WallScanCandidate> CollectWallCandidates(Document sourceDoc, Transform transformToCurrentDoc, bool isLinked)
        {
            var result = new List<WallScanCandidate>();
            if (sourceDoc == null) return result;

            Transform tr = transformToCurrentDoc ?? Transform.Identity;

            var wallElements = new FilteredElementCollector(sourceDoc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var element in wallElements)
            {
                var bb = GetBoundingBoxInHostCoordinates(element, tr);
                if (bb == null) continue;

                XYZ axis = element is Wall wall
                    ? GetWallDirectionXY(wall, tr)
                    : EstimateWallAxisXY(bb);

                double depth = element is Wall typedWall
                    ? GetHostDepth(typedWall)
                    : EstimateWallDepth(bb);

                result.Add(new WallScanCandidate
                {
                    Element = element,
                    BoundingBoxInCurrentDocument = bb,
                    TransformToCurrentDocument = tr,
                    IsLinked = isLinked,
                    AxisX = axis,
                    DepthFt = depth
                });
            }

            return result;
        }

        private static WallScanCandidate PickLinkedHostCandidate(
            UIDocument uiDoc,
            Document doc,
            ReservationAutoV3Window.PipeSource source,
            ReservationAutoV3Window.HostTarget hostTarget,
            bool allowAnyLink = false)
        {
            try
            {
                string hostLabel = hostTarget == ReservationAutoV3Window.HostTarget.Sol ? "sol" : "mur";
                Reference reference = uiDoc.Selection.PickObject(
                    ObjectType.LinkedElement,
                    new LinkHostSelectionFilter(doc, source, hostTarget, allowAnyLink),
                    $"Sélectionne le {hostLabel} du lien (ESC pour annuler)");

                if (reference == null || reference.LinkedElementId == ElementId.InvalidElementId)
                    return null;

                var linkInstance = doc.GetElement(reference.ElementId) as RevitLinkInstance;
                Document linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null)
                    return null;

                Element linkedHost = linkDoc.GetElement(reference.LinkedElementId);
                if (linkedHost == null)
                    return null;

                Transform tr = linkInstance.GetTotalTransform();
                var bb = GetBoundingBoxInHostCoordinates(linkedHost, tr);
                if (bb == null)
                    return null;

                XYZ axis = linkedHost is Wall wall
                    ? GetWallDirectionXY(wall, tr)
                    : EstimateWallAxisXY(bb);

                double depth = linkedHost is Wall || linkedHost is Floor
                    ? GetHostDepth(linkedHost)
                    : EstimateWallDepth(bb);

                XYZ pickPoint = NormalizeLinkedReferencePoint(reference.GlobalPoint, tr, bb);

                return new WallScanCandidate
                {
                    Element = linkedHost,
                    BoundingBoxInCurrentDocument = bb,
                    TransformToCurrentDocument = tr,
                    IsLinked = true,
                    AxisX = axis,
                    DepthFt = depth,
                    PickPointInCurrentDocument = pickPoint,
                    IsFloorLike = hostTarget == ReservationAutoV3Window.HostTarget.Sol || IsFloorLikeElement(linkedHost)
                };
            }
            catch
            {
                return null;
            }
        }

        private static FamilyInstance CreateUnhostedFamilyInstance(Document doc, XYZ center, FamilySymbol symbol, Level level)
        {
            if (doc == null || center == null || symbol == null) return null;

            try
            {
                return doc.Create.NewFamilyInstance(
                    center,
                    symbol,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            catch
            {
            }

            try
            {
                if (level != null)
                {
                    return doc.Create.NewFamilyInstance(
                        center,
                        symbol,
                        level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                }
            }
            catch
            {
            }

            return null;
        }

        private static void MoveInstanceToPoint(Document doc, FamilyInstance fi, XYZ target)
        {
            if (doc == null || fi == null || target == null)
                return;

            if (fi.Location is not LocationPoint location || location.Point == null)
                return;

            XYZ delta = target - location.Point;
            if (delta.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, fi.Id, delta);
        }

        private static void CenterUnhostedReservationOnPoint(Document doc, FamilyInstance fi, XYZ target)
        {
            if (doc == null || fi == null || target == null)
                return;

            try
            {
                doc.Regenerate();

                BoundingBoxXYZ boundingBox = ToWorldBoundingBox(fi.get_BoundingBox(null));
                if (boundingBox == null)
                {
                    MoveInstanceToPoint(doc, fi, target);
                    return;
                }

                XYZ geometryCenter = (boundingBox.Min + boundingBox.Max) * 0.5;
                XYZ delta = target - geometryCenter;
                if (delta.GetLength() > 1e-9)
                    ElementTransformUtils.MoveElement(doc, fi.Id, delta);
            }
            catch
            {
                MoveInstanceToPoint(doc, fi, target);
            }
        }

        private static Level GetNearestLevel(Document doc, double z)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z))
                .FirstOrDefault();
        }

        // =========================
        // Create reservations
        // =========================
        private void CreateReservation_Single(Document doc,
            Element host, Level level, FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            Element el, Transform trToHost,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            bool isWall, bool isRect, bool normeEnabled)
        {
            var bbHost = host.get_BoundingBox(null);
            var bbEl = GetBoundingBoxInHostCoordinates(el, trToHost);
            if (bbHost == null || bbEl == null) return;

            var bbInt = IntersectBoundingBoxes(bbHost, bbEl);
            if (bbInt == null) return;

            XYZ center = (bbInt.Min + bbInt.Max) * 0.5;
            bool isMepCurve = objType == ReservationAutoV3Window.ObjectType.Canalisation
                              || objType == ReservationAutoV3Window.ObjectType.Gaine;

            if (isMepCurve && TryGetElementHostIntersectionCenter(
                    el,
                    trToHost,
                    host,
                    Transform.Identity,
                    out XYZ exactCenter))
            {
                center = exactCenter;
            }

            if (isWall && host is Wall w)
                center = ProjectPointOntoWallPlane(w, center);
            else if (!isWall && host is Floor f)
                center = GetPlacementPointOnFloor(f, center);

            var fi = doc.Create.NewFamilyInstance(
                center, sym, host, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignReservationOrientationIfNeeded(doc, fi, sym, host, center,
                isWall ? GetWallDirectionXY(host as Wall) : GetElementDirectionXY(el, trToHost));

            ApplySizing(fi, host, bbInt, cfg, prof, objType, el, trToHost, isRect, normeEnabled);

            ApplyVerticalPlacementCorrection(doc, fi, sym, host, prof, bbInt, cfg, objType, el, isRect, normeEnabled);

            ForceVoidCutSafe(doc, host, fi);
        }

        private void CreateReservation_SingleAgainstCandidate(Document doc,
            WallScanCandidate host,
            FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            MepSelection selected,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            bool isWall,
            bool isRect,
            bool normeEnabled)
        {
            if (doc == null || host == null || sym == null || selected?.Element == null)
                return;

            var bbHost = host.BoundingBoxInCurrentDocument;
            var bbEl = GetBoundingBoxInHostCoordinates(selected.Element, selected.TransformToCurrentDocument);
            if (bbHost == null || bbEl == null) return;

            var bbInt = IntersectBoundingBoxes(bbHost, bbEl);
            if (bbInt == null) return;

            XYZ center;
            if (!TryGetLinkedHostPlacementPoint(selected, host, out center))
            {
                center = (bbInt.Min + bbInt.Max) * 0.5;
                center = isWall
                    ? ProjectPointOntoWallPlane(host, center)
                    : ProjectPointOntoFloorPlane(host, center);
            }

            var level = GetNearestLevel(doc, center.Z)
                        ?? new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .Cast<Level>()
                            .FirstOrDefault();

            FamilyInstance fi = host.CanHost
                ? doc.Create.NewFamilyInstance(
                    center,
                    sym,
                    host.Element,
                    level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                : CreateUnhostedFamilyInstance(doc, center, sym, level);

            if (fi == null)
            {
                TaskDialog.Show(
                    "BIMaestro",
                    $"Impossible de créer la réservation sans hôte.\nUtilise une famille de réservation non hébergée pour traverser un {(isWall ? "mur" : "sol")} de lien.");
                return;
            }

            AlignReservationOrientationIfNeeded(doc, fi, sym, host.Element, center,
                isWall ? host.AxisX : GetElementDirectionXY(selected.Element, selected.TransformToCurrentDocument));

            ApplySizing(
                fi,
                host.Element,
                bbInt,
                cfg,
                prof,
                objType,
                selected.Element,
                selected.TransformToCurrentDocument,
                isRect,
                normeEnabled,
                isWall ? host.AxisX : null,
                host.DepthFt,
                floorOverride: !isWall,
                hostTransformToCurrentDocument: host.TransformToCurrentDocument);

            if (host.CanHost)
            {
                ApplyVerticalPlacementCorrection(
                    doc,
                    fi,
                    sym,
                    host.Element,
                    prof,
                    bbInt,
                    cfg,
                    objType,
                    selected.Element,
                    isRect,
                    normeEnabled);

                ForceVoidCutSafe(doc, host.Element, fi);
            }
            else
            {
                CenterUnhostedReservationOnPoint(doc, fi, center);
            }
        }

        private void CreateRectReservation_Multi(Document doc,
            Element host, Level level, FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            List<(Element el, Transform tr)> elems,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            bool isWall, bool normeEnabled)
        {
            var bbHost = host.get_BoundingBox(null);
            if (bbHost == null) return;

            var clipped = elems
                .Select(t => GetBoundingBoxInHostCoordinates(t.el, t.tr))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bbHost, bb))
                .Where(bb => bb != null)
                .ToList();

            if (!clipped.Any()) return;

            double minX = clipped.Min(bb => bb.Min.X);
            double minY = clipped.Min(bb => bb.Min.Y);
            double minZ = clipped.Min(bb => bb.Min.Z);
            double maxX = clipped.Max(bb => bb.Max.X);
            double maxY = clipped.Max(bb => bb.Max.Y);
            double maxZ = clipped.Max(bb => bb.Max.Z);

            var unionInt = new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };

            XYZ center = (unionInt.Min + unionInt.Max) * 0.5;

            if (isWall && host is Wall w)
                center = ProjectPointOntoWallPlane(w, center);
            else if (!isWall && host is Floor f)
                center = GetPlacementPointOnFloor(f, center);

            var fi = doc.Create.NewFamilyInstance(
                center, sym, host, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignReservationOrientationIfNeeded(doc, fi, sym, host, center,
               isWall
                   ? GetWallDirectionXY(host as Wall)
                   : GetPreferredFloorAxisXY(unionInt, elems));

            ApplySizing_MultiRect(fi, host, unionInt, cfg, prof, objType, normeEnabled);

            ApplyVerticalPlacementCorrection(doc, fi, sym, host, prof, unionInt, cfg, objType, null, true, normeEnabled);

            ForceVoidCutSafe(doc, host, fi);
        }

        private void CreateRectReservation_MultiAgainstCandidate(Document doc,
            WallScanCandidate host,
            FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            List<MepSelection> selectedElements,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            bool normeEnabled)
        {
            if (doc == null || host == null || sym == null || selectedElements == null || selectedElements.Count == 0)
                return;

            var bbHost = host.BoundingBoxInCurrentDocument;
            if (bbHost == null) return;

            var clipped = new List<BoundingBoxXYZ>();

            foreach (var selected in selectedElements)
            {
                if (selected?.Element == null) continue;

                var bbEl = GetBoundingBoxInHostCoordinates(selected.Element, selected.TransformToCurrentDocument);
                if (bbEl == null) continue;

                var bbInt = IntersectBoundingBoxes(bbHost, bbEl);
                if (bbInt == null) continue;

                clipped.Add(bbInt);
            }

            if (!clipped.Any()) return;

            double minX = clipped.Min(bb => bb.Min.X);
            double minY = clipped.Min(bb => bb.Min.Y);
            double minZ = clipped.Min(bb => bb.Min.Z);
            double maxX = clipped.Max(bb => bb.Max.X);
            double maxY = clipped.Max(bb => bb.Max.Y);
            double maxZ = clipped.Max(bb => bb.Max.Z);

            var unionInt = new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };

            XYZ center = GetMultiReservationCenterOnCandidate(host, unionInt, selectedElements);

            var level = GetNearestLevel(doc, center.Z)
                        ?? new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .Cast<Level>()
                            .FirstOrDefault();

            FamilyInstance fi = host.CanHost
                ? doc.Create.NewFamilyInstance(
                    center,
                    sym,
                    host.Element,
                    level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                : CreateUnhostedFamilyInstance(doc, center, sym, level);

            if (fi == null)
            {
                TaskDialog.Show(
                    "BIMaestro",
                    "Impossible de créer la réservation multi sans hôte.\nUtilise une famille de réservation non hébergée pour traverser un mur de lien.");
                return;
            }

            AlignReservationOrientationIfNeeded(doc, fi, sym, host.Element, center, host.AxisX);

            ApplySizing_MultiRect(
                fi,
                host.Element,
                unionInt,
                cfg,
                prof,
                objType,
                normeEnabled,
                host.AxisX,
                host.DepthFt);

            if (host.CanHost)
            {
                ApplyVerticalPlacementCorrection(doc, fi, sym, host.Element, prof, unionInt, cfg, objType, null, true, normeEnabled);
                ForceVoidCutSafe(doc, host.Element, fi);
            }
            else
            {
                CenterUnhostedReservationOnPoint(doc, fi, center);
            }
        }

        private void CreateRectReservation_MultiAgainstFloorCandidate(Document doc,
            WallScanCandidate host,
            FamilySymbol sym,
            ReservationAutoV3Window.ObjectType objType,
            List<MepSelection> selectedElements,
            ReservationAutoV3Config cfg,
            ProfileConfig prof,
            bool normeEnabled)
        {
            if (doc == null || host == null || sym == null || selectedElements == null || selectedElements.Count == 0)
                return;

            BoundingBoxXYZ floorBoundingBox = host.BoundingBoxInCurrentDocument;
            if (floorBoundingBox == null)
                return;

            var clipped = new List<BoundingBoxXYZ>();
            foreach (MepSelection selected in selectedElements)
            {
                if (selected?.Element == null)
                    continue;

                BoundingBoxXYZ elementBoundingBox = GetBoundingBoxInHostCoordinates(
                    selected.Element,
                    selected.TransformToCurrentDocument);
                BoundingBoxXYZ intersection = IntersectBoundingBoxes(floorBoundingBox, elementBoundingBox);
                if (intersection != null)
                    clipped.Add(intersection);
            }

            if (clipped.Count == 0)
                return;

            var unionIntersect = new BoundingBoxXYZ
            {
                Min = new XYZ(
                    clipped.Min(bb => bb.Min.X),
                    clipped.Min(bb => bb.Min.Y),
                    clipped.Min(bb => bb.Min.Z)),
                Max = new XYZ(
                    clipped.Max(bb => bb.Max.X),
                    clipped.Max(bb => bb.Max.Y),
                    clipped.Max(bb => bb.Max.Z))
            };

            XYZ center = (unionIntersect.Min + unionIntersect.Max) * 0.5;
            var crossingPoints = new List<XYZ>();
            foreach (MepSelection selected in selectedElements)
            {
                if (TryGetLinkedHostPlacementPoint(selected, host, out XYZ crossingPoint) && crossingPoint != null)
                    crossingPoints.Add(crossingPoint);
            }

            center = crossingPoints.Count > 0
                ? new XYZ(center.X, center.Y, crossingPoints.Average(p => p.Z))
                : ProjectPointOntoFloorPlane(host, center);

            Level level = GetNearestLevel(doc, center.Z)
                          ?? new FilteredElementCollector(doc)
                              .OfClass(typeof(Level))
                              .Cast<Level>()
                              .FirstOrDefault();

            FamilyInstance fi = CreateUnhostedFamilyInstance(doc, center, sym, level);
            if (fi == null)
            {
                TaskDialog.Show(
                    "BIMaestro",
                    "Impossible de créer la réservation multi sans hôte.\nUtilise une famille de réservation non hébergée pour traverser un sol de lien.");
                return;
            }

            var elements = selectedElements
                .Where(selected => selected?.Element != null)
                .Select(selected => (selected.Element, selected.TransformToCurrentDocument))
                .ToList();

            XYZ floorAxis = GetPreferredFloorAxisXY(unionIntersect, elements);
            AlignReservationOrientationIfNeeded(doc, fi, sym, host.Element, center, floorAxis);

            ApplySizing_MultiRect(
                fi,
                host.Element,
                unionIntersect,
                cfg,
                prof,
                objType,
                normeEnabled,
                wallAxisOverride: null,
                depthOverrideFt: host.DepthFt,
                floorOverride: true);

            CenterUnhostedReservationOnPoint(doc, fi, center);
        }

        // =========================
        // Sizing
        // =========================
        private void ApplySizing(FamilyInstance fi,
            Element host,
            BoundingBoxXYZ bbIntersect,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            ReservationAutoV3Window.ObjectType objType,
            Element intersecting, Transform trToHost,
            bool isRect, bool normeEnabled,
            XYZ wallAxisOverride = null,
            double? depthOverrideFt = null,
            bool floorOverride = false,
            Transform hostTransformToCurrentDocument = null)
        {
            if (fi == null || host == null || bbIntersect == null) return;

            bool isPipeOrDuct = objType == ReservationAutoV3Window.ObjectType.Canalisation
                                || objType == ReservationAutoV3Window.ObjectType.Gaine;

            double oversizeFt = 0.0;

            double depthFt = depthOverrideFt ?? GetHostDepth(host);

            var world = ToWorldBoundingBox(bbIntersect);
            if (world == null) return;

            if (!isRect)
            {
                double diamFt = CalculateDiameterForElement(intersecting, objType, oversizeFt);
                if (diamFt <= 1e-9)
                {
                    diamFt = CalculateFallbackDiameter(host, world, depthFt, oversizeFt);
                }

                if (normeEnabled) diamFt = RoundUpToNext50mm(diamFt);

                TrySet(fi, prof.ParamDiameter, diamFt, "Diamètre", "COM_Diamètre", "Diameter");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
                return;
            }

            var typedWall = host as Wall;
            if (typedWall != null || wallAxisOverride != null)
            {
                XYZ wallDir = wallAxisOverride ?? GetWallDirection(typedWall);
                double len;
                double hgt;

                if (isPipeOrDuct && TryCalculateWallOpeningSizeFromSolidIntersection(
                        host,
                        hostTransformToCurrentDocument,
                        intersecting,
                        trToHost,
                        objType,
                        wallDir,
                        out double exactLength,
                        out double exactHeight))
                {
                    len = exactLength + oversizeFt;
                    hgt = exactHeight + oversizeFt;
                }
                else
                {
                    var corners = new List<XYZ>
                    {
                        new XYZ(world.Min.X, world.Min.Y, world.Min.Z),
                        new XYZ(world.Min.X, world.Max.Y, world.Min.Z),
                        new XYZ(world.Max.X, world.Min.Y, world.Min.Z),
                        new XYZ(world.Max.X, world.Max.Y, world.Min.Z)
                    };
                    var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();

                    len = (projs.Max() - projs.Min()) + oversizeFt;
                    hgt = (world.Max.Z - world.Min.Z) + oversizeFt;
                }

                if (normeEnabled)
                {
                    len = RoundUpToNext50mm(len);
                    hgt = RoundUpToNext50mm(hgt);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Largeur", "COM_Largeur", "Length", "Width");
                TrySet(fi, prof.ParamHeight, hgt, "Hauteur", "COM_Hauteur", "Height");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
            else if (host is Floor || floorOverride)
            {
                double len = (world.Max.X - world.Min.X) + oversizeFt;
                double wid = (world.Max.Y - world.Min.Y) + oversizeFt;

                if (normeEnabled)
                {
                    len = RoundUpToNext50mm(len);
                    wid = RoundUpToNext50mm(wid);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Length");
                TrySet(fi, prof.ParamWidth, wid, "Largeur", "COM_Largeur", "Width");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
        }

        private void ApplySizing_MultiRect(FamilyInstance fi,
            Element host,
            BoundingBoxXYZ unionIntersect,
            ReservationAutoV3Config cfg, ProfileConfig prof,
            ReservationAutoV3Window.ObjectType objType,
            bool normeEnabled,
            XYZ wallAxisOverride = null,
            double? depthOverrideFt = null,
            bool floorOverride = false)
        {
            if (fi == null || host == null || unionIntersect == null) return;
            double depthFt = depthOverrideFt ?? GetHostDepth(host);

            var world = ToWorldBoundingBox(unionIntersect);
            if (world == null) return;

            var typedWall = host as Wall;
            if (typedWall != null || wallAxisOverride != null)
            {
                XYZ wallDir = wallAxisOverride ?? GetWallDirection(typedWall);

                var corners = new List<XYZ>
                {
                    new XYZ(world.Min.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Min.X, world.Max.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Min.Y, world.Min.Z),
                    new XYZ(world.Max.X, world.Max.Y, world.Min.Z)
                };
                var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();

                double len = projs.Max() - projs.Min();
                double hgt = world.Max.Z - world.Min.Z;

                if (normeEnabled)
                {
                    len = RoundUpToNext50mm(len);
                    hgt = RoundUpToNext50mm(hgt);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Largeur", "COM_Largeur", "Length", "Width");
                TrySet(fi, prof.ParamHeight, hgt, "Hauteur", "COM_Hauteur", "Height");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
            else if (host is Floor || floorOverride)
            {
                double len = world.Max.X - world.Min.X;
                double wid = world.Max.Y - world.Min.Y;

                if (normeEnabled)
                {
                    len = RoundUpToNext50mm(len);
                    wid = RoundUpToNext50mm(wid);
                }

                TrySet(fi, prof.ParamLength, len, "Longueur", "COM_Longueur", "Length");
                TrySet(fi, prof.ParamWidth, wid, "Largeur", "COM_Largeur", "Width");
                TrySet(fi, prof.ParamDepth, depthFt, "Profondeur", "COM_Profondeur", "Depth");
            }
        }

        private static bool TrySet(FamilyInstance fi, string preferredName, double val, params string[] fallbacks)
        {
            bool Try(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                var p = fi.LookupParameter(name);
                if (p != null && !p.IsReadOnly)
                {
                    p.Set(val);
                    return true;
                }
                return false;
            }

            if (Try(preferredName)) return true;
            foreach (var fb in fallbacks ?? Array.Empty<string>())
                if (Try(fb)) return true;

            return false;
        }

        // =========================
        // CORRECTION DE POSITION VERTICALE
        // =========================
        private void ApplyVerticalPlacementCorrection(
    Document doc,
    FamilyInstance fi,
    FamilySymbol symbol,
    Element host,
    ProfileConfig prof,
    BoundingBoxXYZ bbIntersect,
    ReservationAutoV3Config cfg,
    ReservationAutoV3Window.ObjectType objType,
    Element intersecting,
    bool isRect,
    bool normeEnabled)
        {
            if (doc == null || fi == null || symbol == null || host == null || prof == null || bbIntersect == null)
                return;

            // ✅ IMPORTANT :
            // on n'applique la logique de placement vertical QUE sur les profils perso
            bool isPersoProfile = string.IsNullOrWhiteSpace(prof.TypeName)
                ? false
                : prof.FamilyName != null && (
                    prof.FamilyName.IndexOf("perso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    prof.TypeName.IndexOf("perso", StringComparison.OrdinalIgnoreCase) >= 0);

            // En pratique le plus fiable n'est pas le nom "perso" mais le fait que le profil
            // vienne d'une config utilisateur. Comme ici on ne reçoit pas directement cette info,
            // on protège au moins les V1/V2 connues.
            bool isBuiltInKnown =
                FamilyNameContains(prof.FamilyName, "CML_Réservation rectangulaire verticale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation rectangulaire horizontale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation circulaire verticale") ||
                FamilyNameContains(prof.FamilyName, "CML_Réservation circulaire horizontale") ||
                FamilyNameContains(prof.FamilyName, "Réservation rectangulaire murale") ||
                FamilyNameContains(prof.FamilyName, "Réservation rectangulaire sol") ||
                FamilyNameContains(prof.FamilyName, "Réservation circulaire murale") ||
                FamilyNameContains(prof.FamilyName, "Réservation circulaire sol");

            if (isBuiltInKnown)
                return;

            VerticalPlacementMode mode = ResolveVerticalPlacementMode(symbol, prof);

            // si on est sur Auto sans décalage manuel et qu'on ne veut pas impacter les cas standards,
            // on peut sortir si l'auto conclut "Center"
            double verticalSizeFt = ComputeReferenceVerticalSize(host, bbIntersect, cfg, objType, intersecting, isRect, normeEnabled);
            double shiftFt = MmToFt(prof.VerticalPlacementOffsetMm);

            switch (mode)
            {
                // ✅ INVERSION CORRIGÉE PAR RAPPORT À MA VERSION PRÉCÉDENTE
                // Si la référence de famille est en bas, il faut monter la famille
                // et ton test montre que c'est actuellement "Haut" qui donne le bon résultat.
                case VerticalPlacementMode.Top:
                    shiftFt += verticalSizeFt * 0.5;
                    break;

                // Si la référence est en haut, il faut descendre la famille
                case VerticalPlacementMode.Bottom:
                    shiftFt -= verticalSizeFt * 0.5;
                    break;

                case VerticalPlacementMode.Center:
                case VerticalPlacementMode.Auto:
                default:
                    break;
            }

            if (Math.Abs(shiftFt) < 1e-9)
                return;

            ElementTransformUtils.MoveElement(doc, fi.Id, XYZ.BasisZ * shiftFt);
        }

        private VerticalPlacementMode ResolveVerticalPlacementMode(FamilySymbol symbol, ProfileConfig prof)
        {
            if (prof == null)
                return VerticalPlacementMode.Center;

            if (prof.VerticalPlacementMode != VerticalPlacementMode.Auto)
                return prof.VerticalPlacementMode;

            return DetectVerticalPlacementModeFromSymbol(symbol);
        }

        private VerticalPlacementMode DetectVerticalPlacementModeFromSymbol(FamilySymbol symbol)
{
    try
    {
        if (symbol == null)
            return VerticalPlacementMode.Center;

        var opts = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement geo = symbol.get_Geometry(opts);
        if (geo == null)
            return VerticalPlacementMode.Center;

        bool found = false;
        double minZ = double.MaxValue;
        double maxZ = double.MinValue;

        void Accumulate(GeometryElement g)
        {
            if (g == null) return;

            foreach (GeometryObject obj in g)
            {
                if (obj is GeometryInstance gi)
                {
                    Accumulate(gi.GetInstanceGeometry());
                    continue;
                }

                BoundingBoxXYZ bb = GetGeometryBoundingBox(obj);
                if (bb == null) continue;

                Transform t = bb.Transform ?? Transform.Identity;

                var corners = new[]
                {
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                }.Select(p => t.OfPoint(p));

                foreach (var p in corners)
                {
                    found = true;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }
            }
        }

        Accumulate(geo);

        if (!found)
            return VerticalPlacementMode.Center;

        double centerZ = (minZ + maxZ) * 0.5;
        double height = maxZ - minZ;
        if (height < 1e-9)
            return VerticalPlacementMode.Center;

        double tolerance = height * 0.15;

        if (Math.Abs(centerZ) <= tolerance)
            return VerticalPlacementMode.Center;

        if (minZ >= -tolerance && maxZ > tolerance)
            return VerticalPlacementMode.Bottom;

        if (maxZ <= tolerance && minZ < -tolerance)
            return VerticalPlacementMode.Top;

        if (Math.Abs(minZ) < Math.Abs(maxZ))
            return VerticalPlacementMode.Bottom;

        if (Math.Abs(maxZ) < Math.Abs(minZ))
            return VerticalPlacementMode.Top;

        return VerticalPlacementMode.Center;
    }
    catch
    {
        return VerticalPlacementMode.Center;
    }
}

        private BoundingBoxXYZ GetGeometryBoundingBox(GeometryObject obj)
        {
            throw new NotImplementedException();
        }

        private double ComputeReferenceVerticalSize(
            Element host,
            BoundingBoxXYZ bbIntersect,
            ReservationAutoV3Config cfg,
            ReservationAutoV3Window.ObjectType objType,
            Element intersecting,
            bool isRect,
            bool normeEnabled)
        {
            if (host == null || bbIntersect == null)
                return 0.0;

            double depthFt = GetHostDepth(host);

            if (host is Floor)
                return depthFt;

            var world = ToWorldBoundingBox(bbIntersect);
            if (world == null)
                return 0.0;

            if (!isRect)
            {
                double diamFt = CalculateDiameterForElement(intersecting, objType, 0.0);
                if (diamFt <= 1e-9)
                    diamFt = CalculateFallbackDiameter(host, world, depthFt, 0.0);

                if (normeEnabled)
                    diamFt = RoundUpToNext50mm(diamFt);

                return diamFt;
            }

            double hgt = world.Max.Z - world.Min.Z;
            if (normeEnabled)
                hgt = RoundUpToNext50mm(hgt);

            return hgt;
        }

        // =========================
        // VOID CUT FORCE
        // =========================
        private void ForceVoidCutSafe(Document doc, Element host, FamilyInstance fi)
        {
            if (doc == null || host == null || fi == null) return;

            if (TryForceVoidCut(doc, host, fi))
                return;

            try
            {
                doc.Regenerate();
            }
            catch { }

            if (TryForceVoidCut(doc, host, fi))
                return;

            if (!_voidCutWarnShown)
            {
                _voidCutWarnShown = true;
                TaskDialog.Show("BIMaestro",
                    "Info découpe (familles 'vide') :\n" +
                    "Certaines familles ne coupent pas si l’option famille n’est pas activée.\n\n" +
                    "Vérifie dans l’éditeur de famille :\n" +
                    "- 'Cut with Voids When Loaded'\n" +
                    "- le vide est bien en 'Cut Geometry'\n" +
                    "- la catégorie/support autorise la coupe.\n\n" +
                    "Le plugin a tenté de forcer la coupe automatiquement.");
            }
        }

        private static bool TryForceVoidCut(Document doc, Element host, FamilyInstance fi)
        {
            try
            {
                var asm = typeof(Element).Assembly;
                var t = asm.GetType("Autodesk.Revit.DB.InstanceVoidCutUtils");
                if (t == null) return false;

                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "AddInstanceVoidCut")
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;

                    bool ok0 = ps[0].ParameterType == typeof(Document);
                    bool ok1 = typeof(Element).IsAssignableFrom(ps[1].ParameterType);
                    bool ok2 = typeof(Element).IsAssignableFrom(ps[2].ParameterType);

                    if (!ok0 || !ok1 || !ok2) continue;

                    m.Invoke(null, new object[] { doc, host, fi });
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        // =========================
        // Host depth
        // =========================
        private static double GetHostDepth(Element host)
        {
            if (host is Wall w) return w.Width;

            if (host is Floor f)
            {
                var p = f.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                if (p != null && p.StorageType == StorageType.Double)
                    return p.AsDouble();

                var bb = f.get_BoundingBox(null);
                if (bb != null)
                    return Math.Abs(bb.Max.Z - bb.Min.Z);
            }

            return 0.0;
        }

        // =========================
        // Geometry helpers
        // =========================
        private static double MmToFt(double mm) => mm / 304.8;

        private static BoundingBoxXYZ IntersectBoundingBoxes(BoundingBoxXYZ bb1, BoundingBoxXYZ bb2)
        {
            var w1 = ToWorldBoundingBox(bb1);
            var w2 = ToWorldBoundingBox(bb2);
            if (w1 == null || w2 == null) return null;

            double minX = Math.Max(w1.Min.X, w2.Min.X);
            double maxX = Math.Min(w1.Max.X, w2.Max.X);
            if (minX > maxX) return null;

            double minY = Math.Max(w1.Min.Y, w2.Min.Y);
            double maxY = Math.Min(w1.Max.Y, w2.Max.Y);
            if (minY > maxY) return null;

            double minZ = Math.Max(w1.Min.Z, w2.Min.Z);
            double maxZ = Math.Min(w1.Max.Z, w2.Max.Z);
            if (minZ > maxZ) return null;

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static BoundingBoxXYZ ToWorldBoundingBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;

            Transform t = bb.Transform ?? Transform.Identity;
            var corners = new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
            }.Select(p => t.OfPoint(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static BoundingBoxXYZ GetBoundingBoxInHostCoordinates(Element elem, Transform transformToHost)
        {
            if (elem == null) return null;

            var bb = elem.get_BoundingBox(null);
            if (bb == null) return null;

            if (transformToHost == null || transformToHost.IsIdentity)
                return bb;

            var corners = new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
            }.Select(p => transformToHost.OfPoint(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static XYZ GetWallDirection(Wall wall)
        {
            if (wall?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                var d = line.Direction;
                if (d != null && d.GetLength() > 1e-9)
                    return d.Normalize();
            }
            return XYZ.BasisX;
        }

        private static XYZ GetWallDirectionXY(Wall wall)
        {
            if (wall?.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ d = line.Direction;
                d = new XYZ(d.X, d.Y, 0.0);
                if (!d.IsZeroLength()) return d.Normalize();
            }

            XYZ n = wall?.Orientation ?? XYZ.BasisY;
            XYZ dir = n.CrossProduct(XYZ.BasisZ);
            dir = new XYZ(dir.X, dir.Y, 0.0);
            if (!dir.IsZeroLength()) return dir.Normalize();

            return XYZ.BasisX;
        }

        private static XYZ GetWallDirectionXY(Wall wall, Transform transformToCurrentDoc)
        {
            XYZ direction = GetWallDirectionXY(wall);
            if (transformToCurrentDoc != null && !transformToCurrentDoc.IsIdentity)
                direction = transformToCurrentDoc.OfVector(direction);

            direction = new XYZ(direction.X, direction.Y, 0.0);
            if (direction.IsZeroLength()) return XYZ.BasisX;
            return direction.Normalize();
        }

        private static bool TryGetLinkedHostPlacementPoint(MepSelection selected, WallScanCandidate host, out XYZ point)
        {
            point = null;
            if (selected?.Element == null || host == null)
                return false;

            Curve curve = GetElementCurveInCurrentDocument(selected.Element, selected.TransformToCurrentDocument);
            if (curve == null)
                return false;

            if (TryGetHostSolidIntersectionCenter(curve, host, out point))
                return true;

            if (host.IsFloorLike)
            {
                if (TryGetFloorPlaneInCurrentDocument(host, out XYZ floorOrigin, out XYZ floorNormal) &&
                    TryIntersectCurveWithPlane(curve, floorOrigin, floorNormal, out point))
                {
                    return true;
                }

                if (host.PickPointInCurrentDocument != null &&
                    TryProjectPointOnCurve(curve, host.PickPointInCurrentDocument, out point))
                {
                    point = ProjectPointOntoFloorPlane(host, point);
                    return true;
                }

                return false;
            }

            if (TryGetWallPlaneInCurrentDocument(host, out XYZ origin, out XYZ normal) &&
                TryIntersectCurveWithPlane(curve, origin, normal, out point))
            {
                return true;
            }

            if (TryGetEstimatedIfcWallPlane(host, out origin, out normal) &&
                TryIntersectCurveWithPlane(curve, origin, normal, out point))
            {
                return true;
            }

            if (host.PickPointInCurrentDocument != null &&
                TryProjectPointOnCurve(curve, host.PickPointInCurrentDocument, out point))
            {
                point = ProjectPointOntoWallPlane(host, point);
                return true;
            }

            return false;
        }

        private static bool TryGetHostSolidIntersectionCenter(
            Curve curveInCurrentDocument,
            WallScanCandidate host,
            out XYZ point)
        {
            point = null;
            if (curveInCurrentDocument == null || host?.Element == null)
                return false;

            try
            {
                if (!TryGetLongestCurveSegmentInsideHost(
                        curveInCurrentDocument,
                        host.Element,
                        host.TransformToCurrentDocument,
                        out Curve segment))
                    return false;

                point = segment.Evaluate(0.5, true);
                return point != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetElementHostIntersectionCenter(
            Element intersecting,
            Transform intersectingTransformToCurrentDocument,
            Element host,
            Transform hostTransformToCurrentDocument,
            out XYZ point)
        {
            point = null;
            if (intersecting == null || host == null)
                return false;

            Curve curve = GetElementCurveInCurrentDocument(
                intersecting,
                intersectingTransformToCurrentDocument);
            if (curve != null && TryGetLongestCurveSegmentInsideHost(
                    curve,
                    host,
                    hostTransformToCurrentDocument,
                    out Curve segment))
            {
                try
                {
                    point = segment.Evaluate(0.5, true);
                    if (point != null)
                        return true;
                }
                catch
                {
                }
            }

            List<Solid> hostSolids = GetElementSolidsInCurrentDocument(
                host,
                hostTransformToCurrentDocument);
            List<Solid> intersectingSolids = GetElementSolidsInCurrentDocument(
                intersecting,
                intersectingTransformToCurrentDocument);

            double totalVolume = 0.0;
            XYZ weightedCenter = XYZ.Zero;

            foreach (Solid hostSolid in hostSolids)
            {
                foreach (Solid intersectingSolid in intersectingSolids)
                {
                    try
                    {
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            hostSolid,
                            intersectingSolid,
                            BooleanOperationsType.Intersect);
                        if (intersection == null || intersection.Volume <= 1e-9)
                            continue;

                        XYZ centroid = intersection.ComputeCentroid();
                        if (centroid == null)
                            continue;

                        totalVolume += intersection.Volume;
                        weightedCenter += centroid * intersection.Volume;
                    }
                    catch
                    {
                    }
                }
            }

            if (totalVolume <= 1e-9)
                return false;

            point = weightedCenter / totalVolume;
            return point != null;
        }

        private static bool TryCalculateWallOpeningSizeFromSolidIntersection(
            Element host,
            Transform hostTransformToCurrentDocument,
            Element intersecting,
            Transform intersectingTransformToCurrentDocument,
            ReservationAutoV3Window.ObjectType objectType,
            XYZ wallAxis,
            out double length,
            out double height)
        {
            length = 0.0;
            height = 0.0;

            if (TryCalculateWallOpeningSizeFromBooleanIntersection(
                    host,
                    hostTransformToCurrentDocument,
                    intersecting,
                    intersectingTransformToCurrentDocument,
                    wallAxis,
                    out length,
                    out height))
            {
                return true;
            }

            double crossSectionSize = CalculateDiameterForElement(intersecting, objectType, 0.0);
            if (crossSectionSize <= 1e-9)
                return false;

            Curve elementCurve = GetElementCurveInCurrentDocument(
                intersecting,
                intersectingTransformToCurrentDocument);
            if (elementCurve == null ||
                !TryGetLongestCurveSegmentInsideHost(
                    elementCurve,
                    host,
                    hostTransformToCurrentDocument,
                    out Curve segment))
            {
                return false;
            }

            XYZ axis = new XYZ(wallAxis?.X ?? 0.0, wallAxis?.Y ?? 0.0, 0.0);
            if (axis.IsZeroLength())
                return false;
            axis = axis.Normalize();

            IList<XYZ> points;
            try
            {
                points = segment.Tessellate();
            }
            catch
            {
                points = new List<XYZ>();
            }

            if (points == null || points.Count < 2)
            {
                try
                {
                    points = new List<XYZ> { segment.GetEndPoint(0), segment.GetEndPoint(1) };
                }
                catch
                {
                    return false;
                }
            }

            double minAlongWall = points.Min(p => p.DotProduct(axis));
            double maxAlongWall = points.Max(p => p.DotProduct(axis));
            double minZ = points.Min(p => p.Z);
            double maxZ = points.Max(p => p.Z);

            length = Math.Max(0.0, maxAlongWall - minAlongWall) + crossSectionSize;
            height = Math.Max(0.0, maxZ - minZ) + crossSectionSize;
            return length > 1e-9 && height > 1e-9;
        }

        private static bool TryCalculateWallOpeningSizeFromBooleanIntersection(
            Element host,
            Transform hostTransformToCurrentDocument,
            Element intersecting,
            Transform intersectingTransformToCurrentDocument,
            XYZ wallAxis,
            out double length,
            out double height)
        {
            length = 0.0;
            height = 0.0;

            XYZ axis = new XYZ(wallAxis?.X ?? 0.0, wallAxis?.Y ?? 0.0, 0.0);
            if (host == null || intersecting == null || axis.IsZeroLength())
                return false;
            axis = axis.Normalize();

            List<Solid> hostSolids = GetElementSolidsInCurrentDocument(
                host,
                hostTransformToCurrentDocument);
            List<Solid> intersectingSolids = GetElementSolidsInCurrentDocument(
                intersecting,
                intersectingTransformToCurrentDocument);

            if (hostSolids.Count == 0 || intersectingSolids.Count == 0)
                return false;

            var intersectionPoints = new List<XYZ>();
            foreach (Solid hostSolid in hostSolids)
            {
                foreach (Solid intersectingSolid in intersectingSolids)
                {
                    Solid intersection;
                    try
                    {
                        intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            hostSolid,
                            intersectingSolid,
                            BooleanOperationsType.Intersect);
                    }
                    catch
                    {
                        continue;
                    }

                    if (intersection == null || intersection.Volume <= 1e-9)
                        continue;

                    CollectSolidPoints(intersection, intersectionPoints);
                }
            }

            if (intersectionPoints.Count < 2)
                return false;

            double minAlongWall = intersectionPoints.Min(p => p.DotProduct(axis));
            double maxAlongWall = intersectionPoints.Max(p => p.DotProduct(axis));
            double minZ = intersectionPoints.Min(p => p.Z);
            double maxZ = intersectionPoints.Max(p => p.Z);

            length = Math.Max(0.0, maxAlongWall - minAlongWall);
            height = Math.Max(0.0, maxZ - minZ);
            return length > 1e-9 && height > 1e-9;
        }

        private static List<Solid> GetElementSolidsInCurrentDocument(
            Element element,
            Transform transformToCurrentDocument)
        {
            var result = new List<Solid>();
            if (element == null)
                return result;

            try
            {
                var options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel = ViewDetailLevel.Fine
                };

                GeometryElement geometry = element.get_Geometry(options);
                if (geometry == null)
                    return result;

                var sourceSolids = new List<Solid>();
                CollectGeometrySolids(geometry, sourceSolids);

                Transform transform = transformToCurrentDocument ?? Transform.Identity;
                foreach (Solid sourceSolid in sourceSolids)
                {
                    if (transform.IsIdentity)
                    {
                        result.Add(sourceSolid);
                        continue;
                    }

                    try
                    {
                        result.Add(SolidUtils.CreateTransformed(sourceSolid, transform));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static void CollectSolidPoints(Solid solid, ICollection<XYZ> points)
        {
            if (solid == null || points == null)
                return;

            int countBefore = points.Count;

            try
            {
                foreach (Edge edge in solid.Edges)
                {
                    IList<XYZ> tessellated = edge.Tessellate();
                    foreach (XYZ point in tessellated)
                    {
                        if (point != null)
                            points.Add(point);
                    }
                }
            }
            catch
            {
            }

            if (points.Count > countBefore)
                return;

            try
            {
                foreach (Face face in solid.Faces)
                {
                    Mesh mesh = face.Triangulate();
                    for (int i = 0; i < mesh.Vertices.Count; i++)
                    {
                        XYZ point = mesh.Vertices[i];
                        if (point != null)
                            points.Add(point);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TryGetLongestCurveSegmentInsideHost(
            Curve curveInCurrentDocument,
            Element host,
            Transform hostTransformToCurrentDocument,
            out Curve bestSegment)
        {
            bestSegment = null;
            if (curveInCurrentDocument == null || host == null)
                return false;

            try
            {
                var options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel = ViewDetailLevel.Fine
                };

                GeometryElement geometry = host.get_Geometry(options);
                if (geometry == null)
                    return false;

                var solids = new List<Solid>();
                CollectGeometrySolids(geometry, solids);
                if (solids.Count == 0)
                    return false;

                Transform transform = hostTransformToCurrentDocument ?? Transform.Identity;
                double bestLength = -1.0;

                foreach (Solid sourceSolid in solids)
                {
                    Solid solid = sourceSolid;
                    if (!transform.IsIdentity)
                    {
                        try
                        {
                            solid = SolidUtils.CreateTransformed(sourceSolid, transform);
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    SolidCurveIntersection intersection;
                    try
                    {
                        intersection = solid.IntersectWithCurve(
                            curveInCurrentDocument,
                            new SolidCurveIntersectionOptions());
                    }
                    catch
                    {
                        continue;
                    }

                    for (int i = 0; i < intersection.SegmentCount; i++)
                    {
                        Curve segment = intersection.GetCurveSegment(i);
                        if (segment == null)
                            continue;

                        double segmentLength;
                        try
                        {
                            segmentLength = segment.Length;
                        }
                        catch
                        {
                            continue;
                        }

                        if (segmentLength > bestLength)
                        {
                            bestLength = segmentLength;
                            bestSegment = segment;
                        }
                    }
                }

                return bestSegment != null;
            }
            catch
            {
                return false;
            }
        }

        private static void CollectGeometrySolids(GeometryElement geometry, ICollection<Solid> solids)
        {
            if (geometry == null || solids == null)
                return;

            foreach (GeometryObject geometryObject in geometry)
            {
                if (geometryObject is Solid solid && solid.Faces.Size > 0 && solid.Volume > 1e-9)
                {
                    solids.Add(solid);
                }
                else if (geometryObject is GeometryInstance instance)
                {
                    try
                    {
                        CollectGeometrySolids(instance.GetInstanceGeometry(), solids);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static XYZ GetMultiReservationCenterOnCandidate(
            WallScanCandidate host,
            BoundingBoxXYZ unionIntersect,
            IEnumerable<MepSelection> selectedElements)
        {
            if (unionIntersect == null)
                return null;

            XYZ fallbackCenter = (unionIntersect.Min + unionIntersect.Max) * 0.5;
            if (host == null)
                return fallbackCenter;

            if (!TryGetCandidatePlane(host, out XYZ planeOrigin, out XYZ planeNormal))
                return fallbackCenter;

            XYZ axis = host.AxisX;
            axis = new XYZ(axis?.X ?? 0.0, axis?.Y ?? 0.0, 0.0);
            if (axis.IsZeroLength())
                axis = EstimateWallAxisXY(unionIntersect);
            if (axis.IsZeroLength())
                axis = XYZ.BasisX;
            axis = axis.Normalize();

            var crossingPoints = new List<XYZ>();
            foreach (MepSelection selected in selectedElements ?? Enumerable.Empty<MepSelection>())
            {
                if (TryGetLinkedHostPlacementPoint(selected, host, out XYZ crossingPoint) && crossingPoint != null)
                    crossingPoints.Add(crossingPoint);
            }

            var corners = new List<XYZ>
            {
                new XYZ(unionIntersect.Min.X, unionIntersect.Min.Y, unionIntersect.Min.Z),
                new XYZ(unionIntersect.Min.X, unionIntersect.Max.Y, unionIntersect.Min.Z),
                new XYZ(unionIntersect.Max.X, unionIntersect.Min.Y, unionIntersect.Min.Z),
                new XYZ(unionIntersect.Max.X, unionIntersect.Max.Y, unionIntersect.Min.Z)
            };

            var axisValues = corners.Select(p => p.DotProduct(axis)).ToList();
            double axisMid = (axisValues.Min() + axisValues.Max()) * 0.5;
            double zMid = (unionIntersect.Min.Z + unionIntersect.Max.Z) * 0.5;

            if (crossingPoints.Count > 0)
            {
                XYZ anchor = crossingPoints.Aggregate(XYZ.Zero, (sum, p) => sum + p) / crossingPoints.Count;
                return anchor
                       + axis * (axisMid - anchor.DotProduct(axis))
                       + XYZ.BasisZ * (zMid - anchor.Z);
            }

            XYZ center = planeOrigin
                         + axis * (axisMid - planeOrigin.DotProduct(axis))
                         + XYZ.BasisZ * (zMid - planeOrigin.Z);

            return ProjectPointOntoPlane(center, planeOrigin, planeNormal);
        }

        private static bool TryGetCandidatePlane(WallScanCandidate host, out XYZ origin, out XYZ normal)
        {
            if (TryGetWallPlaneInCurrentDocument(host, out origin, out normal))
                return true;

            if (TryGetEstimatedIfcWallPlane(host, out origin, out normal))
                return true;

            origin = null;
            normal = null;
            return false;
        }

        private static bool TryGetFloorPlaneInCurrentDocument(
            WallScanCandidate host,
            out XYZ origin,
            out XYZ normal)
        {
            origin = null;
            normal = null;

            if (host == null || !host.IsFloorLike || host.BoundingBoxInCurrentDocument == null)
                return false;

            BoundingBoxXYZ boundingBox = host.BoundingBoxInCurrentDocument;
            origin = (boundingBox.Min + boundingBox.Max) * 0.5;

            Transform transform = host.TransformToCurrentDocument ?? Transform.Identity;
            try
            {
                normal = transform.OfVector(XYZ.BasisZ);
            }
            catch
            {
                normal = XYZ.BasisZ;
            }

            if (normal == null || normal.GetLength() < 1e-9)
                normal = XYZ.BasisZ;
            else
                normal = normal.Normalize();

            return true;
        }

        private static XYZ ProjectPointOntoFloorPlane(WallScanCandidate host, XYZ point)
        {
            if (point == null)
                return null;

            return TryGetFloorPlaneInCurrentDocument(host, out XYZ origin, out XYZ normal)
                ? ProjectPointOntoPlane(point, origin, normal)
                : point;
        }

        private static XYZ ProjectPointOntoPlane(XYZ point, XYZ planeOrigin, XYZ planeNormal)
        {
            if (point == null || planeOrigin == null || planeNormal == null || planeNormal.GetLength() < 1e-9)
                return point;

            XYZ normal = planeNormal.Normalize();
            double offset = normal.DotProduct(point - planeOrigin);
            return point - normal * offset;
        }

        private static XYZ NormalizeLinkedReferencePoint(XYZ rawPoint, Transform linkTransform, BoundingBoxXYZ linkedBoundingBoxInCurrentDocument)
        {
            if (rawPoint == null)
                return null;

            Transform tr = linkTransform ?? Transform.Identity;
            XYZ transformedPoint = rawPoint;

            try
            {
                if (!tr.IsIdentity)
                    transformedPoint = tr.OfPoint(rawPoint);
            }
            catch
            {
                transformedPoint = rawPoint;
            }

            if (linkedBoundingBoxInCurrentDocument == null)
                return rawPoint;

            double rawDistance = DistanceToBoundingBox(rawPoint, linkedBoundingBoxInCurrentDocument);
            double transformedDistance = DistanceToBoundingBox(transformedPoint, linkedBoundingBoxInCurrentDocument);

            return transformedDistance + 1e-6 < rawDistance
                ? transformedPoint
                : rawPoint;
        }

        private static double DistanceToBoundingBox(XYZ point, BoundingBoxXYZ bb)
        {
            if (point == null || bb == null)
                return double.MaxValue;

            double dx = DistanceToInterval(point.X, bb.Min.X, bb.Max.X);
            double dy = DistanceToInterval(point.Y, bb.Min.Y, bb.Max.Y);
            double dz = DistanceToInterval(point.Z, bb.Min.Z, bb.Max.Z);

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double DistanceToInterval(double value, double min, double max)
        {
            if (value < min) return min - value;
            if (value > max) return value - max;
            return 0.0;
        }

        private static Curve GetElementCurveInCurrentDocument(Element elem, Transform transformToCurrentDocument)
        {
            if (elem?.Location is not LocationCurve lc || lc.Curve == null)
                return null;

            Curve curve = lc.Curve;
            if (transformToCurrentDocument != null && !transformToCurrentDocument.IsIdentity)
            {
                try
                {
                    curve = curve.CreateTransformed(transformToCurrentDocument);
                }
                catch
                {
                    return null;
                }
            }

            return curve;
        }

        private static bool TryGetWallPlaneInCurrentDocument(WallScanCandidate host, out XYZ origin, out XYZ normal)
        {
            origin = null;
            normal = null;

            if (host?.Element is not Wall wall)
                return false;

            Transform tr = host.TransformToCurrentDocument ?? Transform.Identity;

            normal = wall.Orientation;
            if (normal == null || normal.GetLength() < 1e-9)
                return false;

            normal = tr.OfVector(normal);
            if (normal == null || normal.GetLength() < 1e-9)
                return false;

            normal = normal.Normalize();

            if (wall.Location is LocationCurve lc && lc.Curve != null)
            {
                try
                {
                    origin = tr.OfPoint(lc.Curve.Evaluate(0.5, true));
                }
                catch
                {
                    origin = null;
                }
            }

            if (origin == null && host.BoundingBoxInCurrentDocument != null)
                origin = (host.BoundingBoxInCurrentDocument.Min + host.BoundingBoxInCurrentDocument.Max) * 0.5;

            return origin != null;
        }

        private static bool TryGetEstimatedIfcWallPlane(WallScanCandidate host, out XYZ origin, out XYZ normal)
        {
            origin = null;
            normal = null;

            if (host == null || host.Element is Wall || host.IsFloorLike)
                return false;

            BoundingBoxXYZ bb = host.BoundingBoxInCurrentDocument;
            if (bb == null)
                return false;

            origin = (bb.Min + bb.Max) * 0.5;

            double dx = Math.Abs(bb.Max.X - bb.Min.X);
            double dy = Math.Abs(bb.Max.Y - bb.Min.Y);

            if (dx < 1e-6 && dy < 1e-6)
                return false;

            normal = dx <= dy ? XYZ.BasisX : XYZ.BasisY;
            return true;
        }

        private static bool TryIntersectCurveWithPlane(Curve curve, XYZ planeOrigin, XYZ planeNormal, out XYZ point)
        {
            point = null;
            if (curve == null || planeOrigin == null || planeNormal == null || planeNormal.GetLength() < 1e-9)
                return false;

            try
            {
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                XYZ direction = p1 - p0;

                double denom = planeNormal.DotProduct(direction);
                if (Math.Abs(denom) < 1e-9)
                    return false;

                double factor = planeNormal.DotProduct(planeOrigin - p0) / denom;
                if (factor < -0.05 || factor > 1.05)
                    return false;

                factor = Math.Max(0.0, Math.Min(1.0, factor));
                point = p0 + direction * factor;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryProjectPointOnCurve(Curve curve, XYZ referencePoint, out XYZ point)
        {
            point = null;
            if (curve == null || referencePoint == null)
                return false;

            try
            {
                IntersectionResult result = curve.Project(referencePoint);
                if (result?.XYZPoint != null)
                {
                    point = result.XYZPoint;
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                XYZ direction = p1 - p0;
                double lengthSquared = direction.DotProduct(direction);
                if (lengthSquared < 1e-12)
                    return false;

                double factor = (referencePoint - p0).DotProduct(direction) / lengthSquared;
                factor = Math.Max(0.0, Math.Min(1.0, factor));
                point = p0 + direction * factor;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static XYZ GetElementDirectionXY(Element e, Transform trToHost)
        {
            if (e == null) return null;

            XYZ d = null;
            if (e.Location is LocationCurve lc && lc.Curve is Line ln)
                d = ln.Direction;
            else if (e is FamilyInstance fi && fi.HandOrientation != null && !fi.HandOrientation.IsZeroLength())
                d = fi.HandOrientation;

            if (d == null || d.IsZeroLength()) return null;

            if (trToHost != null && !trToHost.IsIdentity)
                d = trToHost.OfVector(d);

            d = new XYZ(d.X, d.Y, 0.0);
            if (d.IsZeroLength()) return null;
            return d.Normalize();
        }

        private static XYZ GetAverageDirectionXY(IEnumerable<Element> elements, Dictionary<ElementId, Transform> transformMap)
        {
            if (elements == null) return null;

            List<XYZ> dirs = new List<XYZ>();
            foreach (var e in elements)
            {
                Transform tr = Transform.Identity;
                if (e != null && transformMap != null && transformMap.TryGetValue(e.Id, out var found) && found != null)
                    tr = found;

                XYZ d = GetElementDirectionXY(e, tr);
                if (d != null && !d.IsZeroLength())
                    dirs.Add(d.Normalize());
            }

            if (dirs.Count == 0) return null;

            XYZ refDir = dirs[0];
            XYZ sum = XYZ.Zero;
            foreach (var d in dirs)
            {
                XYZ dd = d;
                if (dd.DotProduct(refDir) < 0) dd = dd.Negate();
                sum += dd;
            }

            if (sum.IsZeroLength()) return refDir;
            return sum.Normalize();
        }

        private static XYZ GetPreferredFloorAxisXY(BoundingBoxXYZ unionIntersect, List<(Element el, Transform tr)> elems)
        {
            if (unionIntersect != null)
            {
                double dx = Math.Abs(unionIntersect.Max.X - unionIntersect.Min.X);
                double dy = Math.Abs(unionIntersect.Max.Y - unionIntersect.Min.Y);

                if (Math.Abs(dx - dy) > 1e-6)
                    return dx >= dy ? XYZ.BasisX : XYZ.BasisY;
            }

            if (elems != null && elems.Count > 0)
                return GetAverageDirectionXY(elems.Select(x => x.el), elems.ToDictionary(x => x.el.Id, x => x.tr));

            return XYZ.BasisX;
        }

        private static void AlignReservationOrientationIfNeeded(Document doc, FamilyInstance inst, FamilySymbol symbol, Element host, XYZ origin, XYZ axisX)
        {
            if (doc == null || inst == null || symbol == null || host == null || origin == null || axisX == null) return;

            if (IsOpeningFamily(symbol))
                return;

            AlignInstanceXToAxisXY(doc, inst, origin, axisX);
        }

        private static bool IsOpeningFamily(FamilySymbol symbol)
        {
            string famName = symbol?.Family?.Name ?? string.Empty;
            string typeName = symbol?.Name ?? string.Empty;

            bool hasOpeningKeyword = famName.IndexOf("ouverture", StringComparison.OrdinalIgnoreCase) >= 0
                                     || famName.IndexOf("opening", StringComparison.OrdinalIgnoreCase) >= 0
                                     || typeName.IndexOf("ouverture", StringComparison.OrdinalIgnoreCase) >= 0
                                     || typeName.IndexOf("opening", StringComparison.OrdinalIgnoreCase) >= 0;

            return hasOpeningKeyword;
        }

        private static void AlignInstanceXToAxisXY(Document doc, FamilyInstance inst, XYZ origin, XYZ axisX)
        {
            if (doc == null || inst == null || axisX == null || axisX.IsZeroLength()) return;

            XYZ desired = new XYZ(axisX.X, axisX.Y, 0.0);
            if (desired.IsZeroLength()) return;
            desired = desired.Normalize();

            XYZ current = inst.HandOrientation;
            current = new XYZ(current.X, current.Y, 0.0);
            if (current.IsZeroLength()) return;
            current = current.Normalize();

            double dot = Math.Max(-1.0, Math.Min(1.0, current.DotProduct(desired)));
            double angle = Math.Acos(dot);

            double crossZ = current.X * desired.Y - current.Y * desired.X;
            if (crossZ < 0) angle = -angle;

            if (Math.Abs(angle) < 1e-6) return;

            Line axis = Line.CreateUnbound(origin, XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle);
        }

        private static XYZ ProjectPointOntoWallPlane(Wall wall, XYZ point)
        {
            if (wall == null || point == null) return point;

            XYZ normal = wall.Orientation;
            if (normal == null || normal.GetLength() < 1e-9) return point;
            normal = normal.Normalize();

            XYZ origin = null;
            if (wall.Location is LocationCurve lc && lc.Curve != null)
                origin = lc.Curve.Evaluate(0.5, true);

            origin ??= point;
            double offset = normal.DotProduct(point - origin);

            return point - normal * offset;
        }

        private static XYZ ProjectPointOntoWallPlane(WallScanCandidate candidate, XYZ point)
        {
            if (candidate?.Element is not Wall wall || point == null)
                return point;

            if (!candidate.IsLinked)
                return ProjectPointOntoWallPlane(wall, point);

            Transform tr = candidate.TransformToCurrentDocument ?? Transform.Identity;

            XYZ normal = wall.Orientation;
            if (normal == null || normal.GetLength() < 1e-9) return point;
            normal = tr.OfVector(normal).Normalize();

            XYZ origin = null;
            if (wall.Location is LocationCurve lc && lc.Curve != null)
                origin = tr.OfPoint(lc.Curve.Evaluate(0.5, true));

            origin ??= point;
            double offset = normal.DotProduct(point - origin);

            return point - normal * offset;
        }

        private static XYZ EstimateWallAxisXY(BoundingBoxXYZ bb)
        {
            if (bb == null) return XYZ.BasisX;

            double dx = Math.Abs(bb.Max.X - bb.Min.X);
            double dy = Math.Abs(bb.Max.Y - bb.Min.Y);

            if (dx >= dy)
                return XYZ.BasisX;

            return XYZ.BasisY;
        }

        private static double EstimateWallDepth(BoundingBoxXYZ bb)
        {
            if (bb == null) return 0.0;

            double dx = Math.Abs(bb.Max.X - bb.Min.X);
            double dy = Math.Abs(bb.Max.Y - bb.Min.Y);
            double dz = Math.Abs(bb.Max.Z - bb.Min.Z);

            return new[] { dx, dy, dz }
                .Where(v => v > 1e-9)
                .DefaultIfEmpty(0.0)
                .Min();
        }

        private static XYZ GetPlacementPointOnFloor(Floor floor, XYZ fallbackCenter)
        {
            if (floor == null) return fallbackCenter;

            var bb = floor.get_BoundingBox(null);
            if (bb == null) return fallbackCenter;

            double z = (bb.Min.Z + bb.Max.Z) * 0.5;
            return new XYZ(fallbackCenter.X, fallbackCenter.Y, z);
        }

        // =========================
        // Diameter & rounding
        // =========================
        private static double CalculateDiameterForElement(Element elem, ReservationAutoV3Window.ObjectType objType, double oversizeFt)
        {
            if (objType == ReservationAutoV3Window.ObjectType.Canalisation && elem is Pipe p)
            {
                double d = p.LookupParameter("Diamètre")?.AsDouble() ?? 0.0;
                double iso = p.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0;
                return d + 2 * iso + oversizeFt;
            }

            if (objType == ReservationAutoV3Window.ObjectType.Gaine && elem is Duct dct)
            {
                double d = dct.LookupParameter("Diamètre")?.AsDouble() ?? 0.0;
                double iso = dct.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0;
                if (d > 1e-9)
                    return d + 2 * iso + oversizeFt;

                double w = dct.LookupParameter("Largeur")?.AsDouble()
                           ?? dct.LookupParameter("Width")?.AsDouble()
                           ?? 0.0;
                double h = dct.LookupParameter("Hauteur")?.AsDouble()
                           ?? dct.LookupParameter("Height")?.AsDouble()
                           ?? 0.0;

                if (w > 1e-9 || h > 1e-9)
                    return Math.Max(w, h) + 2 * iso + oversizeFt;
            }

            if (objType == ReservationAutoV3Window.ObjectType.Autre && elem is FamilyInstance fi)
            {
                double d = fi.LookupParameter("Diamètre")?.AsDouble()
                           ?? fi.LookupParameter("Diameter")?.AsDouble()
                           ?? 0.0;
                if (d > 1e-9)
                    return d + oversizeFt;

                double w = fi.LookupParameter("Largeur")?.AsDouble()
                           ?? fi.LookupParameter("Width")?.AsDouble()
                           ?? 0.0;
                double h = fi.LookupParameter("Hauteur")?.AsDouble()
                           ?? fi.LookupParameter("Height")?.AsDouble()
                           ?? 0.0;

                if (w > 1e-9 || h > 1e-9)
                    return Math.Max(w, h) + oversizeFt;
            }

            return 0.0;
        }

        private static double CalculateFallbackDiameter(Element host, BoundingBoxXYZ world, double depthFt, double oversizeFt)
        {
            if (world == null) return oversizeFt;

            var dims = new[]
            {
                Math.Abs(world.Max.X - world.Min.X),
                Math.Abs(world.Max.Y - world.Min.Y),
                Math.Abs(world.Max.Z - world.Min.Z)
            };

            double diam = dims.Max();
            if (host is Floor || host is Wall)
            {
                var byDistanceToDepth = dims.OrderBy(d => Math.Abs(d - depthFt)).ToList();
                if (byDistanceToDepth.Count >= 3)
                    diam = Math.Max(byDistanceToDepth[1], byDistanceToDepth[2]);
            }

            return diam + oversizeFt;
        }

        private static double RoundUpToNext50mm(double valueInFeet)
        {
            double mm = valueInFeet * 304.8;
            double mmRounded = Math.Ceiling(mm / 50.0) * 50.0;
            return mmRounded / 304.8;
        }

        // =========================
        // Dynamo
        // =========================
        private static void TryRunDynamo(ExternalCommandData commandData, string dynPath)
        {
            try
            {
                if (!File.Exists(dynPath)) return;

                DynamoRevit dynamoRevit = new DynamoRevit();
                DynamoRevitCommandData dynCmdData = new DynamoRevitCommandData(commandData);
                dynCmdData.JournalData = new Dictionary<string, string>
                {
                    { JournalKeys.ShowUiKey,         false.ToString() },
                    { JournalKeys.AutomationModeKey, false.ToString() },
                    { JournalKeys.DynPathKey,        dynPath },
                    { JournalKeys.DynPathExecuteKey, true.ToString()  },
                    { JournalKeys.ForceManualRunKey, true.ToString()  },
                    { JournalKeys.ModelShutDownKey,  true.ToString()  },
                    { JournalKeys.ModelNodesInfo,    false.ToString() }
                };

                dynamoRevit.ExecuteCommand(dynCmdData);
            }
            catch
            {
            }
        }

        // =========================
        // Link/host pipe selection
        // =========================
        private static string GetElementSearchText(Element elem)
        {
            if (elem == null) return string.Empty;

            var parts = new List<string>
            {
                elem.GetType().Name
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(elem.Name))
                    parts.Add(elem.Name);
            }
            catch { }

            try
            {
                if (!string.IsNullOrWhiteSpace(elem.Category?.Name))
                    parts.Add(elem.Category.Name);
            }
            catch { }

            try
            {
                Element type = elem.Document?.GetElement(elem.GetTypeId());
                if (!string.IsNullOrWhiteSpace(type?.Name))
                    parts.Add(type.Name);
            }
            catch { }

            return string.Join(" ", parts).ToLowerInvariant();
        }

        private static bool HasCategory(Element elem, params BuiltInCategory[] categories)
        {
            if (elem?.Category == null) return false;

            int categoryId = elem.Category.Id.IntegerValue;
            return categories.Any(category => categoryId == (int)category);
        }

        private static bool TextContainsAny(string text, params string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return tokens.Any(token => text.Contains(token));
        }

        private static bool IsPipeLikeElement(Element elem)
        {
            if (elem is Pipe) return true;

            if (HasCategory(elem, BuiltInCategory.OST_PipeCurves))
                return true;

            string text = GetElementSearchText(elem);
            return TextContainsAny(
                text,
                "ifcpipe",
                "ifcflowsegment",
                "flowsegment",
                "pipe segment",
                "pipesegment",
                "pipe",
                "canalisation",
                "tuyau");
        }

        private static bool IsDuctLikeElement(Element elem)
        {
            if (elem is Duct) return true;

            if (HasCategory(elem, BuiltInCategory.OST_DuctCurves))
                return true;

            string text = GetElementSearchText(elem);
            return TextContainsAny(
                text,
                "ifcduct",
                "ifcflowsegment",
                "flowsegment",
                "duct segment",
                "ductsegment",
                "duct",
                "gaine");
        }

        private static bool IsExpectedMepElement(Element elem, ReservationAutoV3Window.ObjectType objectType)
        {
            return objectType switch
            {
                ReservationAutoV3Window.ObjectType.Canalisation => IsPipeLikeElement(elem),
                ReservationAutoV3Window.ObjectType.Gaine => IsDuctLikeElement(elem),
                _ => false
            };
        }

        private static bool IsWallLikeElement(Element elem)
        {
            if (elem is Wall) return true;

            if (HasCategory(elem, BuiltInCategory.OST_Walls))
                return true;

            string text = GetElementSearchText(elem);
            return TextContainsAny(text, "ifcwall", "wall", "mur");
        }

        private static bool IsFloorLikeElement(Element elem)
        {
            if (elem is Floor) return true;

            if (HasCategory(elem, BuiltInCategory.OST_Floors))
                return true;

            string text = GetElementSearchText(elem);
            return TextContainsAny(text, "ifcslab", "slab", "floor", "dalle", "sol");
        }

        private static bool IsIfcLinkInstance(RevitLinkInstance linkInstance)
        {
            try
            {
                var extRef = linkInstance.GetExternalFileReference();
                if (extRef != null)
                {
                    string kind = extRef.ExternalFileReferenceType.ToString();
                    if (string.Equals(kind, "IFC", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }

            var linkDoc = linkInstance.GetLinkDocument();
            string pathOrName = string.Join(
                " ",
                linkDoc?.PathName,
                linkDoc?.Title,
                linkInstance.Name,
                linkInstance.Document?.GetElement(linkInstance.GetTypeId())?.Name).ToLowerInvariant();
            return pathOrName.EndsWith(".ifc") || pathOrName.Contains(".ifc");
        }

        private static bool IsRvtLinkInstance(RevitLinkInstance linkInstance)
        {
            try
            {
                var extRef = linkInstance.GetExternalFileReference();
                if (extRef != null && extRef.ExternalFileReferenceType == ExternalFileReferenceType.RevitLink)
                    return true;
            }
            catch { }

            var linkDoc = linkInstance.GetLinkDocument();
            string pathOrName = string.Join(
                " ",
                linkDoc?.PathName,
                linkDoc?.Title,
                linkInstance.Name,
                linkInstance.Document?.GetElement(linkInstance.GetTypeId())?.Name).ToLowerInvariant();
            bool hasIfc = pathOrName.Contains(".ifc");
            bool hasRvt = pathOrName.EndsWith(".rvt");
            return hasRvt && !hasIfc;
        }

        private static bool MatchesExpectedLinkType(RevitLinkInstance linkInstance, ReservationAutoV3Window.PipeSource source)
        {
            if (linkInstance == null) return false;

            return source switch
            {
                ReservationAutoV3Window.PipeSource.LienIFC => linkInstance.GetLinkDocument() != null,
                ReservationAutoV3Window.PipeSource.LienRVT => IsRvtLinkInstance(linkInstance),
                _ => false
            };
        }

        private class LinkInstanceSelectionFilter : ISelectionFilter
        {
            private readonly ReservationAutoV3Window.PipeSource _source;

            public LinkInstanceSelectionFilter(Document doc, ReservationAutoV3Window.PipeSource source)
            {
                _source = source;
            }

            public bool AllowElement(Element elem)
            {
                var linkInstance = elem as RevitLinkInstance;
                return linkInstance?.GetLinkDocument() != null &&
                       MatchesExpectedLinkType(linkInstance, _source);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class HostMepCurveSelectionFilter : ISelectionFilter
        {
            private readonly ReservationAutoV3Window.ObjectType _objectType;

            public HostMepCurveSelectionFilter(ReservationAutoV3Window.ObjectType objectType)
            {
                _objectType = objectType;
            }

            public bool AllowElement(Element elem)
            {
                return IsExpectedMepElement(elem, _objectType);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LocalHostSelectionFilter : ISelectionFilter
        {
            private readonly ReservationAutoV3Window.HostTarget _hostTarget;

            public LocalHostSelectionFilter(ReservationAutoV3Window.HostTarget hostTarget)
            {
                _hostTarget = hostTarget;
            }

            public bool AllowElement(Element elem)
            {
                return _hostTarget switch
                {
                    ReservationAutoV3Window.HostTarget.Mur => elem is Wall,
                    ReservationAutoV3Window.HostTarget.Sol => elem is Floor,
                    _ => false
                };
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LinkHostSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ReservationAutoV3Window.PipeSource _source;
            private readonly ReservationAutoV3Window.HostTarget _hostTarget;
            private readonly bool _allowAnyLink;

            public LinkHostSelectionFilter(
                Document doc,
                ReservationAutoV3Window.PipeSource source,
                ReservationAutoV3Window.HostTarget hostTarget,
                bool allowAnyLink = false)
            {
                _doc = doc;
                _source = source;
                _hostTarget = hostTarget;
                _allowAnyLink = allowAnyLink;
            }

            public bool AllowElement(Element elem)
            {
                var linkInstance = elem as RevitLinkInstance;
                return linkInstance?.GetLinkDocument() != null &&
                       (_allowAnyLink || MatchesExpectedLinkType(linkInstance, _source));
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null) return false;

                var linkInstance = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                Document linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null || (!_allowAnyLink && !MatchesExpectedLinkType(linkInstance, _source)))
                    return false;

                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                return _hostTarget switch
                {
                    ReservationAutoV3Window.HostTarget.Mur => IsWallLikeElement(linkedElem),
                    ReservationAutoV3Window.HostTarget.Sol => IsFloorLikeElement(linkedElem),
                    _ => false
                };
            }
        }

        private class HybridMepCurveSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ReservationAutoV3Window.PipeSource _source;
            private readonly ReservationAutoV3Window.ObjectType _objectType;
            private readonly bool _linksOnly;

            public HybridMepCurveSelectionFilter(
                Document doc,
                ReservationAutoV3Window.PipeSource source,
                ReservationAutoV3Window.ObjectType objectType,
                bool linksOnly = false)
            {
                _doc = doc;
                _source = source;
                _objectType = objectType;
                _linksOnly = linksOnly;
            }

            public bool AllowElement(Element elem)
            {
                if (!_linksOnly && IsExpectedMepCurve(elem))
                    return true;

                var linkInstance = elem as RevitLinkInstance;
                return linkInstance?.GetLinkDocument() != null &&
                       (_linksOnly || MatchesExpectedLinkType(linkInstance, _source));
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null)
                    return false;

                if (reference.LinkedElementId == ElementId.InvalidElementId)
                {
                    Element localElem = _doc.GetElement(reference.ElementId);
                    return !_linksOnly && IsExpectedMepCurve(localElem);
                }

                var linkInstance = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                Document linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null || (!_linksOnly && !MatchesExpectedLinkType(linkInstance, _source)))
                    return false;

                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                return IsExpectedMepCurve(linkedElem);
            }

            private bool IsExpectedMepCurve(Element elem)
            {
                return IsExpectedMepElement(elem, _objectType);
            }
        }

        private class LinkPipeSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ReservationAutoV3Window.PipeSource _pipeSource;
            private readonly ReservationAutoV3Window.ObjectType _objectType;

            public LinkPipeSelectionFilter(Document doc, ReservationAutoV3Window.PipeSource pipeSource, ReservationAutoV3Window.ObjectType objectType)
            {
                _doc = doc;
                _pipeSource = pipeSource;
                _objectType = objectType;
            }

            private static bool IsIfcLink(RevitLinkInstance linkInstance)
            {
                try
                {
                    var extRef = linkInstance.GetExternalFileReference();
                    if (extRef != null)
                    {
                        string kind = extRef.ExternalFileReferenceType.ToString();
                        if (string.Equals(kind, "IFC", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }

                var linkDoc = linkInstance.GetLinkDocument();
                string pathOrName = (linkDoc?.PathName ?? linkInstance.Name ?? string.Empty).ToLowerInvariant();
                return pathOrName.EndsWith(".ifc") || pathOrName.Contains(".ifc");
            }

            private static bool IsRvtLink(RevitLinkInstance linkInstance)
            {
                try
                {
                    var extRef = linkInstance.GetExternalFileReference();
                    if (extRef != null && extRef.ExternalFileReferenceType == ExternalFileReferenceType.RevitLink)
                        return true;
                }
                catch { }

                var linkDoc = linkInstance.GetLinkDocument();
                string pathOrName = (linkDoc?.PathName ?? linkInstance.Name ?? string.Empty).ToLowerInvariant();
                bool hasIfc = pathOrName.Contains(".ifc");
                bool hasRvt = pathOrName.EndsWith(".rvt");
                return hasRvt && !hasIfc;
            }

            private bool MatchesExpectedLinkType(RevitLinkInstance linkInstance)
            {
                return _pipeSource switch
                {
                    ReservationAutoV3Window.PipeSource.LienIFC => IsIfcLink(linkInstance),
                    ReservationAutoV3Window.PipeSource.LienRVT => IsRvtLink(linkInstance),
                    _ => false
                };
            }

            public bool AllowElement(Element elem)
            {
                var linkInstance = elem as RevitLinkInstance;
                if (linkInstance == null) return false;
                return linkInstance.GetLinkDocument() != null && MatchesExpectedLinkType(linkInstance);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null) return false;

                Element linkElem = _doc.GetElement(reference.ElementId);
                var linkInstance = linkElem as RevitLinkInstance;
                if (linkInstance == null) return false;

                Document linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) return false;

                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                if (!MatchesExpectedLinkType(linkInstance) || linkedElem == null)
                    return false;

                return _objectType switch
                {
                    ReservationAutoV3Window.ObjectType.Canalisation => IsPipeLikeElement(linkedElem),
                    ReservationAutoV3Window.ObjectType.Gaine => IsDuctLikeElement(linkedElem),
                    _ => false
                };
            }
        }

        private static Reference PickSingleMepCurveBySource(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window.PipeSource pipeSource,
            ReservationAutoV3Window.ObjectType objectType,
            bool linksOnly = false)
        {
            var hostFilter = new HostMepCurveSelectionFilter(objectType);
            var hybridFilter = new HybridMepCurveSelectionFilter(doc, pipeSource, objectType, linksOnly);

            string objectName = objectType == ReservationAutoV3Window.ObjectType.Gaine ? "gaine" : "canalisation";

            if (linksOnly)
            {
                return uiDoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    hybridFilter,
                    $"Sélectionne la {objectName} dans un lien");
            }

            return pipeSource switch
            {
                ReservationAutoV3Window.PipeSource.Maquette => uiDoc.Selection.PickObject(
                    ObjectType.Element, hostFilter,
                    $"Sélectionne la {objectName} (maquette)"),

                ReservationAutoV3Window.PipeSource.LienIFC or ReservationAutoV3Window.PipeSource.LienRVT => uiDoc.Selection.PickObject(
                    ObjectType.PointOnElement, hybridFilter,
                    $"Sélectionne la {objectName} dans ta maquette ou dans le lien"),

                _ => uiDoc.Selection.PickObject(ObjectType.Element, hostFilter, $"Sélectionne la {objectName}")
            };
        }

        private static IList<Reference> GetMepCurveReferencesBySource(UIDocument uiDoc, Document doc,
            ReservationAutoV3Window.PipeSource pipeSource,
            ReservationAutoV3Window.ObjectType objectType,
            bool linksOnly = false)
        {
            var hostFilter = new HostMepCurveSelectionFilter(objectType);
            var hybridFilter = new HybridMepCurveSelectionFilter(doc, pipeSource, objectType, linksOnly);
            string objectLabelPlural = objectType == ReservationAutoV3Window.ObjectType.Gaine ? "gaines" : "canalisations";

            if (linksOnly)
            {
                return uiDoc.Selection.PickObjects(
                    ObjectType.PointOnElement,
                    hybridFilter,
                    $"Sélectionne les {objectLabelPlural} dans les liens (CTRL + clic, ESC pour terminer)");
            }

            return pipeSource switch
            {
                ReservationAutoV3Window.PipeSource.Maquette => uiDoc.Selection.PickObjects(
                    ObjectType.Element, hostFilter,
                    $"Sélectionne les {objectLabelPlural} (CTRL + clic, ESC pour terminer)"),

                ReservationAutoV3Window.PipeSource.LienIFC or ReservationAutoV3Window.PipeSource.LienRVT => uiDoc.Selection.PickObjects(
                    ObjectType.PointOnElement, hybridFilter,
                    $"Sélectionne les {objectLabelPlural} dans ta maquette ou dans le lien (CTRL + clic, ESC pour terminer)"),

                _ => null
            };
        }

        private static bool TryResolveReference(UIDocument uiDoc, Reference reference, out Element element, out Transform transformToHost)
        {
            return TryResolveReference(uiDoc, reference, out element, out transformToHost, out _);
        }

        private static bool TryResolveReference(UIDocument uiDoc, Reference reference, out Element element, out Transform transformToHost, out bool isLinked)
        {
            element = null;
            transformToHost = Transform.Identity;
            isLinked = false;

            if (reference == null)
                return false;

            if (reference.LinkedElementId != ElementId.InvalidElementId)
            {
                var linkInstance = uiDoc.Document.GetElement(reference.ElementId) as RevitLinkInstance;
                if (linkInstance == null)
                    return false;

                Document linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                element = linkDoc.GetElement(reference.LinkedElementId);
                transformToHost = linkInstance.GetTotalTransform();
                isLinked = true;
                return element != null;
            }

            element = uiDoc.Document.GetElement(reference);
            transformToHost = Transform.Identity;
            return element != null;
        }
    }
}
