using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMaestro.Tutorials
{
    [Transaction(TransactionMode.Manual)]
    public sealed class CreateDemoProjectCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "CreateDemoProject";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                DemoProjectBuilder.Create(data.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                DemoTourMessage.Show(data.Application.MainWindowHandle, "Bulbizarre a besoin d'aide",
                    "Création de la maquette impossible : " + ex.Message);
                return Result.Failed;
            }
        }
    }

    internal static class DemoProjectBuilder
    {
        internal const string DemoPrefix = "BIMaestro_DEMO_";
        private static double M(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

        internal static string Create(UIApplication uiApp)
        {
            string version = uiApp.Application.VersionNumber;
            string templateFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + version, "Templates");
            string template = new[] { "Default_M_FRA.rte", "Default_M_ENU.rte", "Default_I_ENU.rte" }
                .Select(name => Path.Combine(templateFolder, name)).FirstOrDefault(File.Exists);
            if (template == null) throw new FileNotFoundException("Aucun gabarit de projet Revit n'a été trouvé pour " + version);

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "BIMaestro_Apprentissage_" + version + ".rvt");
            if (File.Exists(path)) path = Path.Combine(folder,
                "BIMaestro_Apprentissage_" + version + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".rvt");

            Document doc = uiApp.Application.NewProjectDocument(template);
            if (doc == null) throw new InvalidOperationException("Revit n'a pas créé le projet de démonstration.");
            bool saved = false;
            try
            {
                CheckRequiredFamilies(doc);
                using (var tx = new Transaction(doc, "BIMaestro - Construire la maquette de formation"))
                {
                    tx.Start();
                    Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(candidate => Math.Abs(candidate.Elevation)).FirstOrDefault();
                    if (level == null) level = Level.Create(doc, 0);
                    WallType wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                        .Cast<WallType>().FirstOrDefault(candidate => candidate.Kind == WallKind.Basic);
                    if (wallType == null) throw new InvalidOperationException("Le gabarit ne contient pas de type de mur de base.");

                    Wall openingWall = Wall.Create(doc,
                        Line.CreateBound(new XYZ(M(-4), 0, 0), new XYZ(M(4), 0, 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);
                    openingWall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "RESERVATION_MUR");
                    Wall.Create(doc, Line.CreateBound(new XYZ(M(-4), M(8), 0), new XYZ(M(1), M(8), 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);
                    Wall.Create(doc, Line.CreateBound(new XYZ(M(-4), M(4), 0), new XYZ(M(-4), M(8), 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);

                    PipingSystemType system = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();
                    PipeType pipeType = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipeType)).Cast<PipeType>()
                        .OrderByDescending(candidate => HasElbowRule(doc, candidate.RoutingPreferenceManager))
                        .FirstOrDefault();
                    if (system == null || pipeType == null)
                        throw new InvalidOperationException("Le gabarit ne contient pas de canalisation MEP.");
                    EnsureElbowRule(doc, pipeType.RoutingPreferenceManager,
                        Path.Combine("Pipe", "Fittings", "Generic", "M_Elbow - Welded - Generic.rfa"));
                    Pipe pipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id,
                        new XYZ(0, M(-2), M(1.5)), new XYZ(0, M(2), M(1.5)));
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(M(0.1));
                    pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "RESERVATION_CANALISATION");
                    AddCalculationNetworks(doc, level, system, pipeType);
                    AddBoosterScene(doc, level, system, pipeType);
                    AddOrganizerScene(doc, level);
                    CreateExcelScene(doc, level);

                    ViewFamilyType viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>().FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);
                    if (viewType == null) throw new InvalidOperationException("Le gabarit ne contient pas de type de vue 3D.");
                    View3D mainView = View3D.CreateIsometric(doc, viewType.Id);
                    mainView.Name = "BIMaestro - 01 Auto réservation";
                    ConfigureTrainingView(mainView);
                    mainView.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(M(-5), M(-3), M(-0.5)),
                        Max = new XYZ(M(5), M(3), M(4))
                    });
                    View3D historyView = View3D.CreateIsometric(doc, viewType.Id);
                    historyView.Name = "BIMaestro - 02 Qui a fait ça";
                    ConfigureTrainingView(historyView);
                    historyView.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(M(-5), M(3), M(-0.5)),
                        Max = new XYZ(M(2), M(9), M(4))
                    });
                    View3D colorsView = View3D.CreateIsometric(doc, viewType.Id);
                    colorsView.Name = "BIMaestro - 03 Couleurs et vues";
                    ConfigureTrainingView(colorsView);
                    colorsView.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(M(-5), M(-3), M(-0.5)),
                        Max = new XYZ(M(5), M(9), M(4))
                    });
                    CreateTrainingView(doc, viewType, "BIMaestro - 04 Calcul des canalisations",
                        new XYZ(M(-7), M(10), M(-0.5)), new XYZ(M(7), M(21), M(4)));
                    View3D organizerView = CreateTrainingView(doc, viewType, "BIMaestro - 05 Organisateur",
                        OrganizerSectionMin(), OrganizerSectionMax());
                    SetOrganizerInitialOrientation(organizerView);
                    ConfigureOrganizerSceneView(doc, organizerView, level);
                    CreateTrainingView(doc, viewType, "BIMaestro - 06 Gabarit source",
                        new XYZ(M(-8), M(10), M(-0.5)), new XYZ(M(8), M(29), M(4)));
                    CreateTrainingView(doc, viewType, "BIMaestro - 07 Gabarit cible",
                        new XYZ(M(-8), M(10), M(-0.5)), new XYZ(M(8), M(29), M(4)));
                    StartingViewSettings.GetStartingViewSettings(doc).ViewId = mainView.Id;

                    string familyPath = TrainingFamilyPath("CML_Réservation rectangulaire murale");
                    if (!HasFamily(doc, "CML_Réservation rectangulaire murale") &&
                        (!doc.LoadFamily(familyPath, out Family family) || family == null))
                        throw new InvalidOperationException("La famille de réservation murale n'a pas pu être chargée.");
                    AddHistoryFurniture(doc, level);
                    tx.Commit();
                }

                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
                saved = true;
            }
            finally
            {
                doc.Close(false);
            }
            if (saved)
            {
                uiApp.OpenAndActivateDocument(path);
                // The event also queues this invitation for files opened manually.
                DemoTrainingInvitation.OnDocumentOpened(uiApp.ActiveUIDocument?.Document);
            }
            return path;
        }

        internal static void ConfigureTrainingView(View3D view)
        {
            view.DetailLevel = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.FlatColors;
        }

        private static View3D CreateTrainingView(Document doc, ViewFamilyType type, string name, XYZ min, XYZ max)
        {
            View3D view = View3D.CreateIsometric(doc, type.Id);
            view.Name = name;
            ConfigureTrainingView(view);
            view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
            return view;
        }

        private static void AddCalculationNetworks(Document doc, Level level, PipingSystemType system, PipeType type)
        {
            AddPipeRun(doc, level, system, type, 0.10, "CALCUL_DN100", new[]
            {
                new XYZ(M(-4), M(11.5), M(1.3)), new XYZ(M(-1), M(11.5), M(1.3)),
                new XYZ(M(-1), M(13.5), M(1.3)), new XYZ(M(3), M(13.5), M(1.3)),
                new XYZ(M(3), M(15.5), M(1.3))
            });
            AddPipeRun(doc, level, system, type, 0.05, "CALCUL_DN50", new[]
            {
                new XYZ(M(-4), M(17), M(1.3)), new XYZ(M(-2), M(17), M(1.3)),
                new XYZ(M(-2), M(19), M(1.3)), new XYZ(M(2), M(19), M(1.3))
            });
            AddDuctRun(doc, level);
        }

        internal static View3D EnsureBoosterScene(Document doc)
        {
            if (doc == null || !Path.GetFileName(doc.PathName).StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre la maquette de formation BIMaestro pour ce parcours.");
            using (var tx = new Transaction(doc, "BIMaestro - Scène MEP Booster"))
            {
                tx.Start();
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(item => Math.Abs(item.Elevation)).First();
                var system = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().First();
                var type = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                    .OrderByDescending(item => HasElbowRule(doc, item.RoutingPreferenceManager)).First();
                EnsureElbowRule(doc, type.RoutingPreferenceManager, Path.Combine("Pipe", "Fittings", "Generic", "M_Elbow - Welded - Generic.rfa"));
                AddBoosterScene(doc, level, system, type);
                tx.Commit();
            }
            return new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().First(view => view.Name == "BIMaestro - 09 MEP Booster");
        }

        private static void AddBoosterScene(Document doc, Level level, PipingSystemType system, PipeType type)
        {
            for (int row = 0; row < 2; row++)
            {
                string network = "BOOSTER_DN" + (row == 0 ? "100" : "50");
                if (new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>()
                    .Any(pipe => (pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "").StartsWith(DemoPrefix + network)))
                {
                    AddBoosterExtraAccessories(doc, level, network, row == 0 ? 0.1 : 0.05);
                    continue;
                }
                double y = 12 + row * 7;
                double diameter = row == 0 ? 0.1 : 0.05;
                AddPipeRun(doc, level, system, type, diameter, network, new[] {
                    new XYZ(M(20), M(y), M(1.3)), new XYZ(M(26), M(y), M(1.3)),
                    new XYZ(M(26), M(y + 4), M(1.3)), new XYZ(M(20), M(y + 4), M(1.3)) });
                for (int segment = 2; segment <= 3; segment++)
                {
                    Pipe pipe = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>()
                        .First(item => item.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoPrefix + network + "_" + segment);
                    AddInlineTrainingValve(doc, level, pipe, diameter, network + "_ACCESSOIRE_" + segment);
                }
            }
            foreach (string network in new[] { "BOOSTER_DN100", "BOOSTER_DN50" })
                AddBoosterExtraAccessories(doc, level, network, network.EndsWith("100") ? 0.1 : 0.05);
            if (!new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(view => view.Name == "BIMaestro - 09 MEP Booster"))
            {
                var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(item => item.ViewFamily == ViewFamily.ThreeDimensional);
                var view = CreateTrainingView(doc, viewType, "BIMaestro - 09 MEP Booster", new XYZ(M(18), M(10), M(0.5)), new XYZ(M(28), M(25), M(3)));
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.Shading;
                XYZ forward = new XYZ(-1, 1, -0.8).Normalize();
                XYZ right = forward.CrossProduct(XYZ.BasisZ).Normalize();
                view.SetOrientation(new ViewOrientation3D(new XYZ(M(40), M(-15), M(35)), right.CrossProduct(forward).Normalize(), forward));
            }
        }

        private static void AddBoosterExtraAccessories(Document doc, Level level, string network, double diameter)
        {
            var accessories = new[] {
                new { Tag = "FILTRE", Family = "Filtre à tamis en Y - 50-500 mm - A brides", Segment = 1 },
                new { Tag = "COMPTEUR", Family = "CML_Compteur d'eau à brides DN40-150", Segment = 3 }
            };
            foreach (var item in accessories)
            {
                string marker = network + "_" + item.Tag;
                if (new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Any(instance => instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoPrefix + marker + "_VANNE")) continue;
                var pipe = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>()
                    .FirstOrDefault(element => element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoPrefix + network + "_" + item.Segment);
                if (pipe == null) throw new InvalidOperationException("Le tronçon prévu pour " + item.Family + " est introuvable.");
                AddInlineTrainingValve(doc, level, pipe, diameter, marker, item.Family);
            }
        }

        private static void AddPipeRun(Document doc, Level level, PipingSystemType system,
            PipeType type, double diameter, string markPrefix, XYZ[] points)
        {
            var pipes = new List<Pipe>();
            for (int index = 0; index < points.Length - 1; index++)
            {
                Pipe pipe = Pipe.Create(doc, system.Id, type.Id, level.Id, points[index], points[index + 1]);
                pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(M(diameter));
                pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + markPrefix + "_" + (index + 1));
                pipes.Add(pipe);
            }
            doc.Regenerate();
            for (int index = 1; index < pipes.Count; index++)
            {
                XYZ junction = points[index];
                Connector left = NearestConnector(pipes[index - 1], junction);
                Connector right = NearestConnector(pipes[index], junction);
                FamilyInstance elbow;
                try { elbow = doc.Create.NewElbowFitting(left, right); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Impossible de créer le coude " + index +
                        " du réseau " + markPrefix + " avec le type de canalisation « " + type.Name +
                        " » (déjà relié : " + left.IsConnectedTo(right) + "). " + ex.Message, ex);
                }
                elbow.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(
                    DemoPrefix + markPrefix + "_COUDE_" + index);
            }
            AddInlineTrainingValve(doc, level, pipes[0], diameter, markPrefix);
        }

        internal static void EnsureCalculationValves(Document doc)
        {
            using (var transaction = new Transaction(doc, "BIMaestro - Ajouter les vannes de formation"))
            {
                transaction.Start();
                foreach (string network in new[] { "CALCUL_DN100", "CALCUL_DN50" })
                {
                    string mark = DemoPrefix + network;
                    if (new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .Any(item => item.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == mark + "_VANNE")) continue;
                    Pipe pipe = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>()
                        .FirstOrDefault(item => item.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == mark + "_1");
                    if (pipe == null) throw new InvalidOperationException("Le réseau " + network + " manque dans la maquette de formation.");
                    AddInlineTrainingValve(doc, doc.GetElement(pipe.LevelId) as Level, pipe,
                        network == "CALCUL_DN100" ? 0.1 : 0.05, network);
                }
                transaction.Commit();
            }
        }

        private static void AddInlineTrainingValve(Document doc, Level level, Pipe pipe, double diameter, string network, string familyName = "Vanne papillon - 50-300 mm")
        {
            Family family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(item => item.Name == familyName);
            if (family == null && (!doc.LoadFamily(TrainingFamilyPath(familyName), out family) || family == null))
                throw new InvalidOperationException("L’accessoire de formation n’a pas pu être chargé : " + familyName);
            FamilySymbol source = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).First();
            string typeName = "BIMaestro DN" + (diameter * 1000).ToString("0");
            FamilySymbol symbol = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(item => item.Name == typeName) ?? (FamilySymbol)source.Duplicate(typeName);
            if (symbol.Category?.Id.GetIdValue() != (int)BuiltInCategory.OST_PipeAccessory)
                throw new InvalidOperationException("La vanne doit appartenir à la catégorie Accessoires de canalisation.");
            if (!symbol.IsActive) symbol.Activate();
            string originalMark = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            XYZ middle = ((LocationCurve)pipe.Location).Curve.Evaluate(0.5, true);
            FamilyInstance valve = doc.Create.NewFamilyInstance(middle, symbol, level, StructuralType.NonStructural);
            foreach (Element element in new Element[] { symbol, valve })
                foreach (string name in new[] { "Diamètre nominal", "Diamètre", "DN", "Diamètre Nominal", "Diamètre de raccordement", "Nominal Diameter", "Diameter" })
                {
                    Parameter parameter = element.LookupParameter(name);
                    if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double &&
                        (parameter.Definition.GetDataType() == SpecTypeId.Length ||
                         parameter.Definition.GetDataType() == SpecTypeId.PipeSize)) parameter.Set(M(diameter));
                }
            doc.Regenerate();
            var connectors = valve.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                .Where(item => item.Domain == Domain.DomainPiping && item.ConnectorType == ConnectorType.End).ToList();
            if (connectors == null || connectors.Count != 2)
                throw new InvalidOperationException("L’accessoire " + familyName + " doit avoir deux connecteurs de canalisation.");
            foreach (Connector connector in connectors)
            {
                if (Math.Abs(connector.Radius * 2 - M(diameter)) <= M(0.001)) continue;
                if (!SetTrainingConnectorSize(valve, connector, BuiltInParameter.CONNECTOR_DIAMETER, M(diameter)))
                    SetTrainingConnectorSize(valve, connector, BuiltInParameter.CONNECTOR_RADIUS, M(diameter) / 2);
            }
            doc.Regenerate();
            connectors = valve.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(item => item.Domain == Domain.DomainPiping && item.ConnectorType == ConnectorType.End).ToList();
            if (connectors.Any(item => Math.Abs(item.Radius * 2 - M(diameter)) > M(0.001)))
                throw new InvalidOperationException("Le diamètre des connecteurs de la vanne ne correspond pas au DN du réseau " + network +
                    " (diamètres obtenus en mm : " + string.Join(", ", connectors.Select(item =>
                        UnitUtils.ConvertFromInternalUnits(item.Radius * 2, UnitTypeId.Millimeters).ToString("0.##"))) + ").");
            XYZ direction = (((LocationCurve)pipe.Location).Curve.GetEndPoint(1) -
                ((LocationCurve)pipe.Location).Curve.GetEndPoint(0)).Normalize();
            XYZ valveDirection = (connectors[1].Origin - connectors[0].Origin).Normalize();
            double angle = Math.Atan2(valveDirection.CrossProduct(direction).Z, valveDirection.DotProduct(direction));
            ElementTransformUtils.RotateElement(doc, valve.Id, Line.CreateBound(middle, middle + XYZ.BasisZ), angle);
            doc.Regenerate();
            ElementTransformUtils.MoveElement(doc, valve.Id, middle - (connectors[0].Origin + connectors[1].Origin) * 0.5);
            doc.Regenerate();
            connectors = connectors.OrderBy(item => item.Origin.DotProduct(direction)).ToList();
            XYZ start = ((LocationCurve)pipe.Location).Curve.GetEndPoint(0);
            XYZ end = ((LocationCurve)pipe.Location).Curve.GetEndPoint(1);
            Pipe second = doc.GetElement(PlumbingUtils.BreakCurve(doc, pipe.Id, middle)) as Pipe;
            Pipe first = pipe;
            if (((LocationCurve)first.Location).Curve.GetEndPoint(0).DistanceTo(start) > M(0.01))
            { first = second; second = pipe; }
            ((LocationCurve)first.Location).Curve = Line.CreateBound(start, connectors[0].Origin);
            ((LocationCurve)second.Location).Curve = Line.CreateBound(connectors[1].Origin, end);
            first.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(originalMark ?? DemoPrefix + network + "_1");
            second.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + network + "_VANNE_TRONCON");
            valve.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + network + "_VANNE");
            doc.Regenerate();
            NearestConnector(first, connectors[0].Origin).ConnectTo(connectors[0]);
            NearestConnector(second, connectors[1].Origin).ConnectTo(connectors[1]);
            doc.Regenerate();
            if (!connectors[0].IsConnected || !connectors[1].IsConnected)
                throw new InvalidOperationException("L’accessoire " + familyName + " n’est pas raccordé des deux côtés.");
        }

        private static bool SetTrainingConnectorSize(FamilyInstance valve, Connector connector,
            BuiltInParameter connectorParameter, double value)
        {
            using (var info = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo)
            {
                if (info == null) return false;
                ElementId id = info.GetAssociateFamilyParameterId(new ElementId(connectorParameter));
                if (id == ElementId.InvalidElementId) return false;
                foreach (Element element in new Element[] { valve, valve.Symbol })
                {
                    Parameter parameter = element.Parameters.Cast<Parameter>().FirstOrDefault(item => item.Id == id);
                    if (parameter == null && valve.Document.GetElement(id) is ParameterElement definition)
                        parameter = element.get_Parameter(definition.GetDefinition());
                    if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double)
                        return parameter.Set(value);
                }
                return false;
            }
        }

        private static void AddDuctRun(Document doc, Level level)
        {
            MechanicalSystemType system = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault();
            DuctType type = new FilteredElementCollector(doc).OfClass(typeof(DuctType))
                .Cast<DuctType>().Where(candidate => candidate.Shape == ConnectorProfileType.Rectangular)
                .OrderByDescending(candidate => HasElbowRule(doc, candidate.RoutingPreferenceManager))
                .FirstOrDefault();
            if (system == null || type == null)
                throw new InvalidOperationException("Le gabarit MEP ne contient pas de système et de gaine rectangulaire pour l'exercice de calcul.");
            EnsureElbowRule(doc, type.RoutingPreferenceManager,
                Path.Combine("Duct", "Fittings", "Rectangular", "Elbows", "M_Rectangular Elbow - Mitered.rfa"));
            XYZ[] points =
            {
                new XYZ(M(-4), M(12.5), M(2.8)), new XYZ(M(-2), M(12.5), M(2.8)),
                new XYZ(M(-2), M(15.5), M(2.8)), new XYZ(M(2), M(15.5), M(2.8))
            };
            var ducts = new List<Duct>();
            for (int index = 0; index < points.Length - 1; index++)
            {
                Duct duct = Duct.Create(doc, system.Id, type.Id, level.Id, points[index], points[index + 1]);
                duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(M(0.4));
                duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(M(0.2));
                duct.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "CALCUL_GAINE_" + (index + 1));
                ducts.Add(duct);
            }
            doc.Regenerate();
            for (int index = 1; index < ducts.Count; index++)
            {
                FamilyInstance elbow = doc.Create.NewElbowFitting(
                    NearestConnector(ducts[index - 1], points[index]),
                    NearestConnector(ducts[index], points[index]));
                elbow.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(
                    DemoPrefix + "CALCUL_GAINE_COUDE_" + index);
            }
        }

        private static Connector NearestConnector(MEPCurve curve, XYZ point) =>
            curve.ConnectorManager.Connectors.Cast<Connector>()
                .OrderBy(connector => connector.Origin.DistanceTo(point)).First();

        private static void EnsureElbowRule(Document doc, RoutingPreferenceManager routing, string relativePath)
        {
            if (routing == null) throw new InvalidOperationException("Le type MEP n'a pas de préférences de routage.");
            if (HasElbowRule(doc, routing)) return;

            // Un autre type du gabarit peut déjà référencer un coude utilisable.
            bool isPipe = relativePath.StartsWith("Pipe", StringComparison.OrdinalIgnoreCase);
            IEnumerable<MEPCurveType> otherTypes = isPipe
                ? new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<MEPCurveType>()
                : new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                    .Where(candidate => candidate.Shape == ConnectorProfileType.Rectangular).Cast<MEPCurveType>();
            FamilySymbol existingSymbol = otherTypes
                .Select(candidate => ElbowSymbol(doc, candidate.RoutingPreferenceManager))
                .FirstOrDefault(candidate => candidate != null);
            if (existingSymbol != null)
            {
                routing.AddRule(RoutingPreferenceRuleGroupType.Elbows,
                    new RoutingPreferenceRule(existingSymbol.Id, "Coude maquette BIMaestro"));
                return;
            }

            string fileName = Path.GetFileName(relativePath);
            string[] familyNames = isPipe
                ? new[] { fileName, "Coude - Générique.rfa" }
                : new[] { fileName, "Coude rectangulaire - En onglet.rfa" };
            string packagedFolder = Path.Combine(Path.GetDirectoryName(typeof(DemoProjectBuilder).Assembly.Location),
                "Demo", "Maquette", "Familles");
            string familyPath = familyNames.Select(name => Path.Combine(packagedFolder, name)).FirstOrDefault(File.Exists);
            if (familyPath == null)
                throw new FileNotFoundException("Le coude de formation manque dans les ressources du plugin : " +
                    string.Join(" ou ", familyNames) + ". Réinstallez BIMaestro avec le dossier Demo\\Maquette\\Familles.");
            Family family;
            if (!doc.LoadFamily(familyPath, out family) || family == null)
                family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(candidate => candidate.Name == Path.GetFileNameWithoutExtension(familyPath));
            FamilySymbol symbol = family?.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(candidate => candidate != null);
            if (symbol == null) throw new InvalidOperationException("La famille de coude n'a pas de type : " + familyPath);
            routing.AddRule(RoutingPreferenceRuleGroupType.Elbows,
                new RoutingPreferenceRule(symbol.Id, "Coude maquette BIMaestro"));
        }

        private static bool HasFamily(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Any(candidate => candidate.Name == name);

        private static void CheckRequiredFamilies(Document doc)
        {
            string[] required = { "CML_Réservation rectangulaire murale", "CML_Parking", "CML_Table ronde + chaise" };
            var missing = required.Where(name => !HasFamily(doc, name) && TrainingFamilyPath(name) == null).ToArray();
            if (missing.Length > 0)
                throw new FileNotFoundException("Des familles de la maquette d’essai manquent dans les ressources du plugin. " +
                    "Réinstallez BIMaestro avec le dossier Demo\\Maquette\\Familles :\n" +
                    string.Join("\n", missing));
        }

        private static string TrainingFamilyPath(string name)
        {
            string fileName = name + ".rfa";
            string pluginFolder = Path.GetDirectoryName(typeof(DemoProjectBuilder).Assembly.Location);
            string packaged = Path.Combine(pluginFolder, "Demo", "Maquette", "Familles", fileName);
            if (File.Exists(packaged)) return packaged;
            return null;
        }

        private static bool HasElbowRule(Document doc, RoutingPreferenceManager routing) =>
            ElbowSymbol(doc, routing) != null;

        private static FamilySymbol ElbowSymbol(Document doc, RoutingPreferenceManager routing)
        {
            if (routing == null) return null;
            for (int index = 0; index < routing.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows); index++)
            {
                RoutingPreferenceRule rule = routing.GetRule(RoutingPreferenceRuleGroupType.Elbows, index);
                FamilySymbol symbol = doc.GetElement(rule.MEPPartId) as FamilySymbol;
                if (symbol != null) return symbol;
            }
            return null;
        }

        private static XYZ OrganizerSectionMin() => new XYZ(M(-8), M(21), M(-0.5));

        private static XYZ OrganizerSectionMax() => new XYZ(M(8), M(31), M(5.5));

        private static void SetOrganizerInitialOrientation(View3D view)
        {
            XYZ center = new XYZ(0, M(25), M(1.5));
            XYZ eye = center + new XYZ(M(12), M(-12), M(12));
            XYZ forward = (center - eye).Normalize();
            XYZ right = forward.CrossProduct(XYZ.BasisZ).Normalize();
            XYZ up = right.CrossProduct(forward).Normalize();
            view.SetOrientation(new ViewOrientation3D(eye, up, forward));
        }

        private static FamilySymbol OrganizerParkingSymbol(Document doc)
        {
            Family family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(candidate => candidate.Name == "CML_Parking");
            if (family == null)
            {
                string parkingPath = TrainingFamilyPath("CML_Parking");
                if (!File.Exists(parkingPath))
                    throw new FileNotFoundException("Famille CML_Parking nécessaire à Organisateur introuvable : " + parkingPath);
                if (!doc.LoadFamily(parkingPath, out family) || family == null)
                    throw new InvalidOperationException("La famille CML_Parking n'a pas pu être chargée.");
            }
            FamilySymbol symbol = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(candidate => candidate != null);
            if (symbol == null) throw new InvalidOperationException("CML_Parking ne contient aucun type.");
            if (!symbol.IsActive) symbol.Activate();
            doc.Regenerate();
            return symbol;
        }

        private static Level OrganizerUpperLevel(Document doc, Level baseLevel)
        {
            Level upperLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(candidate => candidate.Name == "BIMaestro - Niveau 1");
            if (upperLevel != null) return upperLevel;
            upperLevel = Level.Create(doc, baseLevel.Elevation + M(3));
            upperLevel.Name = "BIMaestro - Niveau 1";
            return upperLevel;
        }

        private static void AddOrganizerScene(Document doc, Level baseLevel)
        {
            DemoOrganizerExercise.EnsureSheets(doc);
            FamilySymbol symbol = OrganizerParkingSymbol(doc);
            Level upperLevel = OrganizerUpperLevel(doc, baseLevel);
            var existingMarks = new HashSet<string>(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Select(instance => instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                .Where(mark => !string.IsNullOrEmpty(mark)), StringComparer.Ordinal);
            for (int index = 0; index < 8; index++)
            {
                int number = index + 1;
                string mark = DemoPrefix + "ORGANISATEUR_" + number;
                if (existingMarks.Contains(mark)) continue;
                bool upperRow = index >= 4;
                Level level = upperRow ? upperLevel : baseLevel;
                XYZ position = new XYZ(M(-4.5 + (index % 4) * 3.0),
                    M(upperRow ? 26.5 : 24), level.Elevation);
                FamilyInstance parking = doc.Create.NewFamilyInstance(position, symbol, level,
                    StructuralType.NonStructural);
                Parameter numberParameter = parking.LookupParameter("CML_Numéros de place");
                if (numberParameter == null || numberParameter.IsReadOnly || numberParameter.StorageType != StorageType.String)
                    throw new InvalidOperationException("CML_Parking doit exposer « CML_Numéros de place » comme paramètre texte d'instance modifiable pour l'exercice Organisateur.");
                string initialNumber = "DEMO-" + number.ToString("00");
                numberParameter.Set(initialNumber);
                parking.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(initialNumber);
                parking.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            }
        }

        // Extend an older learning model in place; keep existing parking values and identifiers.
        internal static void EnsureOrganizerScene(Document doc)
        {
            if (doc == null || !Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre d'abord une maquette de formation BIMaestro.");
            Level baseLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(level => Math.Abs(level.Elevation)).FirstOrDefault();
            if (baseLevel == null) throw new InvalidOperationException("Aucun niveau de base trouvé dans la maquette.");
            View3D view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(candidate => candidate.Name == "BIMaestro - 05 Organisateur");
            var organizerPlaces = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoPrefix + "ORGANISATEUR_", StringComparison.Ordinal))
                .ToList();
            var marks = new HashSet<string>(organizerPlaces
                .Select(instance => instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()),
                StringComparer.Ordinal);
            bool missingParking = Enumerable.Range(1, 8)
                .Any(number => !marks.Contains(DemoPrefix + "ORGANISATEUR_" + number));
            bool viewNeedsExpansion = view == null ||
                view.GetSectionBox().Max.Z < M(5.5) - M(0.1) ||
                view.GetSectionBox().Max.Y < M(31) - M(0.1);
            bool commentsNeedSync = organizerPlaces.Any(place =>
                (place.LookupParameter("CML_Numéros de place")?.AsString() ?? "") !=
                (place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? ""));
            using (var tx = new Transaction(doc, "BIMaestro - Étendre la scène Organisateur"))
            {
                tx.Start();
                if (missingParking) AddOrganizerScene(doc, baseLevel);
                if (view == null)
                {
                    ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>().FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional);
                    if (type == null) throw new InvalidOperationException("Le gabarit ne contient pas de type de vue 3D.");
                    view = CreateTrainingView(doc, type, "BIMaestro - 05 Organisateur",
                        OrganizerSectionMin(), OrganizerSectionMax());
                }
                else if (viewNeedsExpansion)
                {
                    view.SetSectionBox(new BoundingBoxXYZ { Min = OrganizerSectionMin(), Max = OrganizerSectionMax() });
                    view.IsSectionBoxActive = true;
                    ConfigureTrainingView(view);
                }
                if (missingParking || viewNeedsExpansion) SetOrganizerInitialOrientation(view);
                ConfigureOrganizerSceneView(doc, view, baseLevel);
                if (commentsNeedSync)
                    foreach (FamilyInstance place in organizerPlaces)
                    {
                        Parameter cml = place.LookupParameter("CML_Numéros de place");
                        Parameter comments = place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (cml == null || comments == null || comments.IsReadOnly ||
                            comments.StorageType != StorageType.String) continue;
                        comments.Set(cml.AsString() ?? "");
                    }
                tx.Commit();
            }
        }

        private static void ConfigureOrganizerSceneView(Document doc, View3D view, Level baseLevel)
        {
            var parking = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(instance => instance.Symbol.Family.Name == "CML_Parking").ToList();
            var places = parking.Where(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                .StartsWith(DemoPrefix + "ORGANISATEUR_", StringComparison.Ordinal)).ToList();
            foreach (FamilyInstance place in places)
            {
                string mark = place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                int index;
                if (!int.TryParse(mark.Substring((DemoPrefix + "ORGANISATEUR_").Length), out index) ||
                    !(place.Location is LocationPoint point)) continue;
                double elevation = baseLevel.Elevation + (index > 4 ? M(3) : 0);
                double delta = elevation - point.Point.Z;
                if (Math.Abs(delta) > M(0.001))
                    ElementTransformUtils.MoveElement(doc, place.Id, new XYZ(0, 0, delta));
                if (place.IsHidden(view)) view.UnhideElements(new[] { place.Id });
            }
            doc.Regenerate();
            var boxes = places.Select(place => place.get_BoundingBox(null)).Where(box => box != null).ToList();
            if (boxes.Count > 0)
            {
                XYZ margin = new XYZ(M(0.75), M(0.75), M(0.75));
                view.SetSectionBox(new BoundingBoxXYZ
                {
                    Min = new XYZ(boxes.Min(box => box.Min.X), boxes.Min(box => box.Min.Y), boxes.Min(box => box.Min.Z)) - margin,
                    Max = new XYZ(boxes.Max(box => box.Max.X), boxes.Max(box => box.Max.Y), boxes.Max(box => box.Max.Z)) + margin
                });
            }
            view.IsSectionBoxActive = true;
            view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true);
            var unrelated = parking.Except(places).Where(place => place.CanBeHidden(view) && !place.IsHidden(view))
                .Select(place => place.Id).ToList();
            if (unrelated.Count > 0) view.HideElements(unrelated);
        }

        internal static void ResetOrganizerViewOrientation(Document doc)
        {
            View3D view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(candidate => candidate.Name == "BIMaestro - 05 Organisateur");
            if (view == null)
                throw new InvalidOperationException("La vue 05 Organisateur manque dans la maquette de formation.");
            if (doc.IsModifiable)
            {
                SetOrganizerInitialOrientation(view);
                return;
            }
            using (var tx = new Transaction(doc, "BIMaestro - Réorienter la vue Organisateur"))
            {
                tx.Start();
                SetOrganizerInitialOrientation(view);
                tx.Commit();
            }
        }

        internal static ViewSchedule EnsureExcelScene(Document doc)
        {
            if (doc == null || !Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre d'abord une maquette de formation BIMaestro.");
            ViewSchedule schedule = FindExcelSchedule(doc);
            if (schedule != null && FindExcelParking(doc).Count == 4 &&
                HasExcelExerciseColumns(schedule)) return schedule;
            Level level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(candidate => Math.Abs(candidate.Elevation)).FirstOrDefault();
            if (level == null) throw new InvalidOperationException("Aucun niveau trouvé pour les places de parking.");
            using (var tx = new Transaction(doc, "BIMaestro - Préparer le parcours Gestion Excel"))
            {
                tx.Start();
                schedule = CreateExcelScene(doc, level);
                tx.Commit();
            }
            return schedule;
        }

        internal static ViewSchedule FindExcelSchedule(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(view => view.Name == "BIMaestro - 08 Gestion Excel");

        internal static List<FamilyInstance> FindExcelParking(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoPrefix + "EXCEL_", StringComparison.Ordinal))
                .ToList();

        private static bool HasExcelExerciseColumns(ViewSchedule schedule)
        {
            var fields = schedule.Definition.GetFieldOrder().Select(schedule.Definition.GetField).ToList();
            var visible = fields.Where(field => !field.IsHidden).ToList();
            bool familyNumberAvailable = schedule.Definition.GetSchedulableFields().Any(field =>
                field.GetName(schedule.Document) == "CML_Numéros de place");
            return visible.Count == 2 &&
                visible.Any(field => field.HasSchedulableField &&
                    field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK) &&
                (familyNumberAvailable
                    ? visible.Any(field => field.GetName() == "CML_Numéros de place")
                    : visible.Any(field => field.HasSchedulableField &&
                        field.ParameterId.GetIdLongValue() ==
                        (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
        }

        private static ViewSchedule CreateExcelScene(Document doc, Level level)
        {
            Family family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(candidate => candidate.Name == "CML_Parking");
            FamilySymbol symbol = family?.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(candidate => candidate != null);
            if (symbol == null)
                throw new InvalidOperationException("La famille CML_Parking manque. Crée une nouvelle maquette de formation.");
            if (!symbol.IsActive) symbol.Activate();
            doc.Regenerate();
            var existing = FindExcelParking(doc);
            for (int index = 1; index <= 4; index++)
            {
                string mark = DemoPrefix + "EXCEL_" + index;
                if (existing.Any(instance => instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == mark))
                    continue;
                FamilyInstance parking = doc.Create.NewFamilyInstance(
                    new XYZ(M(-4.5 + (index - 1) * 3.0), M(32), 0), symbol, level,
                    StructuralType.NonStructural);
                Parameter number = parking.LookupParameter("CML_Numéros de place");
                if (number == null || number.IsReadOnly || number.StorageType != StorageType.String)
                    throw new InvalidOperationException("CML_Numéros de place doit être un paramètre texte d'instance modifiable.");
                number.Set("XL-" + index.ToString("D3"));
                parking.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?
                    .Set("XL-" + index.ToString("D3"));
                parking.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            }

            ViewFamilyType viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);
            if (viewType != null && !new FilteredElementCollector(doc).OfClass(typeof(View3D))
                .Cast<View3D>().Any(view => view.Name == "BIMaestro - 08 Parking Excel"))
                CreateTrainingView(doc, viewType, "BIMaestro - 08 Parking Excel",
                    new XYZ(M(-8), M(29), M(-0.5)), new XYZ(M(8), M(36), M(4)));

            ViewSchedule schedule = FindExcelSchedule(doc);
            if (schedule == null)
            {
                schedule = ViewSchedule.CreateSchedule(doc, symbol.Category.Id);
                schedule.Name = "BIMaestro - 08 Gestion Excel";
            }
            ScheduleDefinition definition = schedule.Definition;
            var available = definition.GetSchedulableFields();
            SchedulableField familyNumberField = available.FirstOrDefault(field =>
                field.GetName(doc) == "CML_Numéros de place");
            SchedulableField commentsField = available.FirstOrDefault(field =>
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            SchedulableField numberField = familyNumberField ?? commentsField;
            SchedulableField markField = available.FirstOrDefault(field =>
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK);
            if (numberField == null || markField == null)
                throw new InvalidOperationException("La famille CML_Parking ne fournit pas les champs nécessaires à la nomenclature Excel.");
            var existingFields = definition.GetFieldOrder().Select(definition.GetField).ToList();
            ScheduleField numberColumn = existingFields.FirstOrDefault(field => field.GetName() == numberField.GetName(doc))
                ?? definition.AddField(numberField);
            ScheduleField markColumn = existingFields.FirstOrDefault(field => field.HasSchedulableField &&
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK)
                ?? definition.AddField(markField);
            // Deux colonnes utiles : le numéro affiché dans le modèle et le repère fixe.
            // Une ancienne nomenclature à trois colonnes est migrée sans toucher aux valeurs.
            foreach (ScheduleField field in definition.GetFieldOrder().Select(definition.GetField))
                field.IsHidden = !field.FieldId.Equals(numberColumn.FieldId) &&
                    !field.FieldId.Equals(markColumn.FieldId);
            numberColumn.IsHidden = false;
            markColumn.IsHidden = false;
            if (definition.GetFilterCount() == 0)
                definition.AddFilter(new ScheduleFilter(markColumn.FieldId,
                    ScheduleFilterType.BeginsWith, DemoPrefix + "EXCEL_"));
            if (definition.GetSortGroupFieldCount() == 0)
                definition.AddSortGroupField(new ScheduleSortGroupField(numberColumn.FieldId));
            definition.IsItemized = true;
            return schedule;
        }

        internal static string ExcelValueField(ViewSchedule schedule)
        {
            var visible = schedule.Definition.GetFieldOrder().Select(schedule.Definition.GetField)
                .Where(field => !field.IsHidden).ToList();
            ScheduleField number = visible.FirstOrDefault(field => field.GetName() == "CML_Numéros de place") ??
                visible.FirstOrDefault(field => field.HasSchedulableField &&
                    field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (number == null)
                throw new InvalidOperationException("Le numéro de place est absent de la nomenclature Excel.");
            return number.GetName();
        }

        internal static string ExcelReferenceField(ViewSchedule schedule) => schedule.Definition
            .GetFieldOrder().Select(schedule.Definition.GetField)
            .First(field => field.HasSchedulableField &&
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK)
            .GetName();

        internal static Parameter ExcelValueParameter(FamilyInstance place, string fieldName) =>
            fieldName == "CML_Numéros de place" ? place.LookupParameter(fieldName) :
            place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

        // La famille CML_Parking affiche son texte 3D à partir de Commentaires. Le
        // numéro peut toutefois être exporté via son paramètre CML s'il est schedulable.
        // Cette synchronisation ne touche jamais au Repère, utilisé comme clé fixe.
        internal static void SynchronizeExcelParkingNumbers(IEnumerable<FamilyInstance> places,
            string valueField, bool recoverLegacyValue = false)
        {
            bool cmlIsSource = valueField == "CML_Numéros de place";
            foreach (FamilyInstance place in places)
            {
                Parameter cml = place.LookupParameter("CML_Numéros de place");
                Parameter comments = place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (cml == null || comments == null || cml.IsReadOnly || comments.IsReadOnly ||
                    cml.StorageType != StorageType.String || comments.StorageType != StorageType.String)
                    throw new InvalidOperationException("Les paramètres du numéro de place ne sont pas modifiables.");
                string source = (cmlIsSource ? cml : comments).AsString() ?? "";
                string other = (cmlIsSource ? comments : cml).AsString() ?? "";
                // Les anciennes scènes utilisaient Commentaires pour « Secteur A/B ».
                // Dans le mode de repli, récupérer le vrai numéro CML avant de masquer
                // l'ancienne colonne de secteur.
                if (recoverLegacyValue && (string.IsNullOrWhiteSpace(source) || (!cmlIsSource &&
                    source.StartsWith("Secteur ", StringComparison.OrdinalIgnoreCase))))
                    source = other;
                if (cml.AsString() != source) cml.Set(source);
                if (comments.AsString() != source) comments.Set(source);
            }
        }

        internal static XYZ HistoryFurniturePosition(int index) =>
            new XYZ(M(-2.9 + index * 1.5), M(6), 0);

        internal static string HistoryFurnitureMark(int index) =>
            DemoPrefix + "HISTORIQUE_" + (index == 0 ? "TEMOIN" : "A_RESTAURER_" + index);

        internal static void UpdateTrainingViews(Document doc)
        {
            if (doc == null || !Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase)) return;

            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(view => DemoTourCatalog.Views.Values.Contains(view.Name) ||
                    view.Name == "BIMaestro - 08 Parking Excel")
                .Where(view => view.DetailLevel != ViewDetailLevel.Fine || view.DisplayStyle != DisplayStyle.FlatColors)
                .ToList();
            var frontWalls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(wall => wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == DemoPrefix + "HISTORIQUE_MODIFIER_MOI")
                .Select(wall => wall.Id).ToList();
            if (views.Count == 0 && frontWalls.Count == 0) return;

            using (var tx = new Transaction(doc, "BIMaestro - Affichage des vues de formation"))
            {
                tx.Start();
                if (frontWalls.Count > 0) doc.Delete(frontWalls);
                foreach (View3D view in views) ConfigureTrainingView(view);
                tx.Commit();
            }
        }

        private static void AddHistoryFurniture(Document doc, Level level)
        {
            Family furniture = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(candidate => candidate.Name == "CML_Table ronde + chaise");
            if (furniture == null)
            {
                string furniturePath = TrainingFamilyPath("CML_Table ronde + chaise");
                if (furniturePath == null)
                    throw new FileNotFoundException("Famille de mobilier nécessaire au parcours historique introuvable.");
                if (!doc.LoadFamily(furniturePath, out furniture) || furniture == null)
                    throw new InvalidOperationException("La famille de mobilier n'a pas pu être chargée : " + furniturePath);
            }
            FamilySymbol symbol = furniture.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(candidate => candidate != null);
            if (symbol == null) throw new InvalidOperationException("La famille de mobilier ne contient aucun type.");
            if (!symbol.IsActive) symbol.Activate();
            doc.Regenerate();
            for (int i = 0; i < 3; i++)
            {
                FamilyInstance instance = doc.Create.NewFamilyInstance(
                    HistoryFurniturePosition(i), symbol, level, StructuralType.NonStructural);
                instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(HistoryFurnitureMark(i));
                DemoHistoryScene.ChairCount(instance).Set(4);
            }
        }
    }
}
