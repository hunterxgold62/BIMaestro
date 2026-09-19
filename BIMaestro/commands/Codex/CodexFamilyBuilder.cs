using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        internal string[] PreviewPaths;
        internal object Report;
    }

    internal static class CodexFamilyBuilder
    {
        internal static readonly string OutputRoot = Path.Combine(CodexClient.DataDirectory, "Families");

        // Called inside a transaction, after all generated types have been created.
        internal static void ApplyBranding(FamilyManager manager)
        {
            var manufacturer = manager.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER);
            var url = manager.get_Parameter(BuiltInParameter.ALL_MODEL_URL);
            if (manufacturer == null || url == null)
                throw new InvalidOperationException("Le gabarit ne contient pas les paramètres Fabricant et URL.");
            if (manager.CurrentType == null) manager.NewType("Standard");
            manager.SetFormula(manufacturer, "\"Plugin - BIMaestro\"");
            if (url.IsDeterminedByFormula) manager.SetFormula(url, null);
            var initialType = manager.CurrentType;
            try
            {
                foreach (FamilyType type in manager.Types)
                {
                    manager.CurrentType = type;
                    manager.Set(url, "https://www.bimaestro.fr");
                }
            }
            finally { manager.CurrentType = initialType; }
        }

        // Called exclusively from the bridge's ExternalEvent. The source project is never saved.
        internal static CodexFamilyArtifact Create(UIApplication app, Document source, CodexFamilyDesign design, bool validateOnly = false, CodexParametricDesign parametric = null, bool testHostPlacement = false, Action<Document> inspect = null)
        {
            return CreateSteps(app, source, design, validateOnly, parametric, testHostPlacement, inspect).Last(x => x != null);
        }
        internal static IEnumerable<CodexFamilyArtifact> CreateSteps(UIApplication app, Document source, CodexFamilyDesign design, bool validateOnly = false, CodexParametricDesign parametric = null, bool testHostPlacement = false, Action<Document> inspect = null)
        {
            using var guard = new CodexCreationGuard(app.Application, design.Name);
            if (!validateOnly && design.Load && (source == null || source.IsFamilyDocument || source.IsReadOnly || source.IsModifiable))
                throw new InvalidOperationException("Le chargement nécessite un projet actif modifiable, hors d'une autre commande.");
            string template = FindTemplate(app, parametric?.Hosting ?? design.Hosting);
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
                CodexCreationGuard.Check("création du document temporaire");
                if (family == null) throw new InvalidOperationException("Revit n'a pas créé le document de famille.");
                guard.Pause(); yield return null; guard.Resume();
                stage = "préparation des barres imbriquées";
                var prototypes = new Dictionary<string, FamilySymbol>();
                if (parametric != null)
                    foreach (var spec in parametric.Arrays)
                    {
                        CodexCreationGuard.Check("préparation du réseau « " + spec.Name + " »");
                        foreach (var pair in CodexParametricArrayBuilder.Prepare(family, FindTemplate(app, "free"), parametric, new[] { spec })) prototypes.Add(pair.Key, pair.Value);
                        guard.Pause(); yield return null; guard.Resume();
                    }
                guard.Pause(); yield return null; guard.Resume();
                var bounds = new Bounds();
                var createdElements = new List<Element>();
                CodexParametricBuilder parametricBuilder = null;
                CodexHostOpeningBuilder hostOpening = null;
                View3D preview;
                var previewViews = new List<View3D>();
                using (var transaction = new Transaction(family, "BIMaestro — famille depuis description"))
                {
                    transaction.Start();
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                    family.OwnerFamily.FamilyCategory = Category.GetCategory(family, CategoryId(design.Category));
                    if ((parametric?.Hosting ?? design.Hosting) == "work_plane")
                    {
                        var hosted = family.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                        if (hosted == null || hosted.IsReadOnly) throw new InvalidOperationException("Le gabarit ne permet pas un hébergement par plan de travail.");
                        hosted.Set(1);
                    }
                    var manager = family.FamilyManager;
                    if (manager.CurrentType == null) manager.NewType("Standard");
                    var materials = new Dictionary<string, FamilyParameter>();
                    foreach (var spec in design.Materials)
                    {
                        stage = "matériau « " + spec.Name + " »";
                        string materialName = "BIMaestro " + SafeName(spec.Name);
                        var material = new FilteredElementCollector(family).OfClass(typeof(Material)).Cast<Material>()
                            .FirstOrDefault(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase))
                            ?? (Material)family.GetElement(Material.Create(family, materialName));
                        material.Color = new Color(spec.Rgb[0], spec.Rgb[1], spec.Rgb[2]); material.Transparency = spec.Transparency;
                        var parameter = CodexParameterBuilder.NewInternal(manager, "Matériau — " + SafeName(spec.Name), GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        manager.Set(parameter, material.Id); materials[spec.Name] = parameter;
                    }
                    if (parametric != null)
                    {
                        stage = "construction des extrusions et contraintes paramétriques";
                        parametricBuilder = new CodexParametricBuilder(family, parametric);
                        foreach (var buildStep in parametricBuilder.BuildSteps(materials, prototypes))
                        {
                            buildStep();
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Étape de construction annulée : " + string.Join(" ; ", warnings.Take(5)));
                            guard.Pause(); yield return null; guard.Resume();
                            transaction.Start();
                            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                        }
                        createdElements.AddRange(parametricBuilder.CreatedElements());
                    }
                    else foreach (var part in design.Parts)
                    {
                        CodexCreationGuard.Check("construction de « " + part.Name + " »");
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
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Pièce annulée : " + part.Name);
                        guard.Pause(); yield return null; guard.Resume();
                        transaction.Start();
                        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                    }
                    stage = "découpe de l'hôte";
                    hostOpening = parametricBuilder?.HostOpening;
                    if (parametric == null && design.HostOpening != null)
                        hostOpening = new CodexHostOpeningBuilder(family, design.HostOpening, new Dictionary<string, double>());
                    // Calculated reference dimensions, explicitly not driving parameters.
                    stage = "contrôle des encombrements";
                    family.Regenerate();
                    // Measure the actual model-aligned elements. Transforming the eight corners
                    // of a rotated solid's local box can overestimate a cylinder or sphere.
                    foreach (var element in createdElements)
                    {
                        if (parametric != null && element is FamilyInstance && element.LookupParameter("BIM_Visible")?.AsInteger() == 0) continue;
                        var box = parametric != null && element is FamilyInstance ? CodexParametricArrayBuilder.SolidBounds(element) : element.get_BoundingBox(null);
                        if (box == null) throw new InvalidOperationException("Encombrement introuvable pour la pièce " + element.Id);
                        bounds.Include(box);
                    }
                    if (parametric != null && bounds.Min.X == double.MaxValue)
                    { bounds.Min = XYZ.Zero; bounds.Max = XYZ.Zero; warnings.Add("Aucune barre visible dans le type initial ; encombrement visible nul."); }
                    design.CheckDimensions(new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) });
                    if (parametric == null)
                    {
                        AddReferenceDimension(manager, "Encombrement X (calculé)", bounds.Max.X - bounds.Min.X);
                        AddReferenceDimension(manager, "Encombrement Y (calculé)", bounds.Max.Y - bounds.Min.Y);
                        AddReferenceDimension(manager, "Encombrement Z (calculé)", bounds.Max.Z - bounds.Min.Z);
                    }
                    stage = "aperçu et validation de la transaction";
                    preview = CreatePreview(family, bounds);
                    previewViews.Add(preview);
                    if (!validateOnly)
                    {
                        previewViews.Add(CreatePreview(family, bounds, "BIMaestro - Dessus", -XYZ.BasisZ));
                        previewViews.Add(CreatePreview(family, bounds, "BIMaestro - Dessous", XYZ.BasisZ));
                    }
                    family.Regenerate();
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Famille annulée par Revit : " + string.Join(" ; ", warnings.Take(5)));
                }
                stage = "tests de variation des paramètres";
                CodexCreationGuard.Check(stage);
                guard.Pause(); yield return null; guard.Resume();
                var flexReports = new List<object>();
                if (parametricBuilder != null)
                    foreach (var flexReport in parametricBuilder.FlexSteps())
                    {
                        if (flexReport != null) flexReports.Add(flexReport);
                        guard.Pause(); yield return null; guard.Resume();
                    }
                object[] flexTests = flexReports.ToArray();
                hostOpening?.Check(parametric?.Initial ?? new Dictionary<string, double>());
                object representationReport = null;
                if (design.Representation != null)
                {
                    stage = "représentation 2D et visibilité du modèle";
                    var regionSymbols = CodexRepresentationBuilder.PrepareRegions(family, design.Representation);
                    using (var t = new Transaction(family, "BIMaestro — représentation 2D"))
                    {
                        t.Start();
                        t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
                        representationReport = CodexRepresentationBuilder.Build(family, design.Representation, createdElements, regionSymbols);
                        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Représentation 2D annulée : " + string.Join(" ; ", warnings));
                    }
                }
                object hostPlacementTest = testHostPlacement && hostOpening != null ? hostOpening.VerifyProjectPlacement(parametric?.TestCases() ?? new[] { new Dictionary<string, double>() }) : null;
                guard.Pause(); yield return null; guard.Resume();
                stage = "identification BIMaestro";
                using (var transaction = new Transaction(family, "BIMaestro — identification de la famille"))
                {
                    transaction.Start();
                    ApplyBranding(family.FamilyManager);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Identification BIMaestro non enregistrée.");
                }
                inspect?.Invoke(family);
                CodexCreationGuard.Check("validation terminée");
                if (validateOnly) { guard.Complete(); yield return new CodexFamilyArtifact { Report = new {
                    validated = true, saved = false, loaded = false, solidCount = parametric?.SolidCount ?? design.SolidCount,
                    revit_version = app.Application.VersionNumber, flex_tests = flexTests, host_opening = hostOpening?.Report(), host_placement_test = hostPlacementTest, representation_2d = representationReport,
                    parameters = parametric?.Registry == null ? null : CodexFamilyTools.Read(family), connectors = parametricBuilder?.ConnectorReports(),
                    dimensions_mm = new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) },
                    warnings = warnings.ToArray(), next = "Validation native terminée pour les cas demandés. Appeler " + (parametric == null ? "revit_create_family" : "revit_create_parametric_family") + " avec la même description pour enregistrer." } }; yield break; }
                guard.Pause(); yield return null; guard.Resume();
                // No partial RFA is saved when any geometry creation failed.
                stage = "enregistrement du nouveau RFA";
                CodexCreationGuard.Check(stage);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, fileName + ".rfa");
                var options = new SaveAsOptions { OverwriteExistingFile = false, Compact = true, MaximumBackups = 1, PreviewViewId = preview.Id };
                family.SaveAs(path, options);
                string previewPath = null;
                string[] previewPaths = new string[0];
                try
                {
                    CodexCreationGuard.Check("export des aperçus");
                    var export = new ImageExportOptions { ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "apercu"),
                        HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage, PixelSize = 1200, FitDirection = FitDirectionType.Horizontal, ImageResolution = ImageResolution.DPI_150 };
                    export.SetViewsAndSheets(previewViews.Select(v => v.Id).ToList()); family.ExportImage(export);
                    previewPaths = Directory.EnumerateFiles(folder, "apercu*.png").OrderBy(p => p).Take(3).ToArray();
                    previewPath = previewPaths.FirstOrDefault();
                    if (previewPath == null) warnings.Add("Aucun aperçu PNG renvoyé par Revit.");
                }
                catch (Exception ex) { warnings.Add("RFA enregistré, aperçu non disponible : " + ex.Message); }

                string loadedId = null, placedId = null;
                object loadFailure = null;
                if (design.Load)
                {
                    try
                    {
                        CodexCreationGuard.Check("chargement dans le projet");
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
                    catch (Exception ex)
                    {
                        loadedId = placedId = null;
                        ex.Data["operation"] = "load_saved_family";
                        ex.Data["file"] = path;
                        ex.Data["target_document"] = source?.IsValidObject == true ? source.Title : "document fermé";
                        ex.Data["revit_version"] = app.Application.VersionNumber;
                        ex.Data["journal"] = app.Application.RecordingJournalFilename;
                        string diagnostic = CodexDiagnostics.RecordFailure("revit_load_created_family", new JObject { ["file"] = path }, ex);
                        loadFailure = new { message = ex.Message, exception_type = ex.GetType().FullName, diagnostic };
                        warnings.Add("RFA enregistré mais non chargé : " + ex.Message + (diagnostic == null ? "" : ". Diagnostic : " + diagnostic));
                    }
                }
                var report = new
                {
                    file = path, load_failure = loadFailure, category = design.Category, solidCount = parametric?.SolidCount ?? design.SolidCount, materialCount = design.Materials.Count,
                    dimensions_mm = new[] { Mm(bounds.Max.X - bounds.Min.X), Mm(bounds.Max.Y - bounds.Min.Y), Mm(bounds.Max.Z - bounds.Min.Z) },
                    requested_dimensions_mm = design.TargetDimensions,
                    geometry = parametric == null ? "Solides Revit à géométrie fixe ; matériaux paramétrés. Encombrements calculés non pilotants." : "Extrusions natives contraintes et réseaux de barres imbriquées. Dimensions, nombres et inclinaisons des barres pilotés dans Revit sans Codex. Pas de connecteurs MEP.",
                    driving_parameters = parametric?.Parameters.Select(p => new { name = p.Name, value_mm = p.Value }).ToArray(),
                    driving_angles = parametric?.Angles.Select(p => new { name = p.Name, value_deg = p.Value }).ToArray(),
                    arrays = parametricBuilder?.ArrayReports(parametric.Initial),
                    internal_calculated_lengths = parametricBuilder?.InternalLengthCount,
                    parameters = parametric?.Registry == null ? null : CodexFamilyTools.Read(family),
                    connectors = parametricBuilder?.ConnectorReports(),
                    hosting = parametric?.Hosting ?? design.Hosting,
                    host_opening = hostOpening?.Report(),
                    representation_2d = representationReport,
                    flex_tests = flexTests,
                    assumptions = design.Assumptions, warnings = warnings.Distinct().Take(40).ToArray(), loadedFamilyId = loadedId, placedInstanceId = placedId,
                    undo = "Ctrl+Z annule le chargement/placement dans le projet ; le fichier RFA reste sur disque."
                };
                try
                {
                    File.WriteAllText(Path.Combine(folder, "construction.json"), design.Source.ToString(Formatting.Indented), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(folder, "rapport.json"), JsonConvert.SerializeObject(report, Formatting.Indented), new UTF8Encoding(false));
                }
                catch (IOException) { /* The RFA and in-memory report remain usable. */ }
                guard.Complete();
                yield return new CodexFamilyArtifact { FilePath = path, PreviewPath = previewPath, PreviewPaths = previewPaths, Report = report };
            }
            finally
            {
                // ExportImage can make this document active. Cleanup must not
                // mask a successful save or the original construction error.
                if (family != null && family.IsValidObject && app.ActiveUIDocument?.Document != family)
                {
                    try { family.Close(false); }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                }
            }
        }

        internal static object TemplateInfo(UIApplication app, string hosting)
        {
            if (!new[] { "free", "wall", "floor", "ceiling", "face", "work_plane" }.Contains(hosting)) throw new InvalidOperationException("Hébergement de gabarit inconnu.");
            string path = FindTemplate(app, hosting);
            Document temporary = null;
            try
            {
                temporary = app.Application.NewFamilyDocument(path);
                var walls = new FilteredElementCollector(temporary).OfClass(typeof(Wall)).Cast<Wall>().Select(w => {
                    var bounds=w.get_BoundingBox(null);
                    var direction=((w.Location as LocationCurve)?.Curve as Line)?.Direction;
                    return new { id=w.Id.ToString(), width_mm=w.Width*304.8,
                        parallel_to_x=direction!=null && Math.Abs(direction.DotProduct(XYZ.BasisX))>0.999999,
                        minimum_y_mm=bounds.Min.Y*304.8, maximum_y_mm=bounds.Max.Y*304.8,
                        orientation=new[]{w.Orientation.X,w.Orientation.Y,w.Orientation.Z} };
                }).ToArray();
                var floors = new FilteredElementCollector(temporary).OfClass(typeof(Floor)).Cast<Floor>().Select(f => {
                    var bounds=f.get_BoundingBox(null);
                    return new { minimum_z_mm=bounds.Min.Z*304.8, maximum_z_mm=bounds.Max.Z*304.8 };
                }).ToArray();
                return new { hosting, template=path, walls, floors,
                    template_parameters=temporary.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p=>new { name=p.Definition.Name, instance=p.IsInstance, built_in=p.Id.IntegerValue<0, reporting=p.IsReporting, read_only=p.IsReadOnly }).ToArray(),
                    note="Coordonnées réelles du gabarit, en mm. Pour un mur parallèle à X : une applique côté +Y commence à maximum_y_mm ; côté -Y elle se termine à minimum_y_mm. Ne pas supposer 150 mm ou un centrage sur Y=0. Ces valeurs seules ne créent pas de liaison aux faces d'un autre mur plus épais : préférer hosting=face pour une applique qui doit suivre sa face hôte." };
            }
            finally { if(temporary!=null&&temporary.IsValidObject)temporary.Close(false); }
        }

        private static string FindTemplate(UIApplication app, string hosting)
        {
            var roots = new[] { app.Application.FamilyTemplatePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.Application.VersionNumber, "Family Templates") };
            foreach (string root in roots.Where(Directory.Exists).Distinct())
            {
                string[] names = hosting == "face" ? new[] { "Metric Generic Model face based", "Modèle générique métrique (face)", "Metric_Generic_Model-Face_Based-FRA", "Metric_Generic_Model-Face_Based-ENU" } :
                    hosting == "wall" ? new[] { "Metric Generic Model wall based", "Modèle générique métrique (mur)" } :
                    hosting == "floor" ? new[] { "Metric Generic Model floor based", "Modèle générique métrique (sol)", "Metric_Generic_Model-Floor_Based-FRA", "Metric_Generic_Model-Floor_Based-ENU" } :
                    hosting == "ceiling" ? new[] { "Metric Generic Model ceiling based", "Modèle générique métrique (plafond)" } :
                    new[] { "Modèle générique métrique", "Metric Generic Model", "Metric_Generic_Model-FRA", "Metric_Generic_Model-ENU" };
                string found = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                    .Where(p => names.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => Array.FindIndex(names, name => name.Equals(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
                if (found != null) return found;
            }
            throw new InvalidOperationException("Gabarit métrique pour hébergement « " + hosting + " » introuvable. Installez les gabarits de cette version ou réglez leur chemin dans Revit.");
        }
        internal static BuiltInCategory CategoryId(string category)
        {
            switch (category)
            {
                case "planting": return BuiltInCategory.OST_Planting;
                case "door": return BuiltInCategory.OST_Doors;
                case "window": return BuiltInCategory.OST_Windows;
                case "electrical": return BuiltInCategory.OST_ElectricalEquipment;
                case "mechanical": return BuiltInCategory.OST_MechanicalEquipment;
                case "air_terminal": return BuiltInCategory.OST_DuctTerminal;
                case "lighting": return BuiltInCategory.OST_LightingFixtures;
                case "furniture": return BuiltInCategory.OST_Furniture;
                case "plumbing": return BuiltInCategory.OST_PlumbingFixtures;
                case "pipe_accessory": return BuiltInCategory.OST_PipeAccessory;
                case "pipe_fitting": return BuiltInCategory.OST_PipeFitting;
                case "duct_accessory": return BuiltInCategory.OST_DuctAccessory;
                case "duct_fitting": return BuiltInCategory.OST_DuctFitting;
                case "generic": return BuiltInCategory.OST_GenericModel;
                default: throw new InvalidOperationException("Catégorie de famille inconnue : " + category);
            }
        }
        internal static string SafeName(string name)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '{', '}', '[', ']', ';', '<', '>', '?', '`', '~' }).ToHashSet();
            string result = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (result.Length == 0) result = "Famille";
            // Prefix also avoids reserved DOS device names.
            return "BIM_" + result.Substring(0, Math.Min(result.Length, 80));
        }
        private static void AddReferenceDimension(FamilyManager manager, string name, double value)
        {
            var parameter = CodexParameterBuilder.NewInternal(manager, name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            manager.SetFormula(parameter, Mm(value).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " mm");
        }
        private static View3D CreatePreview(Document doc, Bounds bounds, string name = "BIMaestro - Aperçu", XYZ direction = null)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            var view = View3D.CreateIsometric(doc, type.Id); view.Name = name;
            view.DisplayStyle = DisplayStyle.Shading; view.DetailLevel = ViewDetailLevel.Fine;
            var center = (bounds.Min + bounds.Max) * 0.5;
            double size = bounds.Max.DistanceTo(bounds.Min);
            var forward = (direction ?? new XYZ(-1, 1, -0.65)).Normalize();
            var right = forward.CrossProduct(Math.Abs(forward.Z) > 0.999 ? XYZ.BasisY : XYZ.BasisZ).Normalize(); var up = right.CrossProduct(forward).Normalize();
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
            for (int i = 0; i < p.Count; i++) loop.Append(CodexCreationGuard.CreateLine(p[i], p[(i + 1) % p.Count]));
            return loop;
        }
        private static CurveLoop Circle(double radius)
        {
            var loop = new CurveLoop();
            loop.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return loop;
        }
        internal static Solid MakeSolid(FamilyPrimitive spec, ElementId materialId = null)
        {
            double a = Feet(spec.Size[0]), b = Feet(spec.Size[1]), h = Feet(spec.Size[2]); Solid raw;
            var options = new SolidOptions(materialId ?? ElementId.InvalidElementId, ElementId.InvalidElementId);
            if (spec.Kind == "loft")
            {
                var loops = spec.Sections.Select(section => Polygon(section.Select(Point))).ToList();
                try { raw = GeometryCreationUtilities.CreateLoftGeometry(loops, options); }
                finally { foreach (var loop in loops) loop.Dispose(); }
            }
            else if (spec.Kind == "sphere")
            {
                var loop = new CurveLoop();
                loop.Append(Arc.Create(new XYZ(0, 0, -a), new XYZ(0, 0, a), new XYZ(a, 0, 0)));
                loop.Append(CodexCreationGuard.CreateLine(new XYZ(0, 0, a), new XYZ(0, 0, -a)));
                raw = GeometryCreationUtilities.CreateRevolvedGeometry(new Frame(XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ), new[] { loop }, 0, 2 * Math.PI, options);
            }
            else if (spec.Kind == "cone" || spec.Kind == "lathe")
            {
                var points = spec.Kind == "lathe" ? spec.Profile.Select(p => new XYZ(Feet(p[0]), 0, Feet(p[1]))).ToList()
                    : new[] { XYZ.Zero, new XYZ(a, 0, 0), new XYZ(b, 0, h), new XYZ(0, 0, h) }.Distinct(new PointComparer()).ToList();
                raw = GeometryCreationUtilities.CreateRevolvedGeometry(new Frame(XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ), new[] { Polygon(points) }, 0, 2 * Math.PI, options);
            }
            else
            {
                IList<CurveLoop> loops;
                if (spec.Kind == "box") loops = new[] { Polygon(new[] { XYZ.Zero, new XYZ(a, 0, 0), new XYZ(a, b, 0), new XYZ(0, b, 0) }) };
                else if (spec.Kind == "extrusion") loops = new[] { Polygon(spec.Profile.Select(p => new XYZ(Feet(p[0]), Feet(p[1]), 0))) };
                else if (spec.Kind == "tube") loops = new[] { Circle(a), Circle(b) };
                else loops = new[] { Circle(a) };
                raw = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, h, options);
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
        internal sealed class Bounds
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
        internal sealed class Failures : IFailuresPreprocessor
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
