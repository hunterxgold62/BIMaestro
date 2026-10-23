// Runs against native Revit family parameters and production placement/sizing.
// Neither a synthetic family nor a transformed source replaces production code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Modification;

namespace BIMaestro.ReservationTests
{
    internal static class NativeHeightMappingProbe
    {
        private const string LengthName = "Largeur Réservation";
        private const string HeightName = "Hauteur Réservation";
        private const string DepthName = "Longueur Réservation";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static void Run(UIApplication ui, Document fixture, ICollection<string> results)
        {
            var root = Directory.Exists(ui.Application.FamilyTemplatePath) ? ui.Application.FamilyTemplatePath
                : "C:/ProgramData/Autodesk/RVT " + ui.Application.VersionNumber + "/Family Templates";
            string template = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).First(path =>
                Path.GetFileName(path).Equals("Metric Generic Model.rft", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("Modèle générique métrique.rft", StringComparison.OrdinalIgnoreCase));
            Document familyDocument = ui.Application.NewFamilyDocument(template);
            Document linkedSourceDocument = ui.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
                using (var tx = new Transaction(familyDocument, "Unhosted opening parameter fixture"))
                {
                    tx.Start();
                    foreach (string name in new[] { LengthName, HeightName, DepthName })
                        familyDocument.FamilyManager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, true);
                    familyDocument.FamilyManager.NewType("Réservations Rectangulaires - Fixture");
                    familyDocument.FamilyManager.Set(familyDocument.FamilyManager.get_Parameter(LengthName), .25);
                    familyDocument.FamilyManager.Set(familyDocument.FamilyManager.get_Parameter(HeightName), .25);
                    familyDocument.FamilyManager.Set(familyDocument.FamilyManager.get_Parameter(DepthName), .25);
                    tx.Commit();
                }
                Family family = familyDocument.LoadFamily(fixture);
                if (family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                    throw new InvalidOperationException("The height-mapping fixture must use an unhosted family.");
                FamilySymbol symbol = family.GetFamilySymbolIds().Select(fixture.GetElement).OfType<FamilySymbol>().First();
                DirectShape linkedSource;
                using (var tx = new Transaction(linkedSourceDocument, "Tall linked generic source"))
                {
                    tx.Start();
                    linkedSource = CreateTallSource(linkedSourceDocument);
                    tx.Commit();
                }
                Level level = new FilteredElementCollector(fixture).OfClass(typeof(Level)).Cast<Level>().First();
                Wall wall;
                using (var tx = new Transaction(fixture, "Local wall and opening fixture"))
                {
                    tx.Start();
                    wall = Wall.Create(fixture, Line.CreateBound(new XYZ(0, -5, 0), new XYZ(0, 5, 0)), level.Id, false);
                    if (!symbol.IsActive) symbol.Activate();
                    fixture.Regenerate();
                    tx.Commit();
                }
                var sourceTransform = Transform.CreateTranslation(new XYZ(0, 0, .5));
                CheckPairSelectionRoles(fixture, wall, linkedSource);
                results.Add("PASS unhosted pair selection accepts wall-first or source-first and restricts the second host pick");
                var profile = new ProfileConfig
                {
                    FamilyName = family.Name, TypeName = symbol.Name,
                    ParamLength = LengthName, ParamHeight = HeightName, ParamDepth = DepthName,
                    VerticalPlacementMode = VerticalPlacementMode.Center, VerticalPlacementOffsetMm = 100
                };
                var config = new ReservationAutoV3Config { WallRect = profile, DefaultNormeEnabled = false };
                var command = new ReservationAutoV3Command();
                var placement = typeof(ReservationAutoV3Command).GetMethod("CreateReservation_Single", PrivateInstance);
                using (var tx = new Transaction(fixture, "Unhosted family against local wall"))
                {
                    tx.Start();
                    placement.Invoke(command, new object[] { fixture, wall, level, symbol, ReservationAutoV3Window.ObjectType.Autre,
                        linkedSource, sourceTransform, config, profile, true, true, false });
                    fixture.Regenerate();
                    FamilyInstance instance = new FilteredElementCollector(fixture).OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>().Single(item => item.Symbol.Id.Equals(symbol.Id));
                    CheckDimension(instance, HeightName, 3);
                    CheckDimension(instance, LengthName, 1);
                    CheckDimension(instance, DepthName, wall.Width);
                    if (instance.Host != null) throw new InvalidOperationException("An unhosted opening was created through a hosted placement overload.");
                    BoundingBoxXYZ bounds = instance.get_BoundingBox(null);
                    XYZ center = bounds == null ? ((LocationPoint)instance.Location).Point : (bounds.Min + bounds.Max) * .5;
                    if (Math.Abs(center.Z - (2.5 + 100 / 304.8)) > 1e-7)
                        throw new InvalidOperationException("The unhosted opening lost its configured 100 mm vertical offset.");
                    tx.RollBack();
                }
                results.Add("PASS transformed source sizes custom height on an unhosted family at a local wall and preserves the 100 mm vertical offset");
                CheckMissingHeightMapping(fixture, command, symbol, wall, level, linkedSource, sourceTransform, config, profile, null);
                CheckMissingHeightMapping(fixture, command, symbol, wall, level, linkedSource, sourceTransform, config, profile, "Missing height mapping");
                results.Add("PASS missing or incorrect custom height mapping cannot silently keep the family's default height");
                foreach (string mode in new[] { "instance", "readonly", "type" })
                    CheckComHeight(ui, fixture, template, wall, level, linkedSource, sourceTransform, mode);
                results.Add("PASS COM_Hauteur instance is sized; readonly and type-only heights refuse sizing before writing other dimensions");
            }
            finally
            {
                familyDocument.Close(false);
                linkedSourceDocument.Close(false);
            }
        }

