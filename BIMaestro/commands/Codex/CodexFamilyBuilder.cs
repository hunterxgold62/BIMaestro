using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BIMaestro.Codex
{
    internal sealed class CodexFamilyArtifact
    {
        internal string FilePath, PreviewPath;
        internal object Report;
    }

    internal static class CodexFamilyBuilder
    {
        internal static readonly string OutputRoot = Path.Combine(CodexClient.DataDirectory, "Families");

        // Called exclusively from the bridge's ExternalEvent. The source project is never saved.
        internal static CodexFamilyArtifact Create(UIApplication app, Document source, CodexFamilyDesign design, bool validateOnly = false)
        {
            if (!validateOnly && design.Load && (source == null || source.IsFamilyDocument || source.IsReadOnly || source.IsModifiable))
                throw new InvalidOperationException("Le chargement nécessite un projet actif modifiable, hors d'une autre commande.");
            string template = FindTemplate(app);
            string fileName = SafeName(design.Name);
            var warnings = new List<string>();
            if (app.Application.Documents.Cast<Document>().Any(d => d.Title.Equals(fileName, StringComparison.OrdinalIgnoreCase)) ||
                design.Load && source != null && !source.IsFamilyDocument && new FilteredElementCollector(source).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                fileName += "_v" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                warnings.Add("Une famille homonyme est déjà ouverte ou chargée. Nouvelle version distincte : " + fileName + ". Les anciennes instances ne sont pas remplacées.");
            }
            string folder = Path.Combine(OutputRoot, fileName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Document family = null;
            string stage = "création du document temporaire";
            try
            {
                family = app.Application.NewFamilyDocument(template);
                if (family == null) throw new InvalidOperationException("Revit n'a pas créé le document de famille.");
                var bounds = new Bounds();
                var createdElements = new List<FreeFormElement>();
                View3D preview;
                using (var transaction = new Transaction(family, "BIMaestro — famille depuis description"))
                {
                    transaction.Start();
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                    family.OwnerFamily.FamilyCategory = Category.GetCategory(family, CategoryId(design.Category));
                    var manager = family.FamilyManager;
                    if (manager.CurrentType == null) manager.NewType("Standard");
                    var materials = new Dictionary<string, FamilyParameter>();
                    foreach (var spec in design.Materials)
                    {
                        stage = "matériau « " + spec.Name + " »";
                        var material = (Material)family.GetElement(Material.Create(family, "BIMaestro " + SafeName(spec.Name)));
                        material.Color = new Color(spec.Rgb[0], spec.Rgb[1], spec.Rgb[2]); material.Transparency = spec.Transparency;
                        var parameter = manager.AddParameter("Matériau — " + SafeName(spec.Name), GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        manager.Set(parameter, material.Id); materials[spec.Name] = parameter;
                    }
                    foreach (var part in design.Parts)
                    {
                        stage = "pièce « " + part.Name + " » (" + part.Geometry.Kind + ")";
                        Solid baseSolid = null;
                        try
                        {
                            var subcategory = family.Settings.Categories.NewSubcategory(family.OwnerFamily.FamilyCategory, SafeName(part.Name));
                            baseSolid = MakeSolid(part.Geometry);
                            foreach (var cutSpec in part.Cuts)
                                using (var cut = MakeSolid(cutSpec))
                                {
                                    double before = baseSolid.Volume;
                                    var difference = BooleanOperationsUtils.ExecuteBooleanOperation(baseSolid, cut, BooleanOperationsType.Difference);
                                    baseSolid.Dispose(); baseSolid = difference;
                                    if (Math.Abs(before - baseSolid.Volume) < 1e-10) warnings.Add("Évidement sans intersection : " + part.Name);
                                }
                            if (baseSolid.Faces.Size == 0 || baseSolid.Volume <= 1e-12) throw new InvalidOperationException("Pièce vide après évidement : " + part.Name);
                            for (int i = 0; i < part.Count; i++)
                                using (var solid = SolidUtils.CreateTransformed(baseSolid, Transform.CreateTranslation(Point(part.Step).Multiply(i))))
                                {
                                    var element = FreeFormElement.Create(family, solid);
                                    createdElements.Add(element);
                                    element.Subcategory = subcategory;
                                    var material = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                                    if (material == null || material.IsReadOnly) throw new InvalidOperationException("Revit ne permet pas d'affecter un matériau à " + part.Name);
                                    manager.AssociateElementParameterToFamilyParameter(material, materials[part.Material]);
                                    var comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                    if (comments != null && !comments.IsReadOnly) comments.Set(part.Name + (part.Count > 1 ? " " + (i + 1) : ""));
                                }
                        }
                        catch (Exception ex) { throw new InvalidOperationException("Pièce « " + part.Name + " » : " + ex.Message, ex); }
                        finally { baseSolid?.Dispose(); }
                    }
                    // Calculated reference dimensions, explicitly not driving parameters.
                    stage = "contrôle des encombrements";
                    family.Regenerate();
                    // Measure the actual model-aligned elements. Transforming the eight corners
                    // of a rotated solid's local box can overestimate a cylinder or sphere.
                    foreach (var element in createdElements)
                    {
                        var box = element.get_BoundingBox(null);
                        if (box == null) throw new InvalidOperationException("Encombrement introuvable pour la pièce " + element.Id);
                        bounds.Include(box);
                    }
                    design.CheckDimensions(new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) });
                    AddReferenceDimension(manager, "Encombrement X (calculé)", bounds.Max.X - bounds.Min.X);
                    AddReferenceDimension(manager, "Encombrement Y (calculé)", bounds.Max.Y - bounds.Min.Y);
                    AddReferenceDimension(manager, "Encombrement Z (calculé)", bounds.Max.Z - bounds.Min.Z);
                    stage = "aperçu et validation de la transaction";
                    preview = CreatePreview(family, bounds);
                    family.Regenerate();
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Famille annulée par Revit : " + string.Join(" ; ", warnings.Take(5)));
                }
                if (validateOnly) return new CodexFamilyArtifact { Report = new {
                    validated = true, saved = false, loaded = false, solidCount = design.SolidCount,
                    dimensions_mm = new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) },
                    warnings = warnings.ToArray(), next = "Géométrie validée. Appeler revit_create_family avec la même description pour enregistrer." } };
                // No partial RFA is saved when any geometry creation failed.
                stage = "enregistrement du nouveau RFA";
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, fileName + ".rfa");
                var options = new SaveAsOptions { OverwriteExistingFile = false, Compact = true, MaximumBackups = 1, PreviewViewId = preview.Id };
                family.SaveAs(path, options);
                string previewPath = null;
                try
                {
                    var export = new ImageExportOptions { ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "apercu"),
                        HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage, PixelSize = 1200, FitDirection = FitDirectionType.Horizontal, ImageResolution = ImageResolution.DPI_150 };
                    export.SetViewsAndSheets(new List<ElementId> { preview.Id }); family.ExportImage(export);
                    previewPath = Directory.EnumerateFiles(folder, "apercu*.png").FirstOrDefault();
                    if (previewPath == null) warnings.Add("Aucun aperçu PNG renvoyé par Revit.");
                }
                catch (Exception ex) { warnings.Add("RFA enregistré, aperçu non disponible : " + ex.Message); }

                string loadedId = null, placedId = null;
                if (design.Load)
                {
                    try
                    {
                        using (var group = new TransactionGroup(source, "BIMaestro — charger la famille créée"))
                        {
                            group.Start();
                            // Unique filename per output folder is not enough: family names can collide.
                            // Refuse an existing family rather than silently overwrite users' types.
                            if (new FilteredElementCollector(source).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Une famille portant ce nom est déjà chargée. Le nouveau RFA est conservé séparément ; choisissez un autre nom pour le charger.");
                            var loaded = family.LoadFamily(source);
                            if (loaded == null) throw new InvalidOperationException("Revit n'a pas chargé la famille.");
                            loadedId = loaded.Id.ToString();
                            if (design.Place)
                            {
                                var symbol = loaded.GetFamilySymbolIds().Select(id => source.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault();
                                var level = new FilteredElementCollector(source).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation)).FirstOrDefault();
                                if (symbol == null || level == null) throw new InvalidOperationException("Type de famille ou niveau introuvable pour le placement.");
                                using (var t = new Transaction(source, "Placer la famille à l'origine"))
                                {
                                    t.Start();
                                    t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                                    if (!symbol.IsActive) { symbol.Activate(); source.Regenerate(); }
                                    var instance = source.Create.NewFamilyInstance(new XYZ(0, 0, level.Elevation), symbol, level, StructuralType.NonStructural);
                                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Placement annulé par Revit.");
                                    placedId = instance.Id.ToString();
                                }
                            }
                            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Chargement annulé.");
                        }
                    }
                    catch (Exception ex) { loadedId = placedId = null; warnings.Add("RFA créé mais non chargé : " + ex.Message); }
                }
                var report = new
                {
                    file = path, category = design.Category, solidCount = design.SolidCount, materialCount = design.Materials.Count,
                    dimensions_mm = new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) },
                    requested_dimensions_mm = design.TargetDimensions,
                    geometry = "Solides Revit analytiques, géométrie fixe ; paramètres de matériaux modifiables. Encombrements calculés non pilotants.",
                    assumptions = design.Assumptions, warnings = warnings.Distinct().Take(40).ToArray(), loadedFamilyId = loadedId, placedInstanceId = placedId,
                    undo = "Ctrl+Z annule le chargement/placement dans le projet ; le fichier RFA reste sur disque."
                };
                try
                {
                    File.WriteAllText(Path.Combine(folder, "construction.json"), design.Source.ToString(Formatting.Indented), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(folder, "rapport.json"), JsonConvert.SerializeObject(report, Formatting.Indented), new UTF8Encoding(false));
                }
                catch (IOException) { /* The RFA and in-memory report remain usable. */ }
                return new CodexFamilyArtifact { FilePath = path, PreviewPath = previewPath, Report = report };
            }
            catch (Exception ex) { throw new InvalidOperationException("Étape " + stage + " : " + ex.Message, ex); }
            finally { if (family != null && family.IsValidObject) family.Close(false); }
        }

        private static string FindTemplate(UIApplication app)
        {
            var roots = new[] { app.Application.FamilyTemplatePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.Application.VersionNumber, "Family Templates") };
            foreach (string root in roots.Where(Directory.Exists).Distinct())
            {
                string found = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).FirstOrDefault(p =>
                    new[] { "Metric Generic Model", "Modèle générique métrique" }.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase));
                if (found != null) return found;
            }
            throw new InvalidOperationException("Gabarit Modèle générique métrique introuvable. Installez les gabarits Revit de cette version ou réglez le chemin des gabarits dans Revit.");
        }
        private static BuiltInCategory CategoryId(string category)
        {
            switch (category)
            {
                case "electrical": return BuiltInCategory.OST_ElectricalEquipment;
                case "mechanical": return BuiltInCategory.OST_MechanicalEquipment;
                case "furniture": return BuiltInCategory.OST_Furniture;
                case "plumbing": return BuiltInCategory.OST_PlumbingFixtures;
                default: return BuiltInCategory.OST_GenericModel;
            }
        }
        private static string SafeName(string name)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '{', '}', '[', ']', ';', '<', '>', '?', '`', '~' }).ToHashSet();
            string result = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (result.Length == 0) result = "Famille";
            // Prefix also avoids reserved DOS device names.
            return "BIM_" + result.Substring(0, Math.Min(result.Length, 80));
        }
        private static void AddReferenceDimension(FamilyManager manager, string name, double value)
        {
            var parameter = manager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            manager.SetFormula(parameter, Mm(value).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " mm");
        }
        private static View3D CreatePreview(Document doc, Bounds bounds)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            var view = View3D.CreateIsometric(doc, type.Id); view.Name = "BIMaestro - Aperçu";
            view.DisplayStyle = DisplayStyle.Shading; view.DetailLevel = ViewDetailLevel.Fine;
            var center = (bounds.Min + bounds.Max) * 0.5;
            double size = bounds.Max.DistanceTo(bounds.Min);
            var forward = new XYZ(-1, 1, -0.65).Normalize();
            var right = forward.CrossProduct(XYZ.BasisZ).Normalize(); var up = right.CrossProduct(forward).Normalize();
            view.SetOrientation(new ViewOrientation3D(center - forward * (size * 2 + 1), up, forward));
            var padding = new XYZ(size * 0.05 + 0.01, size * 0.05 + 0.01, size * 0.05 + 0.01);
            view.SetSectionBox(new BoundingBoxXYZ { Min = bounds.Min - padding, Max = bounds.Max + padding }); view.IsSectionBoxActive = true;
            return view;
        }
        private static double Feet(double mm) => mm / 304.8;
        private static double Mm(double feet) => Math.Round(feet * 304.8, 3);
        private static XYZ Point(double[] mm) => new XYZ(Feet(mm[0]), Feet(mm[1]), Feet(mm[2]));
        private static CurveLoop Polygon(IEnumerable<XYZ> points)
        {
            var p = points.ToList(); var loop = new CurveLoop();
            for (int i = 0; i < p.Count; i++) loop.Append(Line.CreateBound(p[i], p[(i + 1) % p.Count]));
            return loop;
        }
        private static CurveLoop Circle(double radius)
        {
            var loop = new CurveLoop();
            loop.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return loop;
        }
        private static Solid MakeSolid(FamilyPrimitive spec)
        {
            double a = Feet(spec.Size[0]), b = Feet(spec.Size[1]), h = Feet(spec.Size[2]); Solid raw;
            if (spec.Kind == "sphere")
            {
                var loop = new CurveLoop();
                loop.Append(Arc.Create(new XYZ(0, 0, -a), new XYZ(0, 0, a), new XYZ(a, 0, 0)));
                loop.Append(Line.CreateBound(new XYZ(0, 0, a), new XYZ(0, 0, -a)));
                raw = GeometryCreationUtilities.CreateRevolvedGeometry(new Frame(XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ), new[] { loop }, 0, 2 * Math.PI);
            }
            else if (spec.Kind == "cone" || spec.Kind == "lathe")
            {
                var points = spec.Kind == "lathe" ? spec.Profile.Select(p => new XYZ(Feet(p[0]), 0, Feet(p[1]))).ToList()
                    : new[] { XYZ.Zero, new XYZ(a, 0, 0), new XYZ(b, 0, h), new XYZ(0, 0, h) }.Distinct(new PointComparer()).ToList();
                raw = GeometryCreationUtilities.CreateRevolvedGeometry(new Frame(XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ), new[] { Polygon(points) }, 0, 2 * Math.PI);
            }
            else
            {
                IList<CurveLoop> loops;
                if (spec.Kind == "box") loops = new[] { Polygon(new[] { XYZ.Zero, new XYZ(a, 0, 0), new XYZ(a, b, 0), new XYZ(0, b, 0) }) };
                else if (spec.Kind == "extrusion") loops = new[] { Polygon(spec.Profile.Select(p => new XYZ(Feet(p[0]), Feet(p[1]), 0))) };
                else if (spec.Kind == "tube") loops = new[] { Circle(a), Circle(b) };
                else loops = new[] { Circle(a) };
                raw = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, h);
            }
            using (raw)
            {
                var transform = Transform.CreateTranslation(Point(spec.Position));
                // Multiplication order: Rx first, then Ry, then Rz, then translation.
                transform = transform.Multiply(Transform.CreateRotation(XYZ.BasisZ, spec.Rotation[2] * Math.PI / 180))
                    .Multiply(Transform.CreateRotation(XYZ.BasisY, spec.Rotation[1] * Math.PI / 180))
                    .Multiply(Transform.CreateRotation(XYZ.BasisX, spec.Rotation[0] * Math.PI / 180));
                return SolidUtils.CreateTransformed(raw, transform);
            }
        }
        private sealed class PointComparer : IEqualityComparer<XYZ>
        {
            public bool Equals(XYZ a, XYZ b) => a.IsAlmostEqualTo(b);
            public int GetHashCode(XYZ obj) => 0;
        }
        private sealed class Bounds
        {
            internal XYZ Min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue), Max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
            internal void Include(BoundingBoxXYZ box)
            {
                for (int i = 0; i < 8; i++)
                {
                    var p = box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z));
                    Min = new XYZ(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
                    Max = new XYZ(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
                }
                if (Math.Max(Min.GetLength(), Max.GetLength()) > Feet(300000)) throw new InvalidOperationException("Géométrie trop éloignée de l'origine.");
            }
        }
        private sealed class Failures : IFailuresPreprocessor
        {
            private readonly List<string> warnings;
            internal Failures(List<string> warnings) { this.warnings = warnings; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                foreach (var failure in accessor.GetFailureMessages())
                {
                    warnings.Add(failure.GetDescriptionText());
                    if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure); else error = true;
                }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
