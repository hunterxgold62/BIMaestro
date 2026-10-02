using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Licensing;
using System;
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
                TaskDialog.Show("BIMaestro - Formation", "Maquette de formation créée :\n" + path +
                    "\n\nOuvre le bouton « Parcours guidés » pour choisir un exercice.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIMaestro - Formation", "Création de la maquette impossible : " + ex.Message);
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
                        throw new InvalidOperationException("Le gabarit ne contient pas de canalisation MEP. Choisis un gabarit MEP.");
                    Pipe pipe = Pipe.Create(doc, system.Id, pipeType.Id, level.Id,
                        new XYZ(0, M(-2), M(1.5)), new XYZ(0, M(2), M(1.5)));
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(M(0.1));
                    pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoPrefix + "RESERVATION_CANALISATION");

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
            if (saved) uiApp.OpenAndActivateDocument(path);
            return path;
        }

        internal static void ConfigureTrainingView(View3D view)
        {
            view.DetailLevel = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.FlatColors;
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
                .Where(view => DemoTourCatalog.Views.Values.Contains(view.Name))
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