        private static void CheckMissingHeightMapping(Document fixture, ReservationAutoV3Command command, FamilySymbol symbol,
            Wall wall, Level level, Element source, Transform transform, ReservationAutoV3Config config, ProfileConfig profile, string heightMapping)
        {
            string savedHeightMapping = profile.ParamHeight;
            using (var tx = new Transaction(fixture, "Invalid opening height mapping"))
            {
                tx.Start();
                try
                {
                    profile.ParamHeight = heightMapping;
                    FamilyInstance instance = fixture.Create.NewFamilyInstance(new XYZ(0, 0, 2.5), symbol, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    bool refused = false;
                    var sizing = typeof(ReservationAutoV3Command).GetMethod("ApplySizing", PrivateInstance);
                    try
                    {
                        sizing.Invoke(command, new object[] { instance, wall,
                            new BoundingBoxXYZ { Min = new XYZ(-.1, -.5, 1), Max = new XYZ(.1, .5, 4) },
                            config, profile, ReservationAutoV3Window.ObjectType.Autre, source, transform, true, false,
                            null, null, false, null, null });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { refused = true; }
                    // Automatic discovery is acceptable only when the real parameter is sized.
                    if (!refused) CheckDimension(instance, HeightName, 3);
                }
                finally { profile.ParamHeight = savedHeightMapping; tx.RollBack(); }
            }
        }

        private static void CheckDimension(FamilyInstance instance, string name, double expected)
        {
            Parameter parameter = instance.LookupParameter(name);
            if (parameter == null || parameter.StorageType != StorageType.Double || Math.Abs(parameter.AsDouble() - expected) > 1e-7)
                throw new InvalidOperationException("Opening parameter '" + name + "' was not sized correctly.");
        }

        private static void CheckPairSelectionRoles(Document document, Wall wall, Element source)
        {
            Type commandType = typeof(ReservationAutoV3Command);
            Type roleType = commandType.GetNestedType("PairSelectionRole", BindingFlags.NonPublic);
            Type filterType = commandType.GetNestedType("UnhostedPairSelectionFilter", BindingFlags.NonPublic);
            MethodInfo isPairHost = commandType.GetMethod("IsPairHost", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var target in new[] { ReservationAutoV3Window.HostTarget.Mur, ReservationAutoV3Window.HostTarget.Sol })
                if ((bool)isPairHost.Invoke(null, new object[] { source, true, target }))
                    throw new InvalidOperationException("Wall-mounted equipment or a console was misclassified as a linked host.");
            foreach (string role in new[] { "Either", "Source", "Host" })
            {
                var filter = (Autodesk.Revit.UI.Selection.ISelectionFilter)Activator.CreateInstance(filterType,
                    new object[] { document, ReservationAutoV3Window.PipeSource.LienRVT, ReservationAutoV3Window.ObjectType.Autre,
                        ReservationAutoV3Window.HostTarget.Mur, Enum.Parse(roleType, role), false });
                if (role != "Source" && !filter.AllowElement(wall))
                    throw new InvalidOperationException("Unhosted selection cannot pick the wall first or after the source.");
                if (filter.AllowElement(source) != (role != "Host"))
                    throw new InvalidOperationException("Unhosted selection failed to distinguish an object from its wall host.");
            }
        }

        private static void CheckComHeight(UIApplication ui, Document fixture, string template, Wall wall, Level level,
            Element source, Transform sourceTransform, string mode)
        {
            Document familyDocument = ui.Application.NewFamilyDocument(template);
            try
            {
                using (var tx = new Transaction(familyDocument, "COM height parameter fixture"))
                {
                    tx.Start();
                    familyDocument.OwnerFamily.Name = "BIMaestro_COM_Height_" + mode;
                    FamilyParameter length = familyDocument.FamilyManager.AddParameter("COM_Longueur", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    FamilyParameter height = familyDocument.FamilyManager.AddParameter("COM_Hauteur", GroupTypeId.Geometry, SpecTypeId.Length, mode != "type");
                    FamilyParameter depth = familyDocument.FamilyManager.AddParameter("COM_Profondeur", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    familyDocument.FamilyManager.NewType("COM parameter fixture");
                    familyDocument.FamilyManager.Set(length, .25);
                    familyDocument.FamilyManager.Set(height, .25);
                    familyDocument.FamilyManager.Set(depth, .25);
                    if (mode == "readonly") familyDocument.FamilyManager.SetFormula(height, "250 mm");
                    tx.Commit();
                }
                Family family = familyDocument.LoadFamily(fixture);
                FamilySymbol symbol = family.GetFamilySymbolIds().Select(fixture.GetElement).OfType<FamilySymbol>().First();
                var profile = new ProfileConfig
                {
                    FamilyName = family.Name, TypeName = symbol.Name,
                    // A null height mapping must still discover COM_Hauteur.
                    ParamLength = "COM_Longueur", ParamHeight = mode == "instance" ? null : "COM_Hauteur", ParamDepth = "COM_Profondeur"
                };
                var config = new ReservationAutoV3Config { WallRect = profile, DefaultNormeEnabled = false };
                using (var tx = new Transaction(fixture, "Verify COM height mapping atomically"))
                {
                    tx.Start();
                    if (!symbol.IsActive) { symbol.Activate(); fixture.Regenerate(); }
                    FamilyInstance instance = fixture.Create.NewFamilyInstance(new XYZ(0, 0, 2.5), symbol, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    bool refused = false;
                    try
                    {
                        typeof(ReservationAutoV3Command).GetMethod("ApplySizing", PrivateInstance).Invoke(new ReservationAutoV3Command(),
                            new object[] { instance, wall, new BoundingBoxXYZ { Min = new XYZ(-.1, -.5, 1), Max = new XYZ(.1, .5, 4) },
                                config, profile, ReservationAutoV3Window.ObjectType.Autre, source, sourceTransform,
                                true, false, null, null, false, null, null });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                    {
                        refused = true;
                        if (ex.InnerException.Message.IndexOf("hauteur", StringComparison.OrdinalIgnoreCase) < 0)
                            throw new InvalidOperationException("Height failure must identify the height dimension.", ex.InnerException);
                    }
                    if (mode == "instance")
                    {
                        if (refused) throw new InvalidOperationException("Writable COM_Hauteur instance parameter was refused.");
                        CheckDimension(instance, "COM_Hauteur", 3);
                        CheckDimension(instance, "COM_Longueur", 1);
                    }
                    else
                    {
                        if (!refused) throw new InvalidOperationException("Readonly/type-only COM_Hauteur silently kept its default height.");
                        CheckDimension(instance, "COM_Longueur", .25);
                        CheckDimension(instance, "COM_Profondeur", .25);
                    }
                    tx.RollBack();
                }
            }
            finally { familyDocument.Close(false); }
        }

        private static DirectShape CreateTallSource(Document document)
        {
            XYZ[] corners = { new XYZ(-3, -.5, .5), new XYZ(3, -.5, .5), new XYZ(3, .5, .5), new XYZ(-3, .5, .5) };
            var loop = new CurveLoop();
            for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, 3);
            DirectShape shape = DirectShape.CreateElement(document, ElementIdExtensions.CreateElementId((int)BuiltInCategory.OST_GenericModel));
            shape.Name = "wall mounted fan console";
            shape.SetShape(new GeometryObject[] { solid });
            return shape;
        }
    }
}
