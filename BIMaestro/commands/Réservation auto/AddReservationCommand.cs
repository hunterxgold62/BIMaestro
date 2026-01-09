#region Imports 
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;    // Pour Duct
using Autodesk.Revit.DB.Plumbing;      // Pour Pipe
using Autodesk.Revit.UI;               // Pour TaskDialog, IExternalCommand
using Autodesk.Revit.UI.Selection;     // Pour PickObject, ISelectionFilter
using Dynamo.Applications;             // Pour DynamoRevit, DynamoRevitCommandData
using Dynamo.Applications.Properties;
using Licensing;
#endregion

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class ReservationAutoMultiCommand : BaseTrackedCommand
    {
        // Surdimensionnement pour ~50 mm
        private const double OVERSIZE_FT = 0.164; // 50 mm ≈ 0.164 ft
        protected override string ButtonId => "ReservationAutoMultiCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // 1) Filtrage des familles de réservation circulaire/rectangulaire
                List<FamilySymbol> reservationSymbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_SpecialityEquipment)
                    .Cast<FamilySymbol>()
                    .Where(sym =>
                        sym.Family != null &&
                        (sym.Family.Name.IndexOf("Réservation circulaire murale", StringComparison.OrdinalIgnoreCase) >= 0
                      || sym.Family.Name.IndexOf("Réservation rectangulaire murale", StringComparison.OrdinalIgnoreCase) >= 0
                      || sym.Family.Name.IndexOf("Réservation circulaire sol", StringComparison.OrdinalIgnoreCase) >= 0
                      || sym.Family.Name.IndexOf("Réservation rectangulaire sol", StringComparison.OrdinalIgnoreCase) >= 0
                      || sym.Family.Name.IndexOf("CML_Réservation circulaire murale", StringComparison.OrdinalIgnoreCase) >= 0
                      || sym.Family.Name.IndexOf("CML_Réservation rectangulaire murale", StringComparison.OrdinalIgnoreCase) >= 0)
                    )
                    .OrderBy(sym => sym.Name)
                    .ToList();

                if (!reservationSymbols.Any())
                {
                    TaskDialog.Show("Info",
                        "Aucune famille d'équipement spécialisé pour réservations murales trouvée.");
                    return Result.Cancelled;
                }

                // 2) Fenêtre WPF (choix du type, de la famille, des options)
                var window = new ExtendedReservationWindow(reservationSymbols);
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
                bool reservationsCreated = false;
                bool userCancelled = false;

                if (symbol == null)
                {
                    TaskDialog.Show("Info", "Aucune famille de réservation sélectionnée.");
                    return Result.Cancelled;
                }

                bool isCirculaire = symbol.Name.IndexOf("circul", StringComparison.OrdinalIgnoreCase) >= 0
                                    || symbol.Family.Name.IndexOf("circul", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isRectangulaire = symbol.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase) >= 0
                                    || symbol.Family.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase) >= 0;

                string hostLabel = hostTarget == ExtendedReservationWindow.HostTarget.Sol ? "sol" : "mur";

                // 3) Mode manuel vs automatique
                if (!automatiqueEnabled)
                {
                    // Mode manuel
                    string objetLabel = objType switch
                    {
                        ExtendedReservationWindow.ObjectType.Canalisation => "une canalisation",
                        ExtendedReservationWindow.ObjectType.Gaine => "une gaine",
                        ExtendedReservationWindow.ObjectType.Porte => "une porte",
                        ExtendedReservationWindow.ObjectType.Fenetre => "une fenêtre",
                        _ => "l'objet"
                    };

                    TaskDialog.Show("Mode manuel",
                        $"Vous allez sélectionner {(multiEnabled ? "plusieurs " : "")}{objetLabel}, puis un {hostLabel}.\n\n" +
                        "Répétez autant de fois que nécessaire.\n" +
                        "Cliquez sur Non pour terminer.");

                    while (true)
                    {
                        using (var trans = new Transaction(doc, "Création de réservation manuelle"))
                        {
                            trans.Start();
                            if (!symbol.IsActive) symbol.Activate();

                            // --- MULTI-SÉLECTION pour canalisations ou autres éléments rectangulaires ---
                            if (multiEnabled && isRectangulaire &&
    (objType == ExtendedReservationWindow.ObjectType.Canalisation ||
     objType == ExtendedReservationWindow.ObjectType.Autre))
                            {
                                IList<Reference> elemRefs;
                                try
                                {
                                    if (objType == ExtendedReservationWindow.ObjectType.Canalisation)
                                    {
                                        // Utilise la source choisie dans la fenêtre (Maquette / Lien IFC / Lien RVT)
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

                                if (elementsSel == null || elementsSel.Count == 0)
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                // Sélection du mur ou du sol (toujours dans la maquette)
                                Reference wallRef;
                                try
                                {
                                    wallRef = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        hostTarget == ExtendedReservationWindow.HostTarget.Sol
                                            ? "Sélectionnez le sol (ESC pour annuler)"
                                            : "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    break;
                                }

                                Element hostElem = doc.GetElement(wallRef);
                                if (hostTarget == ExtendedReservationWindow.HostTarget.Mur && hostElem is not Wall ||
                                    hostTarget == ExtendedReservationWindow.HostTarget.Sol && hostElem is not Floor)
                                {
                                    trans.RollBack();
                                    userCancelled = true;
                                    TaskDialog.Show("Erreur",
                                        hostTarget == ExtendedReservationWindow.HostTarget.Sol
                                            ? "Veuillez sélectionner un sol valide."
                                            : "Veuillez sélectionner un mur valide.");
                                    break;
                                }

                                var level = doc.GetElement(hostElem.LevelId) as Level
                                           ?? new FilteredElementCollector(doc)
                                                  .OfClass(typeof(Level))
                                                  .Cast<Level>()
                                                  .FirstOrDefault();

                                if (objType == ExtendedReservationWindow.ObjectType.Canalisation)
                                {
                                    var pipes = elementsSel.OfType<Pipe>().ToList();

                                    if (hostTarget == ExtendedReservationWindow.HostTarget.Sol)
                                        CreateRectangularReservationFromPipesOnFloor(
                                            doc, hostElem as Floor, symbol, pipes, normeEnabled, level, transformMap);
                                    else
                                        CreateRectangularReservationFromPipes(
                                            doc, hostElem as Wall, symbol, pipes, normeEnabled, level, transformMap);
                                }
                                else
                                {
                                    if (hostTarget == ExtendedReservationWindow.HostTarget.Sol)
                                        CreateRectangularReservationFromElementsOnFloor(
                                            doc, hostElem as Floor, symbol, elementsSel, normeEnabled, level,
                                            GetOversizeForType(objType), transformMap);
                                    else
                                        CreateRectangularReservationFromElements(
                                            doc, hostElem as Wall, symbol, elementsSel, normeEnabled, level,
                                            GetOversizeForType(objType), transformMap);
                                }

                                trans.Commit();
                                userCancelled = true;
                            }

                            else
                            {
                                // --- CAS SINGLE (votre code existant, inchangé) ---
                                // 1) Sélection élément
                                Reference elemRef;
                                try
                                {
                                    if (objType == ExtendedReservationWindow.ObjectType.Canalisation)
                                    {
                                        elemRef = PickSinglePipeBySource(uiDoc, doc, pipeSource);
                                    }
                                    else
                                    {
                                        elemRef = uiDoc.Selection.PickObject(
                                            ObjectType.Element,
                                            $"Sélectionnez {objetLabel} (ESC pour annuler)");
                                    }
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
                                    else
                                    {
                                        userCancelled = true;
                                        break;
                                    }
                                }

                                // 2) Sélection du support
                                Reference wallRef2;
                                try
                                {
                                    wallRef2 = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        hostTarget == ExtendedReservationWindow.HostTarget.Sol
                                            ? "Sélectionnez le sol (ESC pour annuler)"
                                            : "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                Element selHost = doc.GetElement(wallRef2);
                                if (hostTarget == ExtendedReservationWindow.HostTarget.Mur && selHost is not Wall ||
                                    hostTarget == ExtendedReservationWindow.HostTarget.Sol && selHost is not Floor)
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = hostTarget == ExtendedReservationWindow.HostTarget.Sol
                                            ? "Ce n'est pas un sol."
                                            : "Ce n'est pas un mur.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;
                                    else
                                    {
                                        userCancelled = true;
                                        break;
                                    }
                                }

                                // 3) Intersection de bounding boxes
                                var bbHost = selHost.get_BoundingBox(null);
                                var bbElem = GetBoundingBoxInHostCoordinates(selElem, transformToHost);
                                if (bbHost == null || bbElem == null)
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = "Impossible d'obtenir la bounding box.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;
                                    else
                                    {
                                        userCancelled = true;
                                        break;
                                    }
                                }

                                var bbIntersect = IntersectBoundingBoxes(bbHost, bbElem);
                                if (bbIntersect == null)
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = "Les éléments ne se croisent pas.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;
                                    else
                                    {
                                        userCancelled = true;
                                        break;
                                    }
                                }

                                var intersectionSize = GetIntersectionFootprint(bbIntersect, selHost, GetOversizeForType(objType));

                                // 4) Centre et niveau
                                XYZ fallbackCenter = (bbIntersect.Min + bbIntersect.Max) * 0.5;
                                XYZ center = selHost is Floor floorHost
                                    ? GetPlacementPointOnFloor(floorHost, selElem, fallbackCenter, transformToHost)
                                    : GetPlacementPointOnWall(selHost as Wall, selElem, fallbackCenter, transformToHost);
                                var usedLevel = doc.GetElement(selHost.LevelId) as Level
                                              ?? new FilteredElementCollector(doc)
                                                     .OfClass(typeof(Level))
                                                     .Cast<Level>()
                                                     .FirstOrDefault();

                                // 5) Création instance réservation
                                FamilyInstance fiRes = doc.Create.NewFamilyInstance(
                                    center,
                                    symbol,
                                    selHost,
                                    usedLevel,
                                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                                // 6) Dimensionnement
                                if (isCirculaire)
                                {
                                    double diam = CalculateDiameterForElement(selElem, objType);
                                    if (diam <= 0.0)
                                    {
                                        double w = intersectionSize.width;
                                        double h = intersectionSize.height;
                                        if (w <= 0 || h <= 0)
                                            GetOrientedXYDimensions(selElem, objType, out w, out h);

                                        diam = Math.Max(w, h);
                                    }
                                    if (normeEnabled)
                                        diam = RoundToNearest50mm(diam);

                                    Parameter pDiamRes = fiRes.LookupParameter("COM_Diamètre");
                                    if (pDiamRes != null && !pDiamRes.IsReadOnly)
                                        pDiamRes.Set(diam);
                                }
                                else
                                {
                                    if (objType == ExtendedReservationWindow.ObjectType.Canalisation
                                     || objType == ExtendedReservationWindow.ObjectType.Gaine)
                                    {
                                        double d = CalculateDiameterForElement(selElem, objType);
                                        if (d <= 0.0)
                                        {
                                            d = Math.Max(intersectionSize.width, intersectionSize.height);
                                        }
                                        if (normeEnabled)
                                            d = RoundToNearest50mm(d);

                                        SetRectangularParameters(fiRes, d, d, hostTarget == ExtendedReservationWindow.HostTarget.Sol);
                                    }
                                    else
                                    {
                                        double w, h;
                                        if (intersectionSize.width > 0 && intersectionSize.height > 0)
                                        {
                                            w = intersectionSize.width;
                                            h = intersectionSize.height;
                                        }
                                        else
                                        {
                                            GetOrientedXYDimensions(selElem, objType, out w, out h);
                                        }
                                        if (normeEnabled)
                                        {
                                            w = RoundToNearest10cm(w);
                                            h = RoundToNearest10cm(h);
                                        }

                                        SetRectangularParameters(fiRes, w, h, hostTarget == ExtendedReservationWindow.HostTarget.Sol);
                                    }
                                }

                                trans.Commit();
                                reservationsCreated = true;
                            }
                        }

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
                    //=== MODE AUTOMATIQUE ===
                    if (objType == ExtendedReservationWindow.ObjectType.Autre)
                    {
                        TaskDialog.Show("Erreur",
                            "L'option 'Autre' n'est pas disponible en mode automatique.");
                        return Result.Cancelled;
                    }

                    if (hostTarget == ExtendedReservationWindow.HostTarget.Sol)
                    {
                        TaskDialog.Show("Info",
                            "Le mode automatique pour les sols n'est pas disponible. Utilisez le mode manuel.");
                        return Result.Cancelled;
                    }

                    List<Element> targetElements = new List<Element>();
                    switch (objType)
                    {
                        case ExtendedReservationWindow.ObjectType.Canalisation:
            targetElements = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .ToList<Element>();
            break;
        case ExtendedReservationWindow.ObjectType.Gaine:
            targetElements = new FilteredElementCollector(doc)
                .OfClass(typeof(Duct))
                .ToList<Element>();
            break;
        case ExtendedReservationWindow.ObjectType.Porte:
            targetElements = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_Doors)
                .ToList<Element>();
            break;
        case ExtendedReservationWindow.ObjectType.Fenetre:
            targetElements = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_Windows)
                .ToList<Element>();
            break;
    }

    int countCreated = 0;
    using (Transaction trans = new Transaction(doc, "Création de réservations par bounding box"))
    {
        trans.Start();
        if (!symbol.IsActive) symbol.Activate();

        if (objType == ExtendedReservationWindow.ObjectType.Canalisation
         || objType == ExtendedReservationWindow.ObjectType.Gaine)
        {
            // Auto canalisation/gaine
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .ToList();

            foreach (Wall wall in walls)
            {
                BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
                if (bbWall == null) continue;
                Level wallLevel = doc.GetElement(wall.LevelId) as Level;

                foreach (Element elem in targetElements)
                {
                    BoundingBoxXYZ bbElem = elem.get_BoundingBox(null);
                    if (bbElem == null) continue;

                    BoundingBoxXYZ bbIntersect = IntersectBoundingBoxes(bbWall, bbElem);
                    if (bbIntersect == null) continue;

                                    XYZ fallbackCenter = (bbIntersect.Min + bbIntersect.Max) * 0.5;
                                    XYZ center = GetPlacementPointOnWall(wall, elem, fallbackCenter);
                                    Level lvl = wallLevel
                               ?? doc.GetElement(elem.LevelId) as Level
                               ?? new FilteredElementCollector(doc)
                                    .OfClass(typeof(Level))
                                    .Cast<Level>()
                                    .FirstOrDefault();

                    FamilyInstance fiRes = doc.Create.NewFamilyInstance(
                        center,
                        symbol,
                        wall,
                        lvl,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    double finalDiam = CalculateDiameterForElement(elem, objType);
                    if (finalDiam <= 0.0)
                    {
                        double w, h;
                        GetOrientedXYDimensions(elem, objType, out w, out h);
                        finalDiam = Math.Max(w, h);
                    }
                    if (normeEnabled)
                        finalDiam = RoundToNearest50mm(finalDiam);

                    if (isCirculaire)
                    {
                        Parameter pDiam = fiRes.LookupParameter("COM_Diamètre");
                        if (pDiam != null && !pDiam.IsReadOnly)
                            pDiam.Set(finalDiam);
                    }
                    else
                                    {
                                        SetRectangularParameters(fiRes, finalDiam, finalDiam, false);
                                    }

                                    countCreated++;
                }
            }
        }
        else
        {
            // Auto porte/fenêtre
            foreach (Element elem in targetElements)
            {
                FamilyInstance fiDoorWin = elem as FamilyInstance;
                if (fiDoorWin == null) continue;

                Wall wallHost = fiDoorWin.Host as Wall;
                if (wallHost == null) continue;

                BoundingBoxXYZ bbElem = elem.get_BoundingBox(null);
                if (bbElem == null) continue;

                double w, h;
                GetOrientedXYDimensions(elem, objType, out w, out h);
                                XYZ fallbackCenter = (bbElem.Min + bbElem.Max) * 0.5;
                                XYZ center = GetPlacementPointOnWall(wallHost, elem, fallbackCenter);

                                Level hostLevel = doc.GetElement(wallHost.LevelId) as Level
                               ?? new FilteredElementCollector(doc)
                                    .OfClass(typeof(Level))
                                    .Cast<Level>()
                                    .FirstOrDefault();

                FamilyInstance fiRes = doc.Create.NewFamilyInstance(
                    center,
                    symbol,
                    wallHost,
                    hostLevel,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                                if (isCirculaire)
                                {
                                    double finalDiam = Math.Max(w, h);
                                    if (normeEnabled) finalDiam = RoundToNearest50mm(finalDiam);

                                    Parameter pDiamRes = fiRes.LookupParameter("COM_Diamètre");
                                    if (pDiamRes != null && !pDiamRes.IsReadOnly)
                                        pDiamRes.Set(finalDiam);
                                }
                                else
                                {
                                    if (normeEnabled)
                                    {
                                        double newW = RoundToNearest10cm(w);
                                        double newH = RoundToNearest10cm(h);
                                        SetRectangularParameters(fiRes, newW, newH, false);
                                    }
                                    else
                                    {
                                        SetRectangularParameters(fiRes, w, h, false);
                                    }
                                }
                                countCreated++;
                            }
                        }

                        trans.Commit();
                        if (countCreated > 0) reservationsCreated = true;
                        TaskDialog.Show("Réservations créées",
            $"Nombre total de réservations placées : {countCreated}");
    }
}

                // 4) Exécution du script Dynamo (si coché)
                if (dynamoAutoEnabled && !userCancelled)
                    if (dynamoAutoEnabled)
                {
                    string journalDynamoPath = @"P:\0-Boîte à outils Revit\1-Dynamo\CML_Arases réservations_par niveau_V24.dyn";
                    if (!File.Exists(journalDynamoPath))
                    {
                        TaskDialog.Show("Erreur", "Le fichier Dynamo n'existe pas : " + journalDynamoPath);
                        return Result.Failed;
                    }
                    try
                    {
                        DynamoRevit dynamoRevit = new DynamoRevit();
                        DynamoRevitCommandData dynCmdData = new DynamoRevitCommandData(commandData);
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

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        #region Méthodes utilitaires
        private void SetRectangularParameters(FamilyInstance fi, double width, double height, bool isFloor)
        {
            if (fi == null) return;

            bool TrySetParameter(string paramName, double value)
            {
                var param = fi.LookupParameter(paramName);
                if (param != null && !param.IsReadOnly)
                {
                    param.Set(value);
                    return true;
                }

                return false;
            }

            TrySetParameter("Largeur", width);
            TrySetParameter("COM_Largeur", width);

            if (isFloor)
            {
                bool longueurSet = TrySetParameter("Longueur", height);
                bool comLongueurSet = TrySetParameter("COM_Longueur", height);

                if (!longueurSet)
                    TrySetParameter("Hauteur", height);
                if (!comLongueurSet)
                    TrySetParameter("COM_Hauteur", height);
            }
            else
            {
                TrySetParameter("Hauteur", height);
                TrySetParameter("COM_Hauteur", height);
            }
        }


        // --- Sélection des canalisations suivant la source choisie dans la fenêtre ---

        /// <summary>
        /// Canalisations dans la maquette (document courant uniquement).
        /// </summary>
        private class HostPipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        /// <summary>
        /// Canalisations dans un lien RVT/IFC.
        /// </summary>
        private class LinkPipeSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;
            private readonly ExtendedReservationWindow.PipeSource _pipeSource;

            public LinkPipeSelectionFilter(Document doc, ExtendedReservationWindow.PipeSource pipeSource)
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
                        // En 2024/2025+, il existe un membre "IFC" dans l'énum,
                        // mais comme on compile contre 2023 on NE l'utilise PAS directement.
                        string kind = extRef.ExternalFileReferenceType.ToString();
                        if (string.Equals(kind, "IFC", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                    // Ignore et on tombe sur l’heuristique de nom/fichier
                }

                var linkDoc = linkInstance.GetLinkDocument();
                string pathOrName = (linkDoc?.PathName ?? linkInstance.Name ?? string.Empty)
                    .ToLowerInvariant();

                // Les liens IFC sont souvent des .rvt temporaires contenant ".ifc" dans le nom
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
                catch
                {
                    // Ignore and fallback to path/name heuristic
                }

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
                    ExtendedReservationWindow.PipeSource.LienIFC => IsIfcLink(linkInstance),
                    ExtendedReservationWindow.PipeSource.LienRVT => IsRvtLink(linkInstance),
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
                if (linkInstance == null)
                    return false;

                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                return MatchesExpectedLinkType(linkInstance);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference == null)
                    return false;

                Element linkElem = _doc.GetElement(reference.ElementId);
                var linkInstance = linkElem as RevitLinkInstance;
                if (linkInstance == null)
                    return false;

                Document linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                Element linkedElem = linkDoc.GetElement(reference.LinkedElementId);
                return MatchesExpectedLinkType(linkInstance) && IsPipeLike(linkedElem);
            }
        }

        /// <summary>
        /// Sélectionne une seule canalisation en respectant la source choisie.
        /// </summary>
        private Reference PickSinglePipeBySource(
            UIDocument uiDoc,
            Document doc,
            ExtendedReservationWindow.PipeSource pipeSource)
        {
            return pipeSource switch
            {
                ExtendedReservationWindow.PipeSource.Maquette => uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new HostPipeSelectionFilter(),
                    "Sélectionnez la canalisation dans la maquette (ESC pour annuler)"),

                ExtendedReservationWindow.PipeSource.LienIFC or ExtendedReservationWindow.PipeSource.LienRVT => uiDoc.Selection.PickObject(
                    ObjectType.LinkedElement,
                    new LinkPipeSelectionFilter(doc, pipeSource),
                    "Sélectionnez la canalisation dans le lien (ESC pour annuler)"),

                _ => uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new HostPipeSelectionFilter(),
                    "Sélectionnez la canalisation (ESC pour annuler)")
            };
        }

        /// <summary>
        /// Demande à l'utilisateur de sélectionner des canalisations en fonction
        /// de la source choisie dans la fenêtre WPF.
        /// </summary>
        private IList<Reference> GetPipeReferencesBySource(
            UIDocument uiDoc,
            Document doc,
            ExtendedReservationWindow.PipeSource pipeSource)
        {
            switch (pipeSource)
            {
                case ExtendedReservationWindow.PipeSource.Maquette:
                    return uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new HostPipeSelectionFilter(),
                        "Sélectionnez les canalisations dans la maquette (CTRL+clic, ESC pour terminer)");

                case ExtendedReservationWindow.PipeSource.LienIFC:
                case ExtendedReservationWindow.PipeSource.LienRVT:
                    return uiDoc.Selection.PickObjects(
                        ObjectType.LinkedElement,
                        new LinkPipeSelectionFilter(doc, pipeSource),
                        "Sélectionnez les canalisations dans le lien (CTRL+clic, ESC pour terminer)");

                default:
                    return null;
            }
        }


        /// <summary>
        /// Crée une réservation rectangulaire pour plusieurs tuyaux,
        /// en ne considérant que la portion à l’intérieur du mur,
        /// en ajoutant 2×l’isolation max + oversize, puis en arrondissant si demandé.
        /// </summary>
        private void CreateRectangularReservationFromPipes(
            Document doc,
            Wall wall,
            FamilySymbol symbol,
            List<Pipe> pipes,
            bool normeEnabled,
            Level level,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (wall == null || symbol == null || pipes == null || !pipes.Any())
                return;

            if (!symbol.IsActive) symbol.Activate();

            // 1) Bounding-box du mur
            BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
            if (bbWall == null) return;

            // 2) Clippez chaque bbox de tuyau à celle du mur
            var clippedBbs = pipes
                .Select(p => GetBoundingBoxInHostCoordinates(p, GetTransform(transformMap, p.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbWall))
                .Where(bb => bb != null)
                .ToList();
            if (!clippedBbs.Any()) return;

            // 3) Fusionnez-les en une bbox englobante
            double minX = clippedBbs.Min(bb => bb.Min.X);
            double minY = clippedBbs.Min(bb => bb.Min.Y);
            double minZ = clippedBbs.Min(bb => bb.Min.Z);
            double maxX = clippedBbs.Max(bb => bb.Max.X);
            double maxY = clippedBbs.Max(bb => bb.Max.Y);
            double maxZ = clippedBbs.Max(bb => bb.Max.Z);

            var bbAll = new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            // 4) Centre & création de l’instance réservation
            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            var projectedCenter = ProjectPointOntoWallPlane(wall, centroid);
            if (projectedCenter != null)
            {
                centroid = projectedCenter;
            }
            else
            {
                var referencePipe = pipes.FirstOrDefault();
                if (referencePipe != null)
                    centroid = GetPlacementPointOnWall(wall, referencePipe, centroid);
            }
            FamilyInstance fi = doc.Create.NewFamilyInstance(
                centroid,
                symbol,
                wall,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            // 5) Isolation max (une seule fois ici)
            double maxIso = pipes
                .Select(p => {
                    var par = p.LookupParameter("Epaisseur d'isolation");
                    return (par != null) ? par.AsDouble() : 0.0;
                })
                .DefaultIfEmpty(0.0)
                .Max();

            // 6) Tangente du mur (pour largeur)
            var locCurve = wall.Location as LocationCurve;
            if (locCurve == null) return;
            var line = locCurve.Curve as Line;
            if (line == null) return;
            XYZ wallDir = line.Direction.Normalize();

            // 7) Projections des coins bas de bbAll sur wallDir
            var corners = new List<XYZ> {
        new XYZ(minX, minY, minZ),
        new XYZ(minX, maxY, minZ),
        new XYZ(maxX, minY, minZ),
        new XYZ(maxX, maxY, minZ)
    };
            var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();
            double minProj = projs.Min();
            double maxProj = projs.Max();

            // 8) CALCUL BRUT + isolant*2 + oversize
            double widthRaw = (maxProj - minProj)   // étendue le long du mur
                               + 2 * maxIso          // isolant de chaque côté
                               + OVERSIZE_FT;        // vos 50mm
            double heightRaw = (maxZ - minZ)         // hauteur
                               + 2 * maxIso          // isolant haut et bas
                               + OVERSIZE_FT;

            // 9) Arrondi si demandé
            if (normeEnabled)
            {
                widthRaw = RoundToNearest50mm(widthRaw);
                heightRaw = RoundToNearest50mm(heightRaw);
            }

            // 10) Affectation aux paramètres famille
            SetRectangularParameters(fi, widthRaw, heightRaw, false);
        }

        /// <summary>
        /// Crée une réservation rectangulaire pour plusieurs éléments génériques.
        /// Les bounding boxes sont découpées par celle du mur puis fusionnées.
        /// </summary>
        private void CreateRectangularReservationFromElements(
            Document doc,
            Wall wall,
            FamilySymbol symbol,
            List<Element> elements,
            bool normeEnabled,
            Level level,
            double oversize,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (wall == null || symbol == null || elements == null || !elements.Any())
                return;

            if (!symbol.IsActive) symbol.Activate();

            BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
            if (bbWall == null) return;

            var clippedBbs = elements
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .Select(bb => IntersectBoundingBoxes(bb, bbWall))
                .Where(bb => bb != null)
                .ToList();
            if (!clippedBbs.Any()) return;

            double minX = clippedBbs.Min(bb => bb.Min.X);
            double minY = clippedBbs.Min(bb => bb.Min.Y);
            double minZ = clippedBbs.Min(bb => bb.Min.Z);
            double maxX = clippedBbs.Max(bb => bb.Max.X);
            double maxY = clippedBbs.Max(bb => bb.Max.Y);
            double maxZ = clippedBbs.Max(bb => bb.Max.Z);

            var bbAll = new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            var projectedCenter = ProjectPointOntoWallPlane(wall, centroid);
            if (projectedCenter != null)
            {
                centroid = projectedCenter;
            }
            else
            {
                var referenceElement = elements.FirstOrDefault();
                if (referenceElement != null)
                    centroid = GetPlacementPointOnWall(wall, referenceElement, centroid);
            }
            FamilyInstance fi = doc.Create.NewFamilyInstance(
                centroid,
                symbol,
                wall,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            var locCurve = wall.Location as LocationCurve;
            if (locCurve == null) return;
            var line = locCurve.Curve as Line;
            if (line == null) return;
            XYZ wallDir = line.Direction.Normalize();

            var corners = new List<XYZ>
            {
                new XYZ(minX, minY, minZ),
                new XYZ(minX, maxY, minZ),
                new XYZ(maxX, minY, minZ),
                new XYZ(maxX, maxY, minZ)
            };
            var projs = corners.Select(c => c.DotProduct(wallDir)).ToList();
            double minProj = projs.Min();
            double maxProj = projs.Max();

            double widthRaw = (maxProj - minProj) + oversize;
            double heightRaw = (maxZ - minZ) + oversize;

            if (normeEnabled)
            {
                widthRaw = RoundToNearest50mm(widthRaw);
                heightRaw = RoundToNearest50mm(heightRaw);
            }

            SetRectangularParameters(fi, widthRaw, heightRaw, false);
        }

        private void CreateRectangularReservationFromPipesOnFloor(
           Document doc,
           Floor floor,
           FamilySymbol symbol,
           List<Pipe> pipes,
           bool normeEnabled,
           Level level,
           Dictionary<ElementId, Transform> transformMap = null)
        {
            if (floor == null || symbol == null || pipes == null || !pipes.Any())
                return;

            if (!symbol.IsActive) symbol.Activate();

            var pipeBbs = pipes
                .Select(p => GetBoundingBoxInHostCoordinates(p, GetTransform(transformMap, p.Id)))
                .Where(bb => bb != null)
                .ToList();
            if (!pipeBbs.Any()) return;

            double minX = pipeBbs.Min(bb => bb.Min.X);
            double minY = pipeBbs.Min(bb => bb.Min.Y);
            double minZ = pipeBbs.Min(bb => bb.Min.Z);
            double maxX = pipeBbs.Max(bb => bb.Max.X);
            double maxY = pipeBbs.Max(bb => bb.Max.Y);
            double maxZ = pipeBbs.Max(bb => bb.Max.Z);

            BoundingBoxXYZ bbAll = new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            centroid = GetPlacementPointOnFloor(floor, pipes.FirstOrDefault(), centroid);

            FamilyInstance fi = doc.Create.NewFamilyInstance(
                centroid,
                symbol,
                floor,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            double maxIso = pipes
                .Select(pipe => pipe.LookupParameter("Epaisseur d'isolation")?.AsDouble() ?? 0.0)
                .DefaultIfEmpty(0.0)
                .Max();

            double oversize = GetOversizeForType(ExtendedReservationWindow.ObjectType.Canalisation);
            double maxRadius = pipes
                .Select(p => CalculateDiameterForElement(p, ExtendedReservationWindow.ObjectType.Canalisation) / 2.0 + maxIso + oversize)
                .DefaultIfEmpty(0.0)
                .Max();

            double width = maxRadius * 2.0;
            double height = maxRadius * 2.0;

            if (normeEnabled)
            {
                width = RoundToNearest50mm(width);
                height = width;
            }

            SetRectangularParameters(fi, width, height, true);
        }

        private void CreateRectangularReservationFromElementsOnFloor(
            Document doc,
            Floor floor,
            FamilySymbol symbol,
            List<Element> elements,
            bool normeEnabled,
            Level level,
            double oversize,
            Dictionary<ElementId, Transform> transformMap = null)
        {
            if (floor == null || symbol == null || elements == null || !elements.Any())
                return;

            if (!symbol.IsActive) symbol.Activate();

            var clippedBbs = elements
                .Select(e => GetBoundingBoxInHostCoordinates(e, GetTransform(transformMap, e.Id)))
                .Where(bb => bb != null)
                .ToList();
            if (!clippedBbs.Any()) return;

            double minX = clippedBbs.Min(bb => bb.Min.X);
            double minY = clippedBbs.Min(bb => bb.Min.Y);
            double minZ = clippedBbs.Min(bb => bb.Min.Z);
            double maxX = clippedBbs.Max(bb => bb.Max.X);
            double maxY = clippedBbs.Max(bb => bb.Max.Y);
            double maxZ = clippedBbs.Max(bb => bb.Max.Z);

            var bbAll = new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            XYZ centroid = (bbAll.Min + bbAll.Max) * 0.5;
            centroid = GetPlacementPointOnFloor(floor, elements.FirstOrDefault(), centroid);

            FamilyInstance fi = doc.Create.NewFamilyInstance(
                centroid,
                symbol,
                floor,
                level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            double widthRaw = (maxX - minX) + oversize;
            double heightRaw = (maxZ - minZ) + oversize;

            if (normeEnabled)
            {
                widthRaw = RoundToNearest50mm(widthRaw);
                heightRaw = RoundToNearest50mm(heightRaw);
            }

            SetRectangularParameters(fi, widthRaw, heightRaw, true);
        }

        private BoundingBoxXYZ IntersectBoundingBoxes(BoundingBoxXYZ bb1, BoundingBoxXYZ bb2)
        {
            var worldBb1 = ToWorldBoundingBox(bb1);
            var worldBb2 = ToWorldBoundingBox(bb2);
            if (worldBb1 == null || worldBb2 == null) return null;

            double minX = Math.Max(worldBb1.Min.X, worldBb2.Min.X);
            double maxX = Math.Min(worldBb1.Max.X, worldBb2.Max.X);
            if (minX > maxX) return null;

            double minY = Math.Max(worldBb1.Min.Y, worldBb2.Min.Y);
            double maxY = Math.Min(worldBb1.Max.Y, worldBb2.Max.Y);
            if (minY > maxY) return null;

            double minZ = Math.Max(worldBb1.Min.Z, worldBb2.Min.Z);
            double maxZ = Math.Min(worldBb1.Max.Z, worldBb2.Max.Z);
            if (minZ > maxZ) return null;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private BoundingBoxXYZ ToWorldBoundingBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return null;

            Transform transform = bb.Transform ?? Transform.Identity;
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
            }
            .Select(p => transform.OfPoint(p))
            .ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private BoundingBoxXYZ GetBoundingBoxInHostCoordinates(Element elem, Transform transformToHost)
        {
            if (elem == null)
                return null;

            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb == null)
                return null;

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
            }
            .Select(p => transformToHost.OfPoint(p))
            .ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double minZ = corners.Min(p => p.Z);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);
            double maxZ = corners.Max(p => p.Z);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }
          private Transform GetTransform(Dictionary<ElementId, Transform> map, ElementId id)
        {
            if (map != null && id != null && map.TryGetValue(id, out var transform))
                return transform;

            return Transform.Identity;
        }

        private (double width, double height) GetIntersectionFootprint(BoundingBoxXYZ bbIntersect, Element host, double oversize)
        {
            if (bbIntersect == null || host == null)
                return (0.0, 0.0);

            var worldBb = ToWorldBoundingBox(bbIntersect);
            if (worldBb == null)
                return (0.0, 0.0);

            var corners = new List<XYZ>
            {
                new XYZ(worldBb.Min.X, worldBb.Min.Y, worldBb.Min.Z),
                new XYZ(worldBb.Min.X, worldBb.Min.Y, worldBb.Max.Z),
                new XYZ(worldBb.Min.X, worldBb.Max.Y, worldBb.Min.Z),
                new XYZ(worldBb.Min.X, worldBb.Max.Y, worldBb.Max.Z),
                new XYZ(worldBb.Max.X, worldBb.Min.Y, worldBb.Min.Z),
                new XYZ(worldBb.Max.X, worldBb.Min.Y, worldBb.Max.Z),
                new XYZ(worldBb.Max.X, worldBb.Max.Y, worldBb.Min.Z),
                new XYZ(worldBb.Max.X, worldBb.Max.Y, worldBb.Max.Z)
            };

            XYZ axisH;
            XYZ axisV;
            if (host is Wall wall)
            {
                axisV = XYZ.BasisZ;
                axisH = wall.Orientation.CrossProduct(XYZ.BasisZ);
                axisH = axisH.GetLength() < 1e-6 ? XYZ.BasisX : axisH.Normalize();
            }
            else if (host is Floor)
            {
                axisH = XYZ.BasisX;
                axisV = XYZ.BasisY;
            }
            else
            {
                axisH = XYZ.BasisX;
                axisV = XYZ.BasisZ;
            }

            double minH = double.MaxValue, maxH = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            foreach (var c in corners)
            {
                double projH = c.DotProduct(axisH);
                double projV = c.DotProduct(axisV);
                minH = Math.Min(minH, projH);
                maxH = Math.Max(maxH, projH);
                minV = Math.Min(minV, projV);
                maxV = Math.Max(maxV, projV);
            }

            double width = (maxH - minH) + oversize;
            double height = (maxV - minV) + oversize;
            return (width, height);
        }
        private XYZ GetPlacementPointOnFloor(Floor floor, Element intersectingElement, XYZ fallbackCenter, Transform transformToHost = null)
        {
            if (floor == null)
                return fallbackCenter;

            BoundingBoxXYZ bbFloor = floor.get_BoundingBox(null);
            if (bbFloor == null)
                return fallbackCenter;

            double targetZ = (bbFloor.Min.Z + bbFloor.Max.Z) * 0.5;
            XYZ source = fallbackCenter;

            if (intersectingElement != null)
            {
                var bbElem = GetBoundingBoxInHostCoordinates(intersectingElement, transformToHost);
                if (bbElem != null)
                    source = (bbElem.Min + bbElem.Max) * 0.5;
            }

            return new XYZ(source.X, source.Y, targetZ);
        }

        private XYZ GetPlacementPointOnWall(Wall wall, Element intersectingElement, XYZ fallbackCenter, Transform transformToHost = null)
        {
            if (wall == null || intersectingElement == null)
                return fallbackCenter;

            var intersection = TryGetIntersectionOnWallPlane(wall, intersectingElement, transformToHost);
            if (intersection != null)
            {
                XYZ point = intersection;
                double z = double.IsNaN(fallbackCenter.Z) ? point.Z : fallbackCenter.Z;
                return new XYZ(point.X, point.Y, z);
            }

            return fallbackCenter;
        }

        private XYZ ProjectPointOntoWallPlane(Wall wall, XYZ point)
        {
            if (wall == null || point == null)
                return point;

            XYZ wallNormal = wall.Orientation;
            if (wallNormal == null || wallNormal.IsZeroLength())
                return point;

            wallNormal = wallNormal.Normalize();

            XYZ planeOrigin = null;

            if (wall.Location is LocationCurve wallLocCurve)
            {
                Curve wallCurve = wallLocCurve.Curve;
                if (wallCurve != null)
                {
                    planeOrigin = wallCurve.Evaluate(0.5, true);
                }
            }

            if (planeOrigin == null)
            {
                BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
                BoundingBoxXYZ worldBb = ToWorldBoundingBox(bbWall);
                if (worldBb != null)
                {
                    planeOrigin = (worldBb.Min + worldBb.Max) * 0.5;
                }
            }

            if (planeOrigin == null)
                planeOrigin = point;

            double offset = wallNormal.DotProduct(point - planeOrigin);

            return point - wallNormal * offset;
        }

        private XYZ? TryGetIntersectionOnWallPlane(Wall wall, Element intersectingElement, Transform transformToHost = null)
        {
            if (!(wall.Location is LocationCurve wallLocCurve))
                return null;

            Curve wallCurve = wallLocCurve.Curve;
            if (wallCurve == null)
                return null;

            XYZ wallNormal = wall.Orientation;
            if (wallNormal == null || wallNormal.IsZeroLength())
                return null;

            wallNormal = wallNormal.Normalize();
            XYZ planeOrigin = wallCurve.Evaluate(0.5, true);

            if (intersectingElement is FamilyInstance fi && fi.Location is LocationPoint lp)
            {
                XYZ point = transformToHost != null ? transformToHost.OfPoint(lp.Point) : lp.Point;
                double distance = wallNormal.DotProduct(point - planeOrigin);
                return point - wallNormal * distance;
            }

            if (intersectingElement.Location is LocationCurve elemLocCurve)
            {
                Curve elemCurve = elemLocCurve.Curve;
                if (elemCurve == null)
                    return null;

                if (elemCurve is Line elemLine)
                {
                    Line lineToUse = elemLine;
                    if (transformToHost != null)
                    {
                        XYZ p0 = transformToHost.OfPoint(elemLine.GetEndPoint(0));
                        XYZ p1 = transformToHost.OfPoint(elemLine.GetEndPoint(1));
                        lineToUse = Line.CreateBound(p0, p1);
                    }
                    return IntersectLineWithPlane(lineToUse, planeOrigin, wallNormal);
                }

                XYZ start = elemCurve.GetEndPoint(0);
                XYZ end = elemCurve.GetEndPoint(1);
                double startVal = wallNormal.DotProduct(start - planeOrigin);
                double endVal = wallNormal.DotProduct(end - planeOrigin);

                if (Math.Abs(startVal) < 1e-6)
                    return start;
                if (Math.Abs(endVal) < 1e-6)
                    return end;
                if (startVal * endVal < 0)
                {
                    double t = startVal / (startVal - endVal);
                    XYZ point = start + t * (end - start);
                    return point;
                }
            }

            return null;
        }

        private XYZ? IntersectLineWithPlane(Line line, XYZ planeOrigin, XYZ planeNormal)
        {
            if (line == null)
                return null;

            XYZ origin = planeOrigin ?? XYZ.Zero;
            XYZ normal = planeNormal ?? XYZ.BasisZ;

            XYZ lineStart = line.GetEndPoint(0);
            XYZ lineDir = line.Direction;

            double denom = normal.DotProduct(lineDir);
            if (Math.Abs(denom) < 1e-9)
                return null;

            double t = normal.DotProduct(origin - lineStart) / denom;
            if (t < -1e-6 || t > line.Length + 1e-6)
                return null;

            XYZ intersection = lineStart + t * lineDir;
            double residual = normal.DotProduct(intersection - origin);
            if (Math.Abs(residual) > 1e-6)
                intersection -= normal * residual;

            return intersection;
        }

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

        private bool CheckSelectedElementType(Element elem, ExtendedReservationWindow.ObjectType objType)
        {
            return objType switch
            {
                ExtendedReservationWindow.ObjectType.Canalisation => elem is Pipe
                    || elem is ImportInstance
                    || elem is DirectShape,
                ExtendedReservationWindow.ObjectType.Gaine => elem is Duct,
                ExtendedReservationWindow.ObjectType.Porte => elem is FamilyInstance fi1
                    && fi1.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_Doors,
                ExtendedReservationWindow.ObjectType.Fenetre => elem is FamilyInstance fi2
                    && fi2.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_Windows,
                ExtendedReservationWindow.ObjectType.Autre => true,
                _ => false
            };
        }

        private double CalculateDiameterForElement(Element elem, ExtendedReservationWindow.ObjectType objType)
        {
            double oversize = GetOversizeForType(objType);
            double finalDiam = 0.0;
            if (objType == ExtendedReservationWindow.ObjectType.Canalisation && elem is Pipe pipe)
            {
                var pDiam = pipe.LookupParameter("Diamètre");
                var diamVal = pDiam != null ? pDiam.AsDouble() : 0.0;
                var pIso = pipe.LookupParameter("Epaisseur d'isolation");
                var isoVal = pIso != null ? pIso.AsDouble() : 0.0;
                finalDiam = diamVal + 2 * isoVal + oversize;
            }
            else if (objType == ExtendedReservationWindow.ObjectType.Gaine && elem is Duct duct)
            {
                var pDiam = duct.LookupParameter("Diamètre");
                var diamVal = pDiam != null ? pDiam.AsDouble() : 0.0;
                var pIso = duct.LookupParameter("Epaisseur d'isolation");
                var isoVal = pIso != null ? pIso.AsDouble() : 0.0;
                finalDiam = diamVal + 2 * isoVal + oversize;
            }
            return finalDiam;
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

        private void GetOrientedXYDimensions(Element elem, ExtendedReservationWindow.ObjectType objType, out double width, out double height)
        {
            var bb = elem.get_BoundingBox(null);
            if (bb == null)
            {
                width = height = 0;
                return;
            }
            double oversize = GetOversizeForType(objType);
            height = (bb.Max.Z - bb.Min.Z) + oversize;

            if (elem is FamilyInstance fi)
            {
                var hand = fi.HandOrientation;
                if (hand != null && !hand.IsZeroLength())
                {
                    hand = hand.Normalize();
                    var corners = new List<XYZ>
                    {
                        new XYZ(bb.Min.X, bb.Min.Y, 0),
                        new XYZ(bb.Min.X, bb.Max.Y, 0),
                        new XYZ(bb.Max.X, bb.Min.Y, 0),
                        new XYZ(bb.Max.X, bb.Max.Y, 0)
                    };
                    double minProj = double.MaxValue, maxProj = double.MinValue;
                    foreach (var c in corners)
                    {
                        double proj = c.DotProduct(hand);
                        minProj = Math.Min(minProj, proj);
                        maxProj = Math.Max(maxProj, proj);
                    }
                    width = (maxProj - minProj) + oversize;
                    return;
                }
                if (fi.Location is LocationPoint lp)
                {
                    var basePt = lp.Point;
                    double rot = lp.Rotation;
                    var corners = new List<XYZ>
                    {
                        new XYZ(bb.Min.X, bb.Min.Y, 0),
                        new XYZ(bb.Min.X, bb.Max.Y, 0),
                        new XYZ(bb.Max.X, bb.Min.Y, 0),
                        new XYZ(bb.Max.X, bb.Max.Y, 0)
                    };
                    var t = Transform.CreateRotation(XYZ.BasisZ, -rot);
                    var ptsLocal = corners.Select(c => t.OfPoint(c - new XYZ(basePt.X, basePt.Y, 0))).ToList();
                    width = (ptsLocal.Max(p => p.X) - ptsLocal.Min(p => p.X)) + oversize;
                    return;
                }
            }
            width = (bb.Max.X - bb.Min.X) + oversize;
        }

        private double GetOversizeForType(ExtendedReservationWindow.ObjectType objType)
        {
            return objType == ExtendedReservationWindow.ObjectType.Canalisation
                || objType == ExtendedReservationWindow.ObjectType.Gaine
                ? OVERSIZE_FT
                : 0.0;
        }
        #endregion
    }
}
