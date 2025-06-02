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
#endregion

namespace RevitAddinReservationExample
{
    [Transaction(TransactionMode.Manual)]
    public class ReservationAutoMultiCommand : IExternalCommand
    {
        // Surdimensionnement pour ~50 mm
        private const double OVERSIZE_FT = 0.164; // 50 mm ≈ 0.164 ft

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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
                        (sym.Family.Name.Equals("CML_Réservation circulaire murale", StringComparison.OrdinalIgnoreCase)
                      || sym.Family.Name.Equals("CML_Réservation rectangulaire murale", StringComparison.OrdinalIgnoreCase))
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
                var objType = window.SelectedObjectType;
                var symbol = window.SelectedReservationSymbol;

                if (symbol == null)
                {
                    TaskDialog.Show("Info", "Aucune famille de réservation sélectionnée.");
                    return Result.Cancelled;
                }

                bool isCirculaire = symbol.Name.IndexOf("circul", StringComparison.OrdinalIgnoreCase) >= 0
                                    || symbol.Family.Name.IndexOf("circul", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isRectangulaire = symbol.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase) >= 0
                                    || symbol.Family.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase) >= 0;

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
                        $"Vous allez sélectionner {(multiEnabled ? "plusieurs " : "")}{objetLabel}, puis un mur.\n\n" +
                        "Répétez autant de fois que nécessaire.\n" +
                        "Cliquez sur Non pour terminer.");

                    while (true)
                    {
                        using (var trans = new Transaction(doc, "Création de réservation manuelle"))
                        {
                            trans.Start();
                            if (!symbol.IsActive) symbol.Activate();

                            // --- MULTI-SÉLECTION pour canalisations rectangulaires ---
                            if (multiEnabled
                                && objType == ExtendedReservationWindow.ObjectType.Canalisation
                                && isRectangulaire)
                            {
                                // Sélection multiple de tuyaux
                                IList<Reference> pipeRefs;
                                try
                                {
                                    pipeRefs = uiDoc.Selection.PickObjects(
                                        ObjectType.Element,
                                        new PipeSelectionFilter(),
                                        "Sélectionnez plusieurs canalisations (CTRL+clic)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                var pipes = pipeRefs
                                    .Select(r => doc.GetElement(r))
                                    .OfType<Pipe>()
                                    .ToList();

                                // Sélection du mur
                                Reference wallRef;
                                try
                                {
                                    wallRef = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                var wall = doc.GetElement(wallRef) as Wall;
                                if (wall == null)
                                {
                                    trans.RollBack();
                                    TaskDialog.Show("Erreur", "Veuillez sélectionner un mur valide.");
                                    break;
                                }

                                var level = doc.GetElement(wall.LevelId) as Level
                                           ?? new FilteredElementCollector(doc)
                                                  .OfClass(typeof(Level))
                                                  .Cast<Level>()
                                                  .FirstOrDefault();

                                // Création de la résa multi-tuyaux selon la méthode Dynamo
                                CreateRectangularReservationFromPipes(
                                    doc, wall, symbol, pipes, normeEnabled, level);

                                trans.Commit();
                            }
                            else
                            {
                                // --- CAS SINGLE (votre code existant, inchangé) ---
                                // 1) Sélection élément
                                Reference elemRef;
                                try
                                {
                                    elemRef = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        $"Sélectionnez {objetLabel} (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                Element selElem = doc.GetElement(elemRef);
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
                                        break;
                                }

                                // 2) Sélection du mur
                                Reference wallRef2;
                                try
                                {
                                    wallRef2 = uiDoc.Selection.PickObject(
                                        ObjectType.Element,
                                        "Sélectionnez le mur (ESC pour annuler)");
                                }
                                catch
                                {
                                    trans.RollBack();
                                    break;
                                }

                                Wall selWall = doc.GetElement(wallRef2) as Wall;
                                if (selWall == null)
                                {
                                    trans.RollBack();
                                    var tdErr = new TaskDialog("Erreur")
                                    {
                                        MainInstruction = "Ce n'est pas un mur.",
                                        MainContent = "Réessayer ?",
                                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                                    };
                                    if (tdErr.Show() == TaskDialogResult.Yes)
                                        continue;
                                    else
                                        break;
                                }

                                // 3) Intersection de bounding boxes
                                var bbWall = selWall.get_BoundingBox(null);
                                var bbElem = selElem.get_BoundingBox(null);
                                if (bbWall == null || bbElem == null)
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
                                        break;
                                }

                                var bbIntersect = IntersectBoundingBoxes(bbWall, bbElem);
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
                                        break;
                                }

                                // 4) Centre et niveau
                                XYZ center = (bbIntersect.Min + bbIntersect.Max) * 0.5;
                                var usedLevel = doc.GetElement(selWall.LevelId) as Level
                                              ?? new FilteredElementCollector(doc)
                                                     .OfClass(typeof(Level))
                                                     .Cast<Level>()
                                                     .FirstOrDefault();

                                // 5) Création instance réservation
                                FamilyInstance fiRes = doc.Create.NewFamilyInstance(
                                    center,
                                    symbol,
                                    selWall,
                                    usedLevel,
                                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                                // 6) Dimensionnement
                                if (isCirculaire)
                                {
                                    double diam = CalculateDiameterForElement(selElem, objType);
                                    if (diam <= 0.0)
                                    {
                                        double w, h;
                                        GetOrientedXYDimensions(selElem, out w, out h);
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
                                    Parameter pH = fiRes.LookupParameter("Hauteur");
                                    Parameter pL = fiRes.LookupParameter("Largeur");

                                    if (objType == ExtendedReservationWindow.ObjectType.Canalisation
                                     || objType == ExtendedReservationWindow.ObjectType.Gaine)
                                    {
                                        double d = CalculateDiameterForElement(selElem, objType);
                                        if (normeEnabled)
                                            d = RoundToNearest50mm(d);

                                        if (pH != null && !pH.IsReadOnly) pH.Set(d);
                                        if (pL != null && !pL.IsReadOnly) pL.Set(d);
                                    }
                                    else
                                    {
                                        double w, h;
                                        GetOrientedXYDimensions(selElem, out w, out h);
                                        if (normeEnabled)
                                        {
                                            w = RoundToNearest10cm(w);
                                            h = RoundToNearest10cm(h);
                                        }
                                        if (pL != null && !pL.IsReadOnly) pL.Set(w);
                                        if (pH != null && !pH.IsReadOnly) pH.Set(h);
                                    }
                                }

                                trans.Commit();
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

                    XYZ center = (bbIntersect.Min + bbIntersect.Max) * 0.5;
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
                        GetOrientedXYDimensions(elem, out w, out h);
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
                        Parameter pH = fiRes.LookupParameter("Hauteur");
                        Parameter pL = fiRes.LookupParameter("Largeur");
                        if (pH != null && !pH.IsReadOnly) pH.Set(finalDiam);
                        if (pL != null && !pL.IsReadOnly) pL.Set(finalDiam);
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
                GetOrientedXYDimensions(elem, out w, out h);
                XYZ center = (bbElem.Min + bbElem.Max) * 0.5;

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
                    Parameter pH = fiRes.LookupParameter("Hauteur");
                    Parameter pL = fiRes.LookupParameter("Largeur");
                    if (normeEnabled)
                    {
                        double newW = RoundToNearest10cm(w);
                        double newH = RoundToNearest10cm(h);
                        if (pL != null && !pL.IsReadOnly) pL.Set(newW);
                        if (pH != null && !pH.IsReadOnly) pH.Set(newH);
                    }
                    else
                    {
                        if (pL != null && !pL.IsReadOnly) pL.Set(w);
                        if (pH != null && !pH.IsReadOnly) pH.Set(h);
                    }
                }
                countCreated++;
            }
        }

        trans.Commit();
        TaskDialog.Show("Réservations créées",
            $"Nombre total de réservations placées : {countCreated}");
    }
}

                // 4) Exécution du script Dynamo (si coché)
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

        private class PipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe;
            public bool AllowReference(Reference reference, XYZ position) => false;
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
            Level level)
        {
            if (!symbol.IsActive) symbol.Activate();

            // 1) Bounding-box du mur
            BoundingBoxXYZ bbWall = wall.get_BoundingBox(null);
            if (bbWall == null) return;

            // 2) Clippez chaque bbox de tuyau à celle du mur
            var clippedBbs = pipes
                .Select(p => p.get_BoundingBox(null))
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
            var pW = fi.LookupParameter("Largeur");
            var pH = fi.LookupParameter("Hauteur");
            if (pW != null && !pW.IsReadOnly) pW.Set(widthRaw);
            if (pH != null && !pH.IsReadOnly) pH.Set(heightRaw);
        }



        private BoundingBoxXYZ IntersectBoundingBoxes(BoundingBoxXYZ bb1, BoundingBoxXYZ bb2)
        {
            double minX = Math.Max(bb1.Min.X, bb2.Min.X);
            double maxX = Math.Min(bb1.Max.X, bb2.Max.X);
            if (minX > maxX) return null;
            double minY = Math.Max(bb1.Min.Y, bb2.Min.Y);
            double maxY = Math.Min(bb1.Max.Y, bb2.Max.Y);
            if (minY > maxY) return null;
            double minZ = Math.Max(bb1.Min.Z, bb2.Min.Z);
            double maxZ = Math.Min(bb1.Max.Z, bb2.Max.Z);
            if (minZ > maxZ) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private bool CheckSelectedElementType(Element elem, ExtendedReservationWindow.ObjectType objType)
        {
            return objType switch
            {
                ExtendedReservationWindow.ObjectType.Canalisation => elem is Pipe,
                ExtendedReservationWindow.ObjectType.Gaine => elem is Duct,
                ExtendedReservationWindow.ObjectType.Porte => elem is FamilyInstance fi1
                    && fi1.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Doors,
                ExtendedReservationWindow.ObjectType.Fenetre => elem is FamilyInstance fi2
                    && fi2.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Windows,
                ExtendedReservationWindow.ObjectType.Autre => true,
                _ => false
            };
        }

        private double CalculateDiameterForElement(Element elem, ExtendedReservationWindow.ObjectType objType)
        {
            double finalDiam = 0.0;
            if (objType == ExtendedReservationWindow.ObjectType.Canalisation && elem is Pipe pipe)
            {
                var pDiam = pipe.LookupParameter("Diamètre");
                var diamVal = pDiam != null ? pDiam.AsDouble() : 0.0;
                var pIso = pipe.LookupParameter("Epaisseur d'isolation");
                var isoVal = pIso != null ? pIso.AsDouble() : 0.0;
                finalDiam = diamVal + 2 * isoVal + OVERSIZE_FT;
            }
            else if (objType == ExtendedReservationWindow.ObjectType.Gaine && elem is Duct duct)
            {
                var pDiam = duct.LookupParameter("Diamètre");
                var diamVal = pDiam != null ? pDiam.AsDouble() : 0.0;
                var pIso = duct.LookupParameter("Epaisseur d'isolation");
                var isoVal = pIso != null ? pIso.AsDouble() : 0.0;
                finalDiam = diamVal + 2 * isoVal + OVERSIZE_FT;
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

        private void GetOrientedXYDimensions(Element elem, out double width, out double height)
        {
            var bb = elem.get_BoundingBox(null);
            if (bb == null)
            {
                width = height = 0;
                return;
            }
            height = (bb.Max.Z - bb.Min.Z) + OVERSIZE_FT;

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
                    width = (maxProj - minProj) + OVERSIZE_FT;
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
                    width = (ptsLocal.Max(p => p.X) - ptsLocal.Min(p => p.X)) + OVERSIZE_FT;
                    return;
                }
            }
            width = (bb.Max.X - bb.Min.X) + OVERSIZE_FT;
        }
        #endregion
    }
}
