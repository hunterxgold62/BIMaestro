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
                string path = DemoProjectBuilder.Create(data.Application);
                DemoTourMessage.Show(data.Application.MainWindowHandle, "Pika ! Maquette prête",
                    "Maquette de formation créée :\n" + path +
                    "\n\nPikachu va te proposer les tutoriels disponibles.",
                    "Choisir un tutoriel");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                DemoTourMessage.Show(data.Application.MainWindowHandle, "Pikachu a besoin d'aide",
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
                "BIMaestro", "Demo");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "BIMaestro_Apprentissage_" + version + ".rvt");
            if (File.Exists(path)) path = Path.Combine(folder,
                "BIMaestro_Apprentissage_" + version + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".rvt");

            Document doc = uiApp.Application.NewProjectDocument(template);
            if (doc == null) throw new InvalidOperationException("Revit n'a pas créé le projet de démonstration.");
            bool saved = false;
            try
            {
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
                    Wall historyWall = Wall.Create(doc,
                        Line.CreateBound(new XYZ(M(-4), M(4), 0), new XYZ(M(1), M(4), 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);
                    historyWall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "HISTORIQUE_MODIFIER_MOI");
                    Wall.Create(doc, Line.CreateBound(new XYZ(M(-4), M(8), 0), new XYZ(M(1), M(8), 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);
                    Wall.Create(doc, Line.CreateBound(new XYZ(M(-4), M(4), 0), new XYZ(M(-4), M(8), 0)),
                        wallType.Id, level.Id, M(3), 0, false, false);

                    PipingSystemType system = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();
                    PipeType pipeType = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
                    if (system == null || pipeType == null)
                        throw new InvalidOperationException("Le gabarit ne contient pas de canalisation MEP.");
                    EnsureElbowRule(doc, pipeType.RoutingPreferenceManager,
                        Path.Combine("Pipe", "Fittings", "Generic", "M_Elbow - Welded - Generic.rfa"));
                    Pipe pipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id,
                        new XYZ(0, M(-2), M(1.5)), new XYZ(0, M(2), M(1.5)));
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(M(0.1));
                    pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "RESERVATION_CANALISATION");
                    AddCalculationNetworks(doc, level, system, pipeType);
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
                    CreateTrainingView(doc, viewType, "BIMaestro - 05 Organisateur",
                        new XYZ(M(-8), M(21), M(-0.5)), new XYZ(M(8), M(29), M(4)));
                    CreateTrainingView(doc, viewType, "BIMaestro - 06 Gabarit source",
                        new XYZ(M(-8), M(10), M(-0.5)), new XYZ(M(8), M(29), M(4)));
                    CreateTrainingView(doc, viewType, "BIMaestro - 07 Gabarit cible",
                        new XYZ(M(-8), M(10), M(-0.5)), new XYZ(M(8), M(29), M(4)));
                    StartingViewSettings.GetStartingViewSettings(doc).ViewId = mainView.Id;

                    string familyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Revit", "Réservation", "CML_Réservation rectangulaire murale.rfa");
                    if (!File.Exists(familyPath))
                        throw new FileNotFoundException("Famille nécessaire à Auto résa introuvable : " + familyPath);
                    if (!doc.LoadFamily(familyPath, out Family family) || family == null)
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
        }

        private static void AddDuctRun(Document doc, Level level)
        {
            MechanicalSystemType system = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault();
            DuctType type = new FilteredElementCollector(doc).OfClass(typeof(DuctType))
                .Cast<DuctType>().FirstOrDefault(candidate => candidate.Shape == ConnectorProfileType.Rectangular);
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
            if (routing.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) > 0) return;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk");
            string familyPath = new[] { doc.Application.VersionNumber, "2023", "2022", "2021" }
                .Distinct()
                .Select(version => Path.Combine(root, "RVT " + version, "Libraries", "English", relativePath))
                .FirstOrDefault(File.Exists);
            if (familyPath == null)
                throw new FileNotFoundException("Famille de coude nécessaire à la scène MEP introuvable : " + relativePath);
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

        private static void AddOrganizerScene(Document doc, Level level)
        {
            string parkingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Revit", "A-Famille Revit", "Voiture-camion", "CML_Parking.rfa");
            if (!File.Exists(parkingPath))
                throw new FileNotFoundException("Famille CML_Parking nécessaire à Organisateur introuvable : " + parkingPath);
            if (!doc.LoadFamily(parkingPath, out Family family) || family == null)
                throw new InvalidOperationException("La famille CML_Parking n'a pas pu être chargée.");
            FamilySymbol symbol = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(candidate => candidate != null);
            if (symbol == null) throw new InvalidOperationException("CML_Parking ne contient aucun type.");
            if (!symbol.IsActive) symbol.Activate();
            doc.Regenerate();
            for (int index = 0; index < 4; index++)
            {
                XYZ position = new XYZ(M(-4.5 + index * 3.0), M(24), 0);
                FamilyInstance parking = doc.Create.NewFamilyInstance(position, symbol, level,
                    StructuralType.NonStructural);
                Parameter number = parking.LookupParameter("CML_Numéros de place");
                if (number == null || number.IsReadOnly || number.StorageType != StorageType.String)
                    throw new InvalidOperationException("CML_Parking doit exposer « CML_Numéros de place » comme paramètre texte d'instance modifiable pour l'exercice Organisateur.");
                number.Set("DEMO-" + (index + 1).ToString("00"));
                parking.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(
                    DemoPrefix + "ORGANISATEUR_" + (index + 1));
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
            bool hasReference = fields.Any(field => !field.IsHidden && field.HasSchedulableField &&
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK);
            bool hasFamilyNumber = fields.Any(field => !field.IsHidden && field.GetName() == "CML_Numéros de place");
            bool hasComments = fields.Any(field => !field.IsHidden && field.HasSchedulableField &&
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            bool commentsAvailable = schedule.Definition.GetSchedulableFields().Any(field =>
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            return hasReference && (!commentsAvailable || hasComments) &&
                (hasFamilyNumber || fields.Count(field => !field.IsHidden) == 2);
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
                    .Set(index <= 2 ? "Secteur A" : "Secteur B");
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
            // Le repère reste visible : il identifie la place et sert de référence fixe
            // pendant l'aller-retour Excel. Le parcours empêche son import si modifié.
            markColumn.IsHidden = false;
            if (familyNumberField != null && commentsField != null && !existingFields.Any(field =>
                field.HasSchedulableField && field.ParameterId.GetIdLongValue() ==
                (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS))
                definition.AddField(commentsField);
            if (familyNumberField == null)
                foreach (FamilyInstance place in FindExcelParking(doc))
                {
                    string mark = place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                    string suffix = mark.Substring((DemoPrefix + "EXCEL_").Length);
                    place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("XL-" + int.Parse(suffix).ToString("D3"));
                }
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
            return (visible.FirstOrDefault(field => field.GetName() == "CML_Numéros de place") ??
                visible.First()).GetName();
        }

        internal static string ExcelReferenceField(ViewSchedule schedule) => schedule.Definition
            .GetFieldOrder().Select(schedule.Definition.GetField)
            .First(field => field.HasSchedulableField &&
                field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_MARK)
            .GetName();

        internal static string ExcelCommentsField(ViewSchedule schedule) =>
            ExcelValueField(schedule) == "CML_Numéros de place"
                ? schedule.Definition.GetFieldOrder().Select(schedule.Definition.GetField)
                    .FirstOrDefault(field => !field.IsHidden && field.HasSchedulableField &&
                        field.ParameterId.GetIdLongValue() == (long)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.GetName()
                : null;

        internal static Parameter ExcelValueParameter(FamilyInstance place, string fieldName) =>
            fieldName == "CML_Numéros de place" ? place.LookupParameter(fieldName) :
            place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

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
            if (views.Count == 0) return;

            using (var tx = new Transaction(doc, "BIMaestro - Affichage des vues de formation"))
            {
                tx.Start();
                foreach (View3D view in views) ConfigureTrainingView(view);
                tx.Commit();
            }
        }

        private static void AddHistoryFurniture(Document doc, Level level)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string[] candidates =
            {
                Path.Combine(documents, "Revit", "CML_Table ronde + chaise.rfa"),
                Path.Combine(documents, "Revit", "A-Famille Revit", "Mobilier", "CML_Table ronde + chaise.rfa")
            };
            string furniturePath = candidates.FirstOrDefault(File.Exists);
            if (furniturePath == null)
                throw new FileNotFoundException("Famille de mobilier nécessaire au parcours historique introuvable.");
            if (!doc.LoadFamily(furniturePath, out Family furniture) || furniture == null)
                throw new InvalidOperationException("La famille de mobilier n'a pas pu être chargée : " + furniturePath);
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
            }
        }
    }
}
