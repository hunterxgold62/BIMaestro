#region Imports
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;    // Duct
using Autodesk.Revit.DB.Plumbing;      // Pipe
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Dynamo.Applications;
using Dynamo.Applications.Properties;
using Licensing;
#endregion

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ReservationAutoMultiVoidCommandV2 : BaseTrackedCommand
    {
        // Surdimensionnement ~50 mm
        private const double OVERSIZE_FT = 0.164; // 50 mm ≈ 0.164 ft
        protected override string ButtonId => "ReservationAutoMultiVoidCommandV2";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIApplication uiApp = data.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // 1) Filtrage familles V2 (void cutters)
                // Attendu :
                // - CML_Réservation rectangulaire verticale (mur)
                // - CML_Réservation rectangulaire horizontale (sol)
                List<FamilySymbol> reservationSymbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(sym => sym?.Family != null)
                    .Where(sym =>
                    {
                        string fam = sym.Family.Name ?? string.Empty;
                        return fam.IndexOf("CML_Réservation rectangulaire verticale", StringComparison.OrdinalIgnoreCase) >= 0
                            || fam.IndexOf("CML_Réservation rectangulaire horizontale", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .OrderBy(sym => sym.Family.Name)
                    .ThenBy(sym => sym.Name)
                    .ToList();

                if (!reservationSymbols.Any())
                {
                    TaskDialog.Show("Info",
                        "Aucune famille V2 trouvée.\n\nAttendu :\n" +
                        "- CML_Réservation rectangulaire verticale (mur)\n" +
                        "- CML_Réservation rectangulaire horizontale (sol)");
                    return Result.Cancelled;
                }

                // 2) Fenêtre WPF (identique logique V1)
                var window = new ExtendedReservationWindowV2(reservationSymbols);
                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                bool normeEnabled = window.NormeEnabled;
                bool dynamoAutoEnabled = window.DynamoAutoEnabled;
                bool automatiqueEnabled = window.AutomatiqueEnabled;
                bool multiEnabled = window.MultiEnabled;
                var hostTarget = window.SelectedHostTarget;
                var objType = window.SelectedObjectType;
                var symbol = window.SelectedReservationSymbol;
                var pipeSource = window.SelectedPipeSource;

                if (symbol == null)
                {
                    TaskDialog.Show("Info", "Aucune famille V2 sélectionnée.");
                    return Result.Cancelled;
                }

                // Sécurité cohérence support/famille
                string famName = symbol.Family?.Name ?? "";
                bool isVertical = famName.IndexOf("verticale", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isHorizontal = famName.IndexOf("horizontale", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hostTarget == ExtendedReservationWindowV2.HostTarget.Mur && !isVertical)
                {
                    TaskDialog.Show("Erreur",
                        "Support = Mur, mais la famille choisie n'est pas 'verticale'.\n" +
                        "Choisis : CML_Réservation rectangulaire verticale.");
                    return Result.Cancelled;
                }

                if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol && !isHorizontal)
                {
                    TaskDialog.Show("Erreur",
                        "Support = Sol, mais la famille choisie n'est pas 'horizontale'.\n" +
                        "Choisis : CML_Réservation rectangulaire horizontale.");
                    return Result.Cancelled;
                }

                bool reservationsCreated = false;
                bool userCancelled = false;

                string hostLabel = hostTarget == ExtendedReservationWindowV2.HostTarget.Sol ? "sol" : "mur";

                // 3) Mode manuel vs automatique (logique identique V1)
                if (!automatiqueEnabled)
                {
                    // ============ MODE MANUEL ============
                    string objetLabel = objType switch
                    {
                        ExtendedReservationWindowV2.ObjectType.Canalisation => "une canalisation",
                        ExtendedReservationWindowV2.ObjectType.Gaine => "une gaine",
                        ExtendedReservationWindowV2.ObjectType.Porte => "une porte",
                        ExtendedReservationWindowV2.ObjectType.Fenetre => "une fenêtre",
                        _ => "l'objet"
                    };

                    TaskDialog.Show("Mode manuel (V2)",
                        $"Vous allez sélectionner {(multiEnabled ? "plusieurs " : "")}{objetLabel}, puis un {hostLabel}.\n\n" +
                        "V2 = objet indépendant + void cut sur le support.\n" +
                        "- Longueur/Largeur/Hauteur : +50mm (+ iso) + arrondi si norme\n" +
                        "- Profondeur : épaisseur mur/sol (sans +50mm, sans arrondi)\n\n" +
                        "Répétez autant de fois que nécessaire.");

                    while (true)
                    {
                        using (Transaction trans = new Transaction(doc, "V2 - Création réservation (void cut)"))
                        {
                            trans.Start();
                            if (!symbol.IsActive) symbol.Activate();

                            // --- MULTI-SÉLECTION (mêmes règles que V1) ---
                            if (multiEnabled &&
                                (objType == ExtendedReservationWindowV2.ObjectType.Canalisation ||
                                 objType == ExtendedReservationWindowV2.ObjectType.Autre))
                            {
                                IList<Reference> elemRefs;
                                try
                                {
                                    if (objType == ExtendedReservationWindowV2.ObjectType.Canalisation)
                                    {
                                        elemRefs = GetPipeReferencesBySource(uiDoc, doc, pipeSource);
                                    }
                                    else
                                    {
                                        elemRefs = uiDoc.Selection.PickObjects(
                                            ObjectType.Element,
                                            "Sélectionnez plusieurs éléments (CTRL+clic)");
                                    }
                                }
                                catch
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                if (elemRefs == null || elemRefs.Count == 0)
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                // Résolution des éléments + transform vers la maquette hôte
                                var resolvedSelections = elemRefs
                                    .Select(r => TryResolveReference(uiDoc, r, out var el, out var tr)
                                        ? (el, tr)
                                        : (null, Transform.Identity))
                                    .Where(t => t.el != null)
                                    .ToList();

                                var elementsSel = resolvedSelections
                                    .Select(t => t.el)
                                    .Where(el => el != null)
                                    .ToList();

                                var transformMap = resolvedSelections
                                    .Where(t => t.el != null && t.tr != null && !t.tr.IsIdentity)
                                    .ToDictionary(t => t.el.Id, t => t.tr);

                                if (elementsSel.Count == 0)
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                // Sélection du support (toujours dans la maquette)
                                Reference hostRef;
                                try
                                {
                                    hostRef = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        hostTarget == ExtendedReservationWindowV2.HostTarget.Sol
                                            ? "Sélectionnez le sol (ESC pour annuler)"
                                            : "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                Element hostElem = doc.GetElement(hostRef);
                                if (hostTarget == ExtendedReservationWindowV2.HostTarget.Mur && hostElem is not Wall ||
                                    hostTarget == ExtendedReservationWindowV2.HostTarget.Sol && hostElem is not Floor)
                                {
                                    trans.RollBack();
                                    TaskDialog.Show("Erreur",
                                        hostTarget == ExtendedReservationWindowV2.HostTarget.Sol
                                            ? "Veuillez sélectionner un sol valide."
                                            : "Veuillez sélectionner un mur valide.");
                                    userCancelled = true;
                                    break;
                                }

                                Level level = doc.GetElement(hostElem.LevelId) as Level
                                           ?? new FilteredElementCollector(doc)
                                                  .OfClass(typeof(Level))
                                                  .Cast<Level>()
                                                  .FirstOrDefault();

                                bool ok = false;

                                if (objType == ExtendedReservationWindowV2.ObjectType.Canalisation)
                                {
                                    // Pipes si possible
                                    var pipes = elementsSel.OfType<Pipe>().ToList();

                                    if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol)
                                        ok = CreateVoidReservationFromPipesOnFloor_V2(doc, hostElem as Floor, symbol, elementsSel, pipes, normeEnabled, level, transformMap);
                                    else
                                        ok = CreateVoidReservationFromPipesOnWall_V2(doc, hostElem as Wall, symbol, elementsSel, pipes, normeEnabled, level, transformMap);
                                }
                                else
                                {
                                    if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol)
                                        ok = CreateVoidReservationFromElementsOnFloor_V2(doc, hostElem as Floor, symbol, elementsSel, normeEnabled, level, GetOversizeForType(objType), transformMap);
                                    else
                                        ok = CreateVoidReservationFromElementsOnWall_V2(doc, hostElem as Wall, symbol, elementsSel, normeEnabled, level, GetOversizeForType(objType), transformMap);
                                }

                                trans.Commit();

                                if (ok) reservationsCreated = true;

                                userCancelled = true; // on sort après un lot (comportement V1)
                            }
                            else
                            {
                                // --- CAS SINGLE (identique logique V1) ---
                                Reference elemRef;
                                try
                                {
                                    if (objType == ExtendedReservationWindowV2.ObjectType.Canalisation)
                                        elemRef = PickSinglePipeBySource(uiDoc, doc, pipeSource);
                                    else
                                        elemRef = uiDoc.Selection.PickObject(ObjectType.Element, $"Sélectionnez {objetLabel} (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                if (!TryResolveReference(uiDoc, elemRef, out var selElem, out var transformToHost))
                                {
                                    trans.RollBack();
                                    break;
                                }

                                if (!CheckSelectedElementType(selElem, objType))
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = "Type d'élément incorrect.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;

                                    userCancelled = true;
                                    break;
                                }

                                // Sélection support
                                Reference hostRef2;
                                try
                                {
                                    hostRef2 = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        hostTarget == ExtendedReservationWindowV2.HostTarget.Sol
                                            ? "Sélectionnez le sol (ESC pour annuler)"
                                            : "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                Element hostElem2 = doc.GetElement(hostRef2);
                                if (hostTarget == ExtendedReservationWindowV2.HostTarget.Mur && hostElem2 is not Wall ||
                                    hostTarget == ExtendedReservationWindowV2.HostTarget.Sol && hostElem2 is not Floor)
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = hostTarget == ExtendedReservationWindowV2.HostTarget.Sol ? "Ce n'est pas un sol." : "Ce n'est pas un mur.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;

                                    userCancelled = true;
                                    break;
                                }

                                Level level2 = doc.GetElement(hostElem2.LevelId) as Level
                                           ?? new FilteredElementCollector(doc)
                                                  .OfClass(typeof(Level))
                                                  .Cast<Level>()
                                                  .FirstOrDefault();

                                bool ok = false;

                                // SINGLE => on passe en "liste 1" pour réutiliser les méthodes multi
                                var one = new List<Element> { selElem };
                                var transformMapSingle = (transformToHost != null && !transformToHost.IsIdentity)
                                    ? new Dictionary<ElementId, Transform> { { selElem.Id, transformToHost } }
                                    : null;

                                if (objType == ExtendedReservationWindowV2.ObjectType.Canalisation)
                                {
                                    var pipes = one.OfType<Pipe>().ToList();

                                    if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol)
                                        ok = CreateVoidReservationFromPipesOnFloor_V2(doc, hostElem2 as Floor, symbol, one, pipes, normeEnabled, level2, transformMapSingle);
                                    else
                                        ok = CreateVoidReservationFromPipesOnWall_V2(doc, hostElem2 as Wall, symbol, one, pipes, normeEnabled, level2, transformMapSingle);
                                }
                                else
                                {
                                    if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol)
                                        ok = CreateVoidReservationFromElementsOnFloor_V2(doc, hostElem2 as Floor, symbol, one, normeEnabled, level2, GetOversizeForType(objType), transformMapSingle);
                                    else
                                        ok = CreateVoidReservationFromElementsOnWall_V2(doc, hostElem2 as Wall, symbol, one, normeEnabled, level2, GetOversizeForType(objType), transformMapSingle);
                                }

                                trans.Commit();
                                if (ok) reservationsCreated = true;
                            }
                        }

                        // Fin boucle : identique V1
                        var tdFin = new TaskDialog("Terminé")
                        {
                            MainInstruction = "Créer une autre réservation ?",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                        };
                        if (tdFin.Show() != TaskDialogResult.Yes)
                            break;
                    }
                }
                else
                {
                    // ============ MODE AUTOMATIQUE ============
                    if (objType == ExtendedReservationWindowV2.ObjectType.Autre)
                    {
                        TaskDialog.Show("Erreur", "L'option 'Autre' n'est pas disponible en mode automatique.");
                        return Result.Cancelled;
                    }

                    if (hostTarget == ExtendedReservationWindowV2.HostTarget.Sol)
                    {
                        TaskDialog.Show("Info",
                            "Le mode automatique pour les sols n'est pas disponible (comme en V1).\nUtilisez le mode manuel.");
                        return Result.Cancelled;
                    }

                    List<Element> targetElements = new List<Element>();
                    switch (objType)
                    {
                        case ExtendedReservationWindowV2.ObjectType.Canalisation:
                            targetElements = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).ToElements().ToList();
                            break;
                        case ExtendedReservationWindowV2.ObjectType.Gaine:
                            targetElements = new FilteredElementCollector(doc).OfClass(typeof(Duct)).ToElements().ToList();
                            break;
                        case ExtendedReservationWindowV2.ObjectType.Porte:
                            targetElements = new FilteredElementCollector(doc)
                                .OfCategory(BuiltInCategory.OST_Doors)
                                .WhereElementIsNotElementType()
                                .ToElements()
                                .ToList();
                            break;
                        case ExtendedReservationWindowV2.ObjectType.Fenetre:
                            targetElements = new FilteredElementCollector(doc)
                                .OfCategory(BuiltInCategory.OST_Windows)
                                .WhereElementIsNotElementType()
                                .ToElements()
                                .ToList();
                            break;
                    }

                    int countCreated = 0;

                    using (Transaction trans = new Transaction(doc, "V2 - Auto réservations (void cut)"))
                    {
                        trans.Start();
                        if (!symbol.IsActive) symbol.Activate();

                        if (objType == ExtendedReservationWindowV2.ObjectType.Canalisation ||
                            objType == ExtendedReservationWindowV2.ObjectType.Gaine)
                        {
                            var walls = new FilteredElementCollector(doc)
                                .OfClass(typeof(Wall))
                                .Cast<Wall>()
                                .ToList();

                            foreach (Wall wall in walls)
                            {
                                BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
                                if (bbWall == null) continue;

                                Level wallLevel = doc.GetElement(wall.LevelId) as Level
                                               ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                                foreach (Element elem in targetElements)
                                {
                                    BoundingBoxXYZ bbElem = elem.get_BoundingBox(null);
                                    if (bbElem == null) continue;

                                    BoundingBoxXYZ bbI = IntersectBoundingBoxes(bbWall, bbElem);
                                    if (bbI == null) continue;

                                    bool ok = CreateVoidReservationFromElementsOnWall_V2(
                                        doc,
                                        wall,
                                        symbol,
                                        new List<Element> { elem },
                                        normeEnabled,
                                        wallLevel,
                                        GetOversizeForType(objType),
                                        null);

                                    if (ok) countCreated++;
                                }
                            }
                        }
                        else
                        {
                            foreach (Element elem in targetElements)
                            {
                                if (elem is not FamilyInstance fi) continue;
                                if (fi.Host is not Wall wallHost) continue;

                                Level hostLevel = doc.GetElement(wallHost.LevelId) as Level
                                               ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();

                                bool ok = CreateVoidReservationFromElementsOnWall_V2(
                                    doc,
                                    wallHost,
                                    symbol,
                                    new List<Element> { elem },
                                    normeEnabled,
                                    hostLevel,
                                    GetOversizeForType(objType),
                                    null);

                                if (ok) countCreated++;
                            }
                        }

                        trans.Commit();
                    }

                    if (countCreated > 0) reservationsCreated = true;

                    TaskDialog.Show("Réservations V2 créées",
                        $"Nombre total de réservations placées : {countCreated}");
                }

                // 4) Dynamo auto (identique V1)
                if (dynamoAutoEnabled && !userCancelled)
                {
                    string journalDynamoPath = @"P:\0-Boîte à outils Revit\1-Dynamo\CML_Arases réservations_par niveau_V24.dyn";
                    if (File.Exists(journalDynamoPath))
                    {
                        try
                        {
                            DynamoRevit dynamoRevit = new DynamoRevit();
                            DynamoRevitCommandData dynCmdData = new DynamoRevitCommandData(data);
                            dynCmdData.JournalData = new Dictionary<string, string>
                            {
                                { JournalKeys.ShowUiKey,         false.ToString() },
                                { JournalKeys.AutomationModeKey, false.ToString() },
                                { JournalKeys.DynPathKey,        journalDynamoPath },
                                { JournalKeys.DynPathExecuteKey, true.ToString()  },
                                { JournalKeys.ForceManualRunKey, true.ToString()  },
                                { JournalKeys.ModelShutDownKey,  true.ToString()  },
                                { JournalKeys.ModelNodesInfo,    false.ToString() }
                            };

                            var dynRes = dynamoRevit.ExecuteCommand(dynCmdData);
                            if (dynRes != Result.Succeeded)
                            {
                                TaskDialog.Show("Erreur", "Échec de l'exécution Dynamo.");
                                return dynRes;
                            }
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Erreur", "Exception Dynamo : " + ex.Message);
                            return Result.Failed;
                        }
                    }
                }

                if (!reservationsCreated)
                {
                    TaskDialog.Show("Info",
                        "Aucune réservation V2 n'a été créée.\n\n" +
                        "Checklist famille V2 :\n" +
                        "- 'Cut with Voids When Loaded' activé\n" +
                        "- le volume de coupe est bien un Void\n" +
                        "- paramètres : Longueur/Largeur/Hauteur/Profondeur existent (ou équivalents)\n" +
                        "- la famille est bien chargée/active.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        #region V2 - Coeur (Void Cut)

        private bool CreateVoidReservationFromPipesOnWall_V2(
            Document doc,
            Wall wall,
            FamilySymbol symbol,
            List<Element> elementsSel,
            List<Pipe> pipes,
            bool normeEnabled,
            Level level,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (wall == null || symbol == null || elementsSel == null || elementsSel.Count == 0)
                return false;

            BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
            if (bbWall == null) return false;

            // Clip bbox des éléments à l’intérieur du mur (en coordonnées hôte)
            var clippedBbs = elementsSel
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbWall))
                .Where(bb => bb != null)
                .ToList();

            if (!clippedBbs.Any()) return false;

            BoundingBoxXYZ bbAll = Union(clippedBbs);

            // Isolation max si pipes réels
            double maxIso = 0.0;
            if (pipes != null && pipes.Count > 0)
            {
                maxIso = pipes
                    .Select(p => p.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0)
                    .DefaultIfEmpty(0.0)
                    .Max();
            }

            // Direction mur (pour Longueur)
            XYZ wallDir = GetWallDirectionXY(wall);
            if (wallDir == null || wallDir.IsZeroLength()) wallDir = XYZ.BasisX;

            // Longueur = projection sur direction mur
            var corners = GetWorldCorners(bbAll);
            double minP = double.MaxValue, maxP = double.MinValue;
            foreach (var c in corners)
            {
                double p = c.DotProduct(wallDir);
                minP = Math.Min(minP, p);
                maxP = Math.Max(maxP, p);
            }

            double longueur = (maxP - minP) + 2 * maxIso + OVERSIZE_FT;
            double hauteur = (bbAll.Max.Z - bbAll.Min.Z) + 2 * maxIso + OVERSIZE_FT;

            // Profondeur = épaisseur mur (sans oversize, sans arrondi)
            double profondeur = wall.Width;

            // Arrondis V1-like : pipes => 50mm, autres => 10cm.
            // Ici on garde 50mm (plus logique pour des réservations).
            if (normeEnabled)
            {
                longueur = RoundToNearest50mm(longueur);
                hauteur = RoundToNearest50mm(hauteur);
            }

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            XYZ center = ProjectPointOntoWallPlane(wall, centroid);

            // Création instance non hébergée (objet indépendant)
            if (level == null)
            {
                level = doc.GetElement(wall.LevelId) as Level
                     ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            }
            if (level == null) return false;

            FamilyInstance cutter = doc.Create.NewFamilyInstance(
                center,
                symbol,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            // Rotation : aligner l'axe X du cutter sur l'axe du mur
            AlignInstanceXToAxisXY(doc, cutter, center, wallDir);

            // Set paramètres (robuste: plusieurs noms possibles)
            SetParamLength(cutter, longueur);
            SetParamHeight(cutter, hauteur);
            SetParamDepth(cutter, profondeur);

            doc.Regenerate();

            if (!InstanceVoidCutUtils.CanBeCutWithVoid(wall))
                return false;

            try
            {
                InstanceVoidCutUtils.AddInstanceVoidCut(doc, wall, cutter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool CreateVoidReservationFromPipesOnFloor_V2(
            Document doc,
            Floor floor,
            FamilySymbol symbol,
            List<Element> elementsSel,
            List<Pipe> pipes,
            bool normeEnabled,
            Level level,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (floor == null || symbol == null || elementsSel == null || elementsSel.Count == 0)
                return false;

            BoundingBoxXYZ bbFloor = floor.get_BoundingBox(null);
            if (bbFloor == null) return false;

            // On clip aussi au sol (utile si éléments hors sol)
            var clippedBbs = elementsSel
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbFloor))
                .Where(bb => bb != null)
                .ToList();

            if (!clippedBbs.Any()) return false;

            BoundingBoxXYZ bbAll = Union(clippedBbs);

            double maxIso = 0.0;
            if (pipes != null && pipes.Count > 0)
            {
                maxIso = pipes
                    .Select(p => p.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0)
                    .DefaultIfEmpty(0.0)
                    .Max();
            }

            // Axe principal en XY (moyenne des directions de pipes si possible)
            XYZ axisX = GetAverageDirectionXY(elementsSel) ?? XYZ.BasisX;
            axisX = axisX.Normalize();
            XYZ axisY = XYZ.BasisZ.CrossProduct(axisX);
            axisY = new XYZ(axisY.X, axisY.Y, 0.0);
            if (axisY.IsZeroLength()) axisY = XYZ.BasisY;
            axisY = axisY.Normalize();

            var corners = GetWorldCorners(bbAll);
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            foreach (var c in corners)
            {
                double px = c.DotProduct(axisX);
                double py = c.DotProduct(axisY);
                minX = Math.Min(minX, px);
                maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py);
                maxY = Math.Max(maxY, py);
            }

            double longueur = (maxX - minX) + 2 * maxIso + OVERSIZE_FT;
            double largeur = (maxY - minY) + 2 * maxIso + OVERSIZE_FT;

            // Profondeur = épaisseur sol (sans oversize, sans arrondi)
            double profondeur = GetFloorThickness(doc, floor);
            if (profondeur <= 1e-9)
                profondeur = (bbFloor.Max.Z - bbFloor.Min.Z);

            if (normeEnabled)
            {
                longueur = RoundToNearest50mm(longueur);
                largeur = RoundToNearest50mm(largeur);
            }

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            XYZ center = GetPlacementPointOnFloorMidZ(floor, centroid);

            if (level == null)
            {
                level = doc.GetElement(floor.LevelId) as Level
                     ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            }
            if (level == null) return false;

            FamilyInstance cutter = doc.Create.NewFamilyInstance(
                center,
                symbol,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignInstanceXToAxisXY(doc, cutter, center, axisX);

            SetParamLength(cutter, longueur);
            SetParamWidth(cutter, largeur);
            SetParamDepth(cutter, profondeur);

            doc.Regenerate();

            if (!InstanceVoidCutUtils.CanBeCutWithVoid(floor))
                return false;

            try
            {
                InstanceVoidCutUtils.AddInstanceVoidCut(doc, floor, cutter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool CreateVoidReservationFromElementsOnWall_V2(
            Document doc,
            Wall wall,
            FamilySymbol symbol,
            List<Element> elementsSel,
            bool normeEnabled,
            Level level,
            double oversize,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (wall == null || symbol == null || elementsSel == null || elementsSel.Count == 0)
                return false;

            BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
            if (bbWall == null) return false;

            var clippedBbs = elementsSel
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbWall))
                .Where(bb => bb != null)
                .ToList();

            if (!clippedBbs.Any()) return false;

            BoundingBoxXYZ bbAll = Union(clippedBbs);

            XYZ wallDir = GetWallDirectionXY(wall);
            if (wallDir == null || wallDir.IsZeroLength()) wallDir = XYZ.BasisX;

            var corners = GetWorldCorners(bbAll);
            double minP = double.MaxValue, maxP = double.MinValue;
            foreach (var c in corners)
            {
                double p = c.DotProduct(wallDir);
                minP = Math.Min(minP, p);
                maxP = Math.Max(maxP, p);
            }

            double longueur = (maxP - minP) + oversize;
            double hauteur = (bbAll.Max.Z - bbAll.Min.Z) + oversize;

            // Norme V1 : autres => 10cm (comme ton code V1)
            if (normeEnabled)
            {
                longueur = RoundToNearest10cm(longueur);
                hauteur = RoundToNearest10cm(hauteur);
            }

            double profondeur = wall.Width; // sans oversize

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            XYZ center = ProjectPointOntoWallPlane(wall, centroid);

            if (level == null)
            {
                level = doc.GetElement(wall.LevelId) as Level
                     ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            }
            if (level == null) return false;

            FamilyInstance cutter = doc.Create.NewFamilyInstance(
                center,
                symbol,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignInstanceXToAxisXY(doc, cutter, center, wallDir);

            SetParamLength(cutter, longueur);
            SetParamHeight(cutter, hauteur);
            SetParamDepth(cutter, profondeur);

            doc.Regenerate();

            if (!InstanceVoidCutUtils.CanBeCutWithVoid(wall))
                return false;

            try
            {
                InstanceVoidCutUtils.AddInstanceVoidCut(doc, wall, cutter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool CreateVoidReservationFromElementsOnFloor_V2(
            Document doc,
            Floor floor,
            FamilySymbol symbol,
            List<Element> elementsSel,
            bool normeEnabled,
            Level level,
            double oversize,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (floor == null || symbol == null || elementsSel == null || elementsSel.Count == 0)
                return false;

            BoundingBoxXYZ bbFloor = floor.get_BoundingBox(null);
            if (bbFloor == null) return false;

            var clippedBbs = elementsSel
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbFloor))
                .Where(bb => bb != null)
                .ToList();

            if (!clippedBbs.Any()) return false;

            BoundingBoxXYZ bbAll = Union(clippedBbs);

            // Axe X principal basé sur direction des éléments si possible
            XYZ axisX = GetAverageDirectionXY(elementsSel) ?? XYZ.BasisX;
            axisX = axisX.Normalize();
            XYZ axisY = XYZ.BasisZ.CrossProduct(axisX);
            axisY = new XYZ(axisY.X, axisY.Y, 0.0);
            if (axisY.IsZeroLength()) axisY = XYZ.BasisY;
            axisY = axisY.Normalize();

            var corners = GetWorldCorners(bbAll);
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            foreach (var c in corners)
            {
                double px = c.DotProduct(axisX);
                double py = c.DotProduct(axisY);
                minX = Math.Min(minX, px);
                maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py);
                maxY = Math.Max(maxY, py);
            }

            double longueur = (maxX - minX) + oversize;
            double largeur = (maxY - minY) + oversize;

            if (normeEnabled)
            {
                longueur = RoundToNearest10cm(longueur);
                largeur = RoundToNearest10cm(largeur);
            }

            double profondeur = GetFloorThickness(doc, floor);
            if (profondeur <= 1e-9)
                profondeur = (bbFloor.Max.Z - bbFloor.Min.Z);

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            XYZ center = GetPlacementPointOnFloorMidZ(floor, centroid);

            if (level == null)
            {
                level = doc.GetElement(floor.LevelId) as Level
                     ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            }
            if (level == null) return false;

            FamilyInstance cutter = doc.Create.NewFamilyInstance(
                center,
                symbol,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            AlignInstanceXToAxisXY(doc, cutter, center, axisX);

            SetParamLength(cutter, longueur);
            SetParamWidth(cutter, largeur);
            SetParamDepth(cutter, profondeur);

            doc.Regenerate();

            if (!InstanceVoidCutUtils.CanBeCutWithVoid(floor))
                return false;

            try
            {
                InstanceVoidCutUtils.AddInstanceVoidCut(doc, floor, cutter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Param setters (robustes)

        private void SetParamLength(FamilyInstance fi, double v)
        {
            TrySetDouble(fi, new[] { "Longueur", "COM_Longueur", "Largeur", "COM_Largeur" }, v);
        }

        private void SetParamWidth(FamilyInstance fi, double v)
        {
            TrySetDouble(fi, new[] { "Largeur", "COM_Largeur" }, v);
        }

        private void SetParamHeight(FamilyInstance fi, double v)
        {
            TrySetDouble(fi, new[] { "Hauteur", "COM_Hauteur" }, v);
        }

        private void SetParamDepth(FamilyInstance fi, double v)
        {
            TrySetDouble(fi, new[] { "Profondeur", "COM_Profondeur" }, v);
        }

        private bool TrySetDouble(Element e, IEnumerable<string> names, double value)
        {
            if (e == null || names == null) return false;

            foreach (var n in names)
            {
                Parameter p = e.LookupParameter(n);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                {
                    p.Set(value);
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region Sélection canalisations (identique V1)

        private class HostPipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LinkPipeSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ExtendedReservationWindowV2.PipeSource _pipeSource;

            public LinkPipeSelectionFilter(Document doc, ExtendedReservationWindowV2.PipeSource pipeSource)
            {
                _doc = doc;
                _pipeSource = pipeSource;
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
                    ExtendedReservationWindowV2.PipeSource.LienIFC => IsIfcLink(linkInstance),
                    ExtendedReservationWindowV2.PipeSource.LienRVT => IsRvtLink(linkInstance),
                    _ => false
                };
            }

            private static bool IsPipeLike(Element linkedElem)
            {
                return linkedElem is Pipe
                    || linkedElem is ImportInstance
                    || linkedElem is DirectShape;
            }

            public bool AllowElement(Element elem)
            {
                var linkInstance = elem as RevitLinkInstance;
                if (linkInstance == null) return false;
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) return false;
                return MatchesExpectedLinkType(linkInstance);
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
                return MatchesExpectedLinkType(linkInstance) && IsPipeLike(linkedElem);
            }
        }

        private Reference PickSinglePipeBySource(
            UIDocument uiDoc,
            Document doc,
            ExtendedReservationWindowV2.PipeSource pipeSource)
        {
            return pipeSource switch
            {
                ExtendedReservationWindowV2.PipeSource.Maquette => uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new HostPipeSelectionFilter(),
                    "Sélectionnez la canalisation dans la maquette (ESC pour annuler)"),

                ExtendedReservationWindowV2.PipeSource.LienIFC or ExtendedReservationWindowV2.PipeSource.LienRVT => uiDoc.Selection.PickObject(
                    ObjectType.LinkedElement,
                    new LinkPipeSelectionFilter(doc, pipeSource),
                    "Sélectionnez la canalisation dans le lien (ESC pour annuler)"),

                _ => uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new HostPipeSelectionFilter(),
                    "Sélectionnez la canalisation (ESC pour annuler)")
            };
        }

        private IList<Reference> GetPipeReferencesBySource(
            UIDocument uiDoc,
            Document doc,
            ExtendedReservationWindowV2.PipeSource pipeSource)
        {
            return pipeSource switch
            {
                ExtendedReservationWindowV2.PipeSource.Maquette => uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    new HostPipeSelectionFilter(),
                    "Sélectionnez les canalisations dans la maquette (CTRL+clic, ESC pour terminer)"),

                ExtendedReservationWindowV2.PipeSource.LienIFC or ExtendedReservationWindowV2.PipeSource.LienRVT => uiDoc.Selection.PickObjects(
                    ObjectType.LinkedElement,
                    new LinkPipeSelectionFilter(doc, pipeSource),
                    "Sélectionnez les canalisations dans le lien (CTRL+clic, ESC pour terminer)"),

                _ => null
            };
        }

        #endregion

        #region Utils géométrie / transforms / checks

        private bool TryResolveReference(UIDocument uiDoc, Reference reference, out Element element, out Transform transformToHost)
        {
            element = null;
            transformToHost = Transform.Identity;

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
                return element != null;
            }

            element = uiDoc.Document.GetElement(reference);
            transformToHost = Transform.Identity;
            return element != null;
        }

        private bool CheckSelectedElementType(Element elem, ExtendedReservationWindowV2.ObjectType objType)
        {
            return objType switch
            {
                ExtendedReservationWindowV2.ObjectType.Canalisation => elem is Pipe || elem is ImportInstance || elem is DirectShape,
                ExtendedReservationWindowV2.ObjectType.Gaine => elem is Duct,
                ExtendedReservationWindowV2.ObjectType.Porte => elem is FamilyInstance fi1
                    && fi1.Category != null && fi1.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Doors,
                ExtendedReservationWindowV2.ObjectType.Fenetre => elem is FamilyInstance fi2
                    && fi2.Category != null && fi2.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Windows,
                ExtendedReservationWindowV2.ObjectType.Autre => true,
                _ => false
            };
        }

        private double GetOversizeForType(ExtendedReservationWindowV2.ObjectType objType)
        {
            return (objType == ExtendedReservationWindowV2.ObjectType.Canalisation || objType == ExtendedReservationWindowV2.ObjectType.Gaine)
                ? OVERSIZE_FT
                : 0.0;
        }

        private Transform GetTransform(Dictionary<ElementId, Transform> map, ElementId id)
        {
            if (map != null && id != null && map.TryGetValue(id, out var transform))
                return transform;

            return Transform.Identity;
        }

        private BoundingBoxXYZ GetBoundingBoxInHostCoordinates(Element elem, Transform transformToHost)
        {
            if (elem == null) return null;

            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb == null) return null;

            if (transformToHost == null || transformToHost.IsIdentity)
                return bb;

            var corners = GetWorldCorners(bb).Select(p => transformToHost.OfPoint(p)).ToList();
            return FromPoints(corners);
        }

        private BoundingBoxXYZ IntersectBoundingBoxes(BoundingBoxXYZ bb1, BoundingBoxXYZ bb2)
        {
            if (bb1 == null || bb2 == null) return null;

            var w1 = ToWorldBoundingBox(bb1);
            var w2 = ToWorldBoundingBox(bb2);
            if (w1 == null || w2 == null) return null;

            double minX = Math.Max(w1.Min.X, w2.Min.X);
            double minY = Math.Max(w1.Min.Y, w2.Min.Y);
            double minZ = Math.Max(w1.Min.Z, w2.Min.Z);
            double maxX = Math.Min(w1.Max.X, w2.Max.X);
            double maxY = Math.Min(w1.Max.Y, w2.Max.Y);
            double maxZ = Math.Min(w1.Max.Z, w2.Max.Z);

            if (minX > maxX || minY > maxY || minZ > maxZ)
                return null;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private BoundingBoxXYZ ToWorldBoundingBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;

            Transform t = bb.Transform ?? Transform.Identity;
            var corners = GetRawCorners(bb).Select(p => t.OfPoint(p)).ToList();
            return FromPoints(corners);
        }

        private List<XYZ> GetRawCorners(BoundingBoxXYZ bb)
        {
            return new List<XYZ>
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z),
            };
        }

        private List<XYZ> GetWorldCorners(BoundingBoxXYZ bb)
        {
            if (bb == null) return new List<XYZ>();
            Transform t = bb.Transform ?? Transform.Identity;
            return GetRawCorners(bb).Select(p => t.OfPoint(p)).ToList();
        }

        private BoundingBoxXYZ FromPoints(List<XYZ> pts)
        {
            if (pts == null || pts.Count == 0) return null;

            double minX = pts.Min(p => p.X);
            double minY = pts.Min(p => p.Y);
            double minZ = pts.Min(p => p.Z);
            double maxX = pts.Max(p => p.X);
            double maxY = pts.Max(p => p.Y);
            double maxZ = pts.Max(p => p.Z);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private BoundingBoxXYZ Union(List<BoundingBoxXYZ> bbs)
        {
            if (bbs == null || bbs.Count == 0) return null;

            double minX = bbs.Min(b => b.Min.X);
            double minY = bbs.Min(b => b.Min.Y);
            double minZ = bbs.Min(b => b.Min.Z);
            double maxX = bbs.Max(b => b.Max.X);
            double maxY = bbs.Max(b => b.Max.Y);
            double maxZ = bbs.Max(b => b.Max.Z);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private XYZ ProjectPointOntoWallPlane(Wall wall, XYZ point)
        {
            if (wall == null || point == null) return point;

            XYZ wallNormal = wall.Orientation;
            if (wallNormal == null || wallNormal.IsZeroLength()) return point;
            wallNormal = wallNormal.Normalize();

            XYZ planeOrigin = null;

            if (wall.Location is LocationCurve lc && lc.Curve != null)
                planeOrigin = lc.Curve.Evaluate(0.5, true);

            if (planeOrigin == null)
            {
                var bb = wall.get_BoundingBox(null);
                var wbb = ToWorldBoundingBox(bb);
                if (wbb != null) planeOrigin = (wbb.Min + wbb.Max) * 0.5;
            }

            if (planeOrigin == null) planeOrigin = point;

            double offset = wallNormal.DotProduct(point - planeOrigin);
            return point - wallNormal * offset;
        }

        private XYZ GetPlacementPointOnFloorMidZ(Floor floor, XYZ fallbackCenter)
        {
            if (floor == null) return fallbackCenter;

            BoundingBoxXYZ bb = floor.get_BoundingBox(null);
            if (bb == null) return fallbackCenter;

            double zMid = (bb.Min.Z + bb.Max.Z) * 0.5;
            return new XYZ(fallbackCenter.X, fallbackCenter.Y, zMid);
        }

        private XYZ GetWallDirectionXY(Wall wall)
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

        private XYZ GetAverageDirectionXY(List<Element> elementsSel)
        {
            if (elementsSel == null || elementsSel.Count == 0) return null;

            List<XYZ> dirs = new List<XYZ>();

            foreach (var e in elementsSel)
            {
                XYZ d = null;

                if (e.Location is LocationCurve lc && lc.Curve is Line ln)
                {
                    d = ln.Direction;
                }
                else if (e is FamilyInstance fi && fi.HandOrientation != null && !fi.HandOrientation.IsZeroLength())
                {
                    d = fi.HandOrientation;
                }

                if (d != null)
                {
                    d = new XYZ(d.X, d.Y, 0.0);
                    if (!d.IsZeroLength())
                        dirs.Add(d.Normalize());
                }
            }

            if (dirs.Count == 0) return null;

            // Standardiser le signe (évite que +d et -d s'annulent)
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

        private void AlignInstanceXToAxisXY(Document doc, FamilyInstance inst, XYZ origin, XYZ axisX)
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

        private double GetFloorThickness(Document doc, Floor floor)
        {
            try
            {
                ElementType t = doc.GetElement(floor.GetTypeId()) as ElementType;
                if (t != null)
                {
                    Parameter p = t.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
                    if (p != null && p.StorageType == StorageType.Double)
                    {
                        double v = p.AsDouble();
                        if (v > 1e-9) return v;
                    }
                }
            }
            catch { }

            return 0.0;
        }

        private double RoundToNearest50mm(double valueInFeet)
        {
            double mm = valueInFeet * 304.8;
            double mmRounded = Math.Ceiling(mm / 50.0) * 50.0;
            return mmRounded / 304.8;
        }

        private double RoundToNearest10cm(double valueInFeet)
        {
            double m = valueInFeet * 0.3048;
            double mRounded = Math.Ceiling(m / 0.1) * 0.1;
            return mRounded / 0.3048;
        }

        #endregion
    }
}
