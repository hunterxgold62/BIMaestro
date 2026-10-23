using Analyse;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace BIMaestro.HistoryTests
{
    public sealed partial class HistoryNativeTests
    {
        private sealed class OpeningSample
        {
            internal string Uid;
            internal string Class;
            internal string Category;
            internal string Family;
            internal string Label;
            internal int TypeId;
            internal JObject Snapshot;
            internal double CaptureMs;
        }

        private static string FileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }

        private static double[] Coordinates(XYZ value) => value == null ? null : new[] { value.X, value.Y, value.Z };
        private static JObject RecipeOf(OpeningSample sample) => sample.Snapshot["Recipe"] as JObject;
        private static bool HasDirectRecipe(OpeningSample sample) => RecipeOf(sample) != null
            && (string)RecipeOf(sample)["Kind"] != "native_archive";
        private static bool HasOpeningRecipe(OpeningSample sample)
            => (bool?)((RecipeOf(sample)?["Native"] as JObject)?["WholeModel"]) == true;

        private static IEnumerable<JObject> HvacPorts(JObject network)
            => (network?["ConnectorClassifications"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(port => (string)port["Domain"] == "DomainHvac");

        private static bool HvacPortsPreserved(JObject source, JObject actual)
        {
            var saved = HvacPorts(source).ToDictionary(port => (int)port["Id"]);
            var current = HvacPorts(actual).ToDictionary(port => (int)port["Id"]);
            return saved.Count > 0 && saved.Count == current.Count && saved.All(pair =>
                current.TryGetValue(pair.Key, out var port)
                && !string.IsNullOrWhiteSpace((string)pair.Value["Classification"])
                && (string)pair.Value["Classification"] == (string)port["Classification"]
                && (bool?)pair.Value["Connected"] == (bool?)port["Connected"]
                && (!((int?)pair.Value["SystemTypeId"] > 0)
                    || (!string.IsNullOrWhiteSpace((string)pair.Value["SystemTypeUid"])
                        && (string)pair.Value["SystemTypeUid"] == (string)port["SystemTypeUid"])));
        }

        private static JObject NetworkState(Element element)
        {
            if (element == null) return null;
            var parameter = element.get_Parameter(element is Autodesk.Revit.DB.Plumbing.Pipe
                || element is Autodesk.Revit.DB.Plumbing.FlexPipe ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            var systemTypeId = parameter?.StorageType == StorageType.ElementId ? parameter.AsElementId() : null;
            var type = systemTypeId == null ? null : element.Document.GetElement(systemTypeId) as MEPSystemType;
            MEPSystem system = null;
            try { system = (element as MEPCurve)?.MEPSystem; } catch { }
            var actualSystemType = system == null ? null : element.Document.GetElement(system.GetTypeId()) as MEPSystemType;
            var classification = element.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
            string classificationText = null;
            try { classificationText = classification?.StorageType == StorageType.String ? classification.AsString() : classification?.AsValueString(); }
            catch { }
            return JObject.FromObject(new {
                Uid = element.UniqueId, Id = element.Id.GetIdValue(), Class = element.GetType().Name,
                SystemTypeId = systemTypeId?.GetIdValue(), SystemTypeUid = type?.UniqueId, SystemTypeName = type?.Name,
                Classification = type?.SystemClassification.ToString(),
                ClassificationCode = type == null ? (int?)null : (int)type.SystemClassification,
                ClassificationParameter = classificationText,
                ClassificationParameterCode = classification?.StorageType == StorageType.Integer ? (int?)classification.AsInteger() : null,
                MEPSystemMissing = system == null, MEPSystemId = system?.Id.GetIdValue(), MEPSystemUid = system?.UniqueId,
                MEPSystemName = system?.Name, MEPSystemTypeId = actualSystemType?.Id.GetIdValue(),
                MEPSystemTypeName = actualSystemType?.Name, MEPSystemClassification = actualSystemType?.SystemClassification.ToString(),
                ConnectorClassifications = ElementHistoryNetwork.Ports(element).OrderBy(port => port.Id).Select(port => {
                    string connectorClassification = null;
                    try { connectorClassification = port.Domain == Domain.DomainHvac ? port.DuctSystemType.ToString()
                        : port.Domain == Domain.DomainPiping ? port.PipeSystemType.ToString() : null; } catch { }
                    MEPSystem connectorSystem = null;
                    try { connectorSystem = port.MEPSystem; } catch { }
                    var connectorType = connectorSystem == null ? null : element.Document.GetElement(connectorSystem.GetTypeId());
                    return new { Id = port.Id, Domain = port.Domain.ToString(), Classification = connectorClassification,
                        SystemTypeId = connectorSystem?.GetTypeId().GetIdValue(), SystemTypeUid = connectorType?.UniqueId,
                        Connected = port.IsConnected };
                }).ToList()
            });
        }

        private JObject ProbeUndefinedSystemRollback(Document doc, Element element)
        {
            var before = NetworkState(element);
            var uid = element.UniqueId;
            var linksBefore = ElementHistoryNetwork.CaptureConnections(element).Count;
            var probe = new JObject { ["Before"] = before, ["LinksBefore"] = linksBefore };
            using (var group = new TransactionGroup(doc, "Diagnostic unset system type, always rolled back"))
            {
                group.Start();
                try
                {
                    var parameter = element.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                    probe["ParameterReadOnly"] = parameter?.IsReadOnly;
                    if (parameter != null && !parameter.IsReadOnly)
                    {
                        using (var tx = new Transaction(doc, "Try invalid system type on restored test copy"))
                        {
                            tx.Start();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                            try
                            {
                                probe["SetReturned"] = parameter.Set(ElementId.InvalidElementId);
                                doc.Regenerate();
                                probe["CommitStatus"] = tx.Commit().ToString();
                            }
                            catch (Exception ex)
                            {
                                probe["Error"] = ex.ToString();
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                            }
                        }
                    }
                    probe["AfterAttempt"] = NetworkState(doc.GetElement(uid));
                    probe["LinksAfterAttempt"] = ElementHistoryNetwork.CaptureConnections(doc.GetElement(uid)).Count;
                }
                finally { group.RollBack(); }
            }
            probe["AfterRollback"] = NetworkState(doc.GetElement(uid));
            probe["LinksAfterRollback"] = ElementHistoryNetwork.CaptureConnections(doc.GetElement(uid)).Count;
            Check(JToken.DeepEquals(before, probe["AfterRollback"]) && (int)probe["LinksAfterRollback"] == linksBefore,
                "System assignment diagnostic did not fully roll back");
            return probe;
        }

        // The launcher supplies a fresh workspace copy. Every destructive scenario rolls back.
        // This measures API capture work and opening protection; production UI cadence is separate.
        private List<string> RunOpeningBenchmark(UIApplication ui)
        {
            var results = new List<string>();
            var source = Path.Combine(Output, "real-model-source.rvt");
            var sourceHash = FileHash(source);
            var options = new OpenOptions();
            using (var info = BasicFileInfo.Extract(source))
                if (info.IsWorkshared)
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            var watch = Stopwatch.StartNew();
            var doc = ui.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), options);
            var report = new Dictionary<string, object> {
                { "RevitVersion", ui.Application.VersionNumber }, { "SourceSha256Before", sourceHash },
                { "OpenMilliseconds", watch.Elapsed.TotalMilliseconds },
                { "OpenedDocumentState", new { doc.PathName, doc.IsModified, doc.IsWorkshared, doc.IsFamilyDocument,
                    doc.IsReadOnly, doc.IsDetached, NativeOwns = ElementHistoryNativeArchive.Owns(doc) } },
                { "Measurement", "API capture and isolated rollback restoration; no UI scheduler throughput claim" }
            };
            void Save(string stage)
            {
                report["Stage"] = stage;
                File.WriteAllText(Path.Combine(Output, "opening-benchmark.json"),
                    JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            try
            {
                Save("opened-copy");
                var assembly = Assembly.Load(File.ReadAllBytes(File.ReadAllText(Path.Combine(Output, "production-dll.txt")).Trim()));
                // Redirect only this in-memory test assembly's store, without changing user settings.
                assembly.GetType("Analyse.CollaborativeModelTrackerStore", true)
                    .GetField("_activeDirectory", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, Output);
                if (File.Exists(Path.Combine(Output, "targeted-regressions-only.txt")))
                {
                    RunTargetsOnly(doc, assembly, report, results, Save);
                    Save("complete");
                    return results;
                }
                VerifyOpeningProtection(doc, assembly, report, results, Save);

                var tracker = assembly.GetType("Analyse.ElementHistoryTracker", true);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                var build = tracker.GetMethod("BuildSnapshot", flags);
                var ignore = tracker.GetMethod("ShouldIgnoreElement", flags);
                var invalidate = tracker.GetMethod("InvalidateCaptureCaches", flags);
                var getCache = tracker.GetMethod("GetTypeParameterCache", flags);
                Check(build != null && ignore != null && getCache != null, "Production baseline hooks missing");
                invalidate.Invoke(null, new object[] { doc });
                var typeCache = getCache.Invoke(null, new object[] { doc });
                var categories = doc.Settings.Categories.Cast<Category>()
                    .Where(c => c.CategoryType == CategoryType.Model).Select(c => c.Id).ToList();
                var ids = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(categories)).ToElementIds()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).ToElementIds())
                    .Distinct().ToList();
                report["Candidates"] = ids.Count;
                var captured = new List<OpeningSample>();
                var failures = new List<object>();
                int skipped = 0;
                double apiMs = 0;
                watch.Restart();
                for (int index = 0; index < ids.Count; index++)
                {
                    var element = doc.GetElement(ids[index]);
                    if (element == null || (bool)ignore.Invoke(null, new object[] { element })) { skipped++; continue; }
                    try
                    {
                        var timer = Stopwatch.StartNew();
                        var raw = build.Invoke(null, new object[] { element, false, 0, false, typeCache });
                        var elapsed = timer.Elapsed.TotalMilliseconds;
                        apiMs += elapsed;
                        captured.Add(new OpeningSample {
                            Uid = element.UniqueId, Class = element.GetType().Name,
                            Category = element.Category?.Name ?? "(none)", Label = element.Name,
                            Family = (element as FamilyInstance)?.Symbol?.FamilyName,
                            TypeId = element.GetTypeId().GetIdValue(), Snapshot = JObject.FromObject(raw), CaptureMs = elapsed
                        });
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { ElementId = ids[index].GetIdValue(), Uid = element.UniqueId,
                            Class = element.GetType().Name, Error = (ex.InnerException ?? ex).ToString() });
                    }
                    if (index % 500 == 0)
                    {
                        report["Visited"] = index + 1;
                        report["Captured"] = captured.Count;
                        report["BaselineWallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
                        Save("capturing-production-baseline");
                    }
                }
                report["BaselineWallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
                report["BaselineApiMilliseconds"] = apiMs;
                report["ApiCaptureWithinFiveMinutes"] = apiMs < 300000;
                report["Captured"] = captured.Count;
                report["Skipped"] = skipped;
                report["Failures"] = failures;
                report["RecipesAvailable"] = captured.Count(s => RecipeOf(s) != null);
                report["DirectRecipes"] = captured.Count(HasDirectRecipe);
                report["OpeningRecipes"] = captured.Count(HasOpeningRecipe);
                report["Categories"] = captured.GroupBy(s => s.Category).Select(g => new {
                    Category = g.Key, Captured = g.Count(),
                    RecipesAvailable = g.Count(s => RecipeOf(s) != null),
                    DirectRecipes = g.Count(HasDirectRecipe), OpeningRecipes = g.Count(HasOpeningRecipe),
                    Milliseconds = g.Sum(s => s.CaptureMs), MaximumMilliseconds = g.Max(s => s.CaptureMs)
                }).OrderByDescending(g => g.Milliseconds).ToList();
                report["Classes"] = captured.GroupBy(s => s.Class).Select(g => new {
                    Class = g.Key, Captured = g.Count(),
                    RecipesAvailable = g.Count(s => RecipeOf(s) != null),
                    DirectRecipes = g.Count(HasDirectRecipe), OpeningRecipes = g.Count(HasOpeningRecipe),
                    Milliseconds = g.Sum(s => s.CaptureMs), MaximumMilliseconds = g.Max(s => s.CaptureMs)
                }).OrderByDescending(g => g.Milliseconds).ToList();
                report["SlowestCaptures"] = captured.OrderByDescending(s => s.CaptureMs).Take(30)
                    .Select(s => new { s.Uid, s.Class, s.Category, s.Family, s.Label, s.CaptureMs }).ToList();
                Save("baseline-complete");
                Check(failures.Count == 0, "Production baseline threw for " + failures.Count + " candidates; see opening-benchmark.json");
                results.Add("all eligible saved-copy candidates captured with production BuildSnapshot; per-category/class timing and direct recipe coverage recorded");

                VerifyProductionInventoryScheduler(doc, tracker, report, results, Save);
                VerifyTargetedOpeningFallbacks(doc, assembly, captured, report, results, Save);

                var samples = captured.Where(HasDirectRecipe)
                    .GroupBy(s => s.Class + "|" + s.Category + "|" + (string)RecipeOf(s)["Kind"]
                        + "|" + (string)RecipeOf(s)["Placement"])
                    .SelectMany(g => g.Take(2)).Take(30).ToList();
                Check(samples.Count > 0, "No direct recipes available to validate");
                var outcomes = new List<object>();
                int restoreFailures = 0;
                foreach (var sample in samples)
                {
                    var original = doc.GetElement(sample.Uid);
                    var point = (original.Location as LocationPoint)?.Point;
                    var curve = (original.Location as LocationCurve)?.Curve;
                    var endpoints = curve == null ? null : new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) };
                    var request = new HistoryRestoreRequest {
                        SourceUniqueId = sample.Uid, Label = sample.Label, Category = sample.Category,
                        Recipe = RecipeOf(sample).ToObject<HistoryRecipe>(),
                        SuperComponentUniqueId = (string)sample.Snapshot["SuperComponentUniqueId"],
                        FamilyTypeUniqueId = (string)sample.Snapshot["FamilyTypeUniqueId"]
                    };
                    request = JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(request));
                    var countBefore = new FilteredElementCollector(doc).GetElementCount();
                    using (var group = new TransactionGroup(doc, "Antibes direct recipe rollback check"))
                    {
                        group.Start();
                        try
                        {
                            int deleted;
                            using (var tx = new Transaction(doc, "Delete direct recipe test root"))
                            {
                                tx.Start();
                                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                                deleted = doc.Delete(original.Id).Count;
                                Check(tx.Commit() == TransactionStatus.Committed, "Fixture deletion rejected");
                            }
                            watch.Restart();
                            int sourcesOpened = 0;
                            EventHandler<Autodesk.Revit.DB.Events.DocumentOpenedEventArgs> onOpen = (sender, args) => {
                                if (!doc.Equals(args.Document)) sourcesOpened++;
                            };
                            doc.Application.DocumentOpened += onOpen;
                            HistoryRestoreBatch restored;
                            try { restored = ElementHistoryRestoration.Restore(doc, new[] { request }); }
                            finally { doc.Application.DocumentOpened -= onOpen; }
                            var item = restored.Items.FirstOrDefault();
                            var actual = item?.UniqueId == null ? null : doc.GetElement(item.UniqueId);
                            var correctClass = actual?.GetType().Name == sample.Class;
                            var correctType = actual?.GetTypeId().GetIdValue() == sample.TypeId;
                            var actualPoint = (actual?.Location as LocationPoint)?.Point;
                            var actualCurve = (actual?.Location as LocationCurve)?.Curve;
                            var positionMatches = point == null || (actualPoint != null && point.DistanceTo(actualPoint) < 1e-5);
                            if (endpoints != null)
                                positionMatches = actualCurve != null &&
                                    ((endpoints[0].DistanceTo(actualCurve.GetEndPoint(0)) < 1e-5 && endpoints[1].DistanceTo(actualCurve.GetEndPoint(1)) < 1e-5)
                                    || (endpoints[0].DistanceTo(actualCurve.GetEndPoint(1)) < 1e-5 && endpoints[1].DistanceTo(actualCurve.GetEndPoint(0)) < 1e-5));
                            bool passed = restored.Failed == 0 && restored.Created == 1 && correctClass && correctType && positionMatches;
                            if (!passed) restoreFailures++;
                            outcomes.Add(new { sample.Uid, sample.Class, sample.Category, RecipeKind = request.Recipe.Kind,
                                DeletedIncludingDependents = deleted, Milliseconds = watch.Elapsed.TotalMilliseconds,
                                Passed = passed, CorrectClass = correctClass, CorrectType = correctType,
                                PositionMatches = positionMatches, SourcesOpened = sourcesOpened, Restore = restored });
                        }
                        catch (Exception ex)
                        {
                            restoreFailures++;
                            outcomes.Add(new { sample.Uid, sample.Class, sample.Category, Passed = false, Error = ex.ToString() });
                        }
                        finally { group.RollBack(); }
                    }
                    Check(doc.GetElement(sample.Uid) != null && new FilteredElementCollector(doc).GetElementCount() == countBefore,
                        "Rollback changed original model elements");
                    report["DirectRestoreSamples"] = outcomes;
                    Save("checking-direct-recipes");
                }
                if (restoreFailures > 0) results.Add("FAILED: " + restoreFailures + " direct recipe samples did not restore class/type/placement; see report");
                else results.Add(samples.Count + " representative direct recipes survive JSON, deletion and native restoration with matching class/type/placement; every scenario rolled back");
                Save("complete");
            }
            finally
            {
                ElementHistoryOpeningArchive.Shutdown();
                ElementHistoryNativeArchive.Shutdown();
                doc.Close(false);
                var finalHash = FileHash(source);
                report["SourceSha256After"] = finalHash;
                report["SourceUnchanged"] = sourceHash == finalHash;
                Save((string)report["Stage"]);
                Check(sourceHash == finalHash, "Saved source copy changed");
            }
            return results;
        }

        private void VerifyOpeningProtection(Document doc, Assembly production, Dictionary<string, object> report,
            List<string> results, Action<string> save)
        {
            var watch = Stopwatch.StartNew();
            ElementHistoryOpeningArchive.Schedule(doc);
            report["OpeningScheduleMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            var categories = doc.Settings.Categories.Cast<Category>()
                .Where(c => c.CategoryType == CategoryType.Model).Select(c => c.Id).ToList();
            var samples = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WhereElementIsViewIndependent().WherePasses(new ElementMulticategoryFilter(categories))
                .Where(e => e is FamilyInstance || e is Wall || e is Floor || e is MEPCurve)
                .Where(e => e.GroupId == ElementId.InvalidElementId && (!(e is FamilyInstance instance) || instance.SuperComponent == null))
                .Where(e => !ElementHistoryRestoration.IsCalorifuge(e.Category?.Name, (e as FamilyInstance)?.Symbol?.FamilyName + " " + e.Name))
                .GroupBy(e => e.GetType().Name + "|" + e.Category?.Name).Select(g => g.First())
                .Take(5).ToList();
            Check(samples.Count > 0, "Opening snapshot needs independent test roots");
            var receipts = samples.ToDictionary(e => e.UniqueId, e => ElementHistoryOpeningArchive.TryCreateRecipe(doc, e));
            var sampleUids = samples.Select(e => e.UniqueId).ToList();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var productionOpening = production.GetType("Analyse.ElementHistoryOpeningArchive", true);
            productionOpening.GetMethod("Schedule", flags).Invoke(null, new object[] { doc });
            report["OpeningReceiptDiagnostics"] = samples.Select(e => {
                string rootUid = null, rootError = null;
                int rootTypeId = -1;
                try
                {
                    var root = ElementHistoryNativeArchive.OpeningRoot(e);
                    rootUid = root?.UniqueId;
                    rootTypeId = root?.GetTypeId().GetIdValue() ?? -1;
                }
                catch (Exception ex) { rootError = ex.ToString(); }
                return new {
                    Id = e.Id.GetIdValue(), Uid = e.UniqueId, Class = e.GetType().Name,
                    Category = e.Category?.Name, CategoryType = e.Category?.CategoryType.ToString(), e.ViewSpecific,
                    ElementDocumentOperatorEqual = e.Document == doc,
                    ElementDocumentReferenceEqual = ReferenceEquals(e.Document, doc),
                    ElementDocumentEquals = e.Document.Equals(doc), ElementDocumentHash = e.Document.GetHashCode(),
                    ScheduledDocumentHash = doc.GetHashCode(), RootUid = rootUid, RootTypeId = rootTypeId,
                    RootError = rootError, Receipt = receipts[e.UniqueId]
                };
            }).ToList();
            var openingStates = (System.Collections.IDictionary)typeof(ElementHistoryOpeningArchive)
                .GetField("States", flags).GetValue(null);
            var openingState = openingStates[doc];
            report["OpeningStatePresent"] = openingState != null;
            if (openingState != null)
                report["OpeningStateDiagnostics"] = openingState.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field => field.FieldType == typeof(string) || field.FieldType == typeof(bool)
                        || field.FieldType == typeof(long) || field.FieldType == typeof(DateTime) || field.FieldType == typeof(DateTime?))
                    .ToDictionary(field => field.Name, field => field.GetValue(openingState));
            save("opening-receipt-diagnostic");
            Check(receipts.Values.All(r => r?.Native?.WholeModel == true), "Opening archive was not scheduled for the freshly opened copy");
            // Force the persisted-pending path even if the filesystem copy wins the race.
            // Ready is a worker notification, not permission to trust an absent/partial file.
            var pending = JsonConvert.DeserializeObject<HistoryRecipe>(JsonConvert.SerializeObject(receipts.Values.First()));
            pending.Native.Ready = false;
            report["OpeningReceiptInitiallyReady"] = receipts.Values.First().Native.Ready;
            var copyTask = ElementHistoryOpeningArchive.GetCopyTask(doc);
            copyTask.GetAwaiter().GetResult();
            report["OpeningCopyWallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            var archivePath = receipts.Values.First().Native.File;
            Check(File.Exists(archivePath), "Opening archive not published after copy completion");
            var archiveHash = FileHash(archivePath);
            Check(archiveHash == (string)report["SourceSha256Before"], "Opening archive bytes differ from the saved source");
            report["OpeningArchiveBytes"] = new FileInfo(archivePath).Length;
            report["OpeningArchiveSha256"] = archiveHash;
            report["OpeningCopyWithinFiveMinutes"] = watch.Elapsed.TotalMinutes < 5;
            save("opening-file-protected-before-element-scan");

            var outcomes = new List<object>();
            int openingFailures = 0;
            foreach (var uid in sampleUids)
            {
                var original = doc.GetElement(uid);
                var originalClass = original.GetType().Name;
                var type = original.GetTypeId().GetIdValue();
                var expectedType = doc.GetElement(original.GetTypeId());
                var expectedTypeUid = expectedType?.UniqueId;
                var expectedTypeName = expectedType?.Name;
                var originalFamily = (original as FamilyInstance)?.Symbol?.Family;
                var expectedFamilyName = originalFamily?.Name;
                var originalIsInPlace = originalFamily?.IsInPlace;
                var point = (original.Location as LocationPoint)?.Point;
                var curve = (original.Location as LocationCurve)?.Curve;
                var endpoints = curve == null ? null : new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) };
                var bounds = original.get_BoundingBox(null);
                var boundsPoints = bounds == null ? null : new[] { bounds.Min, bounds.Max };
                var count = new FilteredElementCollector(doc).GetElementCount();
                using (var group = new TransactionGroup(doc, "Opening fallback deletion and restoration rollback"))
                {
                    group.Start();
                    try
                    {
                        var id = original.Id;
                        int deleted;
                        bool deletedOriginalType;
                        using (var tx = new Transaction(doc, "Delete before any element baseline capture"))
                        {
                            tx.Start();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                            var deletedIds = doc.Delete(id);
                            deleted = deletedIds.Count;
                            deletedOriginalType = deletedIds.Any(deletedId => deletedId.GetIdValue() == type);
                            Check(tx.Commit() == TransactionStatus.Committed, "Opening scenario deletion rejected");
                        }
                        var recipe = outcomes.Count == 0 ? ElementHistoryOpeningArchive.FreezeForDeletion(doc, pending, id)
                            : ElementHistoryOpeningArchive.TryCreateDeletedRecipe(doc, id);
                        Check(recipe != null, "Unchanged deleted ID lost its opening fallback");
                        var originalTypeExistsAfterDelete = doc.GetElement(ElementIdExtensions.CreateElementId(type)) != null;
                        if (outcomes.Count == 0) recipe.Native.Ready = false;
                        var request = new HistoryRestoreRequest { SourceUniqueId = null, Label = originalClass + " [" + id.GetIdValue() + "]",
                            Recipe = JsonConvert.DeserializeObject<HistoryRecipe>(JsonConvert.SerializeObject(recipe)) };
                        watch.Restart();
                        var restored = RestoreOpeningWithOwnership(doc, new[] { request });
                        var item = restored.Items.FirstOrDefault();
                        var actual = item?.UniqueId == null ? null : doc.GetElement(item.UniqueId);
                        var actualType = actual == null ? null : doc.GetElement(actual.GetTypeId());
                        var actualPoint = (actual?.Location as LocationPoint)?.Point;
                        var actualCurve = (actual?.Location as LocationCurve)?.Curve;
                        var positionMatches = point == null || (actualPoint != null && point.DistanceTo(actualPoint) < 1e-5);
                        if (endpoints != null)
                            positionMatches = actualCurve != null &&
                                ((endpoints[0].DistanceTo(actualCurve.GetEndPoint(0)) < 1e-5 && endpoints[1].DistanceTo(actualCurve.GetEndPoint(1)) < 1e-5)
                                || (endpoints[0].DistanceTo(actualCurve.GetEndPoint(1)) < 1e-5 && endpoints[1].DistanceTo(actualCurve.GetEndPoint(0)) < 1e-5));
                        else if (point == null && boundsPoints != null)
                        {
                            var actualBounds = actual?.get_BoundingBox(null);
                            positionMatches = actualBounds != null && boundsPoints[0].DistanceTo(actualBounds.Min) < 1e-5
                                && boundsPoints[1].DistanceTo(actualBounds.Max) < 1e-5;
                        }
                        var recreatedInPlaceType = originalIsInPlace == true && deletedOriginalType && !originalTypeExistsAfterDelete
                            && actualType?.Name == expectedTypeName && (actual as FamilyInstance)?.Symbol?.Family?.Name == expectedFamilyName;
                        var typeMatches = actual?.GetTypeId().GetIdValue() == type || recreatedInPlaceType;
                        bool passed = restored.Failed == 0 && restored.Created == 1
                            && actual?.GetType().Name == originalClass && typeMatches
                            && positionMatches;
                        if (!passed) openingFailures++;
                        var finalBounds = actual?.get_BoundingBox(null);
                        outcomes.Add(new { Uid = uid, Class = originalClass, Passed = passed,
                            DeletedIncludingDependents = deleted, Milliseconds = watch.Elapsed.TotalMilliseconds,
                            ActualClass = actual?.GetType().Name, ExpectedTypeId = type, ActualTypeId = actual?.GetTypeId().GetIdValue(),
                            ExpectedTypeUid = expectedTypeUid, ActualTypeUid = actualType?.UniqueId,
                            ExpectedTypeName = expectedTypeName, ActualTypeName = actualType?.Name,
                            ExpectedFamilyName = expectedFamilyName, ActualFamilyName = (actual as FamilyInstance)?.Symbol?.Family?.Name,
                            OriginalFamilyIsInPlace = originalIsInPlace, OriginalTypeDeletedWithInstance = deletedOriginalType,
                            OriginalTypeStillExistsAfterDelete = originalTypeExistsAfterDelete,
                            TypeMatches = typeMatches, InPlaceDefinitionRecreated = recreatedInPlaceType,
                            PositionMatches = positionMatches, ExpectedPoint = Coordinates(point), ActualPoint = Coordinates(actualPoint),
                            PointDistance = point == null || actualPoint == null ? (double?)null : point.DistanceTo(actualPoint),
                            ExpectedCurve = endpoints?.Select(Coordinates).ToArray(),
                            ActualCurve = actualCurve == null ? null : new[] { Coordinates(actualCurve.GetEndPoint(0)), Coordinates(actualCurve.GetEndPoint(1)) },
                            CurveDirectDistances = endpoints == null || actualCurve == null ? null
                                : new[] { endpoints[0].DistanceTo(actualCurve.GetEndPoint(0)), endpoints[1].DistanceTo(actualCurve.GetEndPoint(1)) },
                            CurveReversedDistances = endpoints == null || actualCurve == null ? null
                                : new[] { endpoints[0].DistanceTo(actualCurve.GetEndPoint(1)), endpoints[1].DistanceTo(actualCurve.GetEndPoint(0)) },
                            ExpectedBounds = boundsPoints?.Select(Coordinates).ToArray(),
                            ActualBounds = finalBounds == null ? null : new[] { Coordinates(finalBounds.Min), Coordinates(finalBounds.Max) },
                            BoundsDistances = boundsPoints == null || finalBounds == null ? null
                                : new[] { boundsPoints[0].DistanceTo(finalBounds.Min), boundsPoints[1].DistanceTo(finalBounds.Max) },
                            Restore = restored });
                        report["OpeningRestoreSamples"] = outcomes;
                        save("restoring-before-element-scan");
                    }
                    catch (Exception ex)
                    {
                        openingFailures++;
                        outcomes.Add(new { Uid = uid, Class = originalClass, Passed = false, Error = ex.ToString() });
                        report["OpeningRestoreSamples"] = outcomes;
                        save("restoring-before-element-scan");
                    }
                    finally { group.RollBack(); }
                }
                Check(doc.GetElement(uid) != null && new FilteredElementCollector(doc).GetElementCount() == count,
                    "Opening test rollback did not preserve original elements");
            }
            Check(FileHash(archivePath) == archiveHash, "Opening RVT archive changed during restoration");
            report["OpeningArchiveImmutableAfterRestores"] = true;
            if (openingFailures > 0) results.Add("FAILED: " + openingFailures + " opening-root samples did not restore original class/type/placement; see exact coordinates and type identities in report");
            else results.Add(samples.Count + " unchanged model roots restored from opening RVT before any element scan, including persisted pending receipt and deleted-ID lookup without captured UID; archive byte identity and rollback verified");

            VerifyProductionOpeningDeletionBatch(doc, production, sampleUids.Take(3).ToList(), report, results, save);

            var changed = doc.GetElement(sampleUids.First());
            ElementHistoryOpeningArchive.Invalidate(doc, new[] { changed.Id });
            Check(ElementHistoryOpeningArchive.TryCreateRecipe(doc, changed) == null
                && ElementHistoryOpeningArchive.TryCreateDeletedRecipe(doc, changed.Id) == null,
                "Changed element still offers stale opening state");
            using (var tx = new Transaction(doc, "New element cannot use opening snapshot"))
            {
                tx.Start();
                var added = Level.Create(doc, 9876);
                var addedId = added.Id;
                ElementHistoryOpeningArchive.Invalidate(doc, new[] { addedId });
                Check(ElementHistoryOpeningArchive.TryCreateDeletedRecipe(doc, addedId) == null,
                    "Added element incorrectly reuses a saved opening ID");
                tx.RollBack();
            }
            var typed = sampleUids.Select(doc.GetElement).First(e => e.GetTypeId() != ElementId.InvalidElementId);
            var staleReceipt = receipts[typed.UniqueId];
            ElementHistoryOpeningArchive.Invalidate(doc, new[] { typed.GetTypeId() });
            Check(ElementHistoryOpeningArchive.TryCreateRecipe(doc, typed) == null,
                "Modified type still offers obsolete instance definition");
            Check(ElementHistoryOpeningArchive.TryCreateDeletedRecipe(doc, typed.Id) == null,
                "Deleted ID without captured type bypasses changed-type protection");
            Check(ElementHistoryOpeningArchive.FreezeForDeletion(doc, staleReceipt, typed.Id) == null,
                "Cached whole-model receipt still offers stale state after a type change");
            results.Add("modified/added IDs and modified types reject stale opening fallback");
            report["OpeningInvalidationChecksPassed"] = true;
            ElementHistoryOpeningArchive.Forget(doc);
            save("opening-protection-validated");
        }

        private void VerifyProductionOpeningDeletionBatch(Document doc, Assembly production, List<string> uids,
            Dictionary<string, object> report, List<string> results, Action<string> save)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var opening = production.GetType("Analyse.ElementHistoryOpeningArchive", true);
            ((System.Threading.Tasks.Task)opening.GetMethod("GetCopyTask", flags).Invoke(null, new object[] { doc }))
                .GetAwaiter().GetResult();
            var tracker = production.GetType("Analyse.ElementHistoryTracker", true);
            var enqueue = tracker.GetMethod("EnqueueDeleted", flags);
            var convert = production.GetType("Analyse.ElementHistoryWindow", true).GetMethod("ToRestoreRequest", flags);
            var queue = tracker.GetField("Queue", flags).GetValue(null);
            var queueArray = queue.GetType().GetMethod("ToArray");
            var initialCount = ((Array)queueArray.Invoke(queue, null)).Length;
            var originalCount = new FilteredElementCollector(doc).GetElementCount();
            var ids = uids.Select(uid => doc.GetElement(uid).Id).ToList();
            using (var group = new TransactionGroup(doc, "Production uncaptured deletion batch rollback"))
            {
                group.Start();
                try
                {
                    using (var tx = new Transaction(doc, "Delete multiple roots before production baseline"))
                    {
                        tx.Start();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                        doc.Delete(ids);
                        Check(tx.Commit() == TransactionStatus.Committed, "Production batch deletion rejected");
                    }
                    foreach (var id in ids)
                        enqueue.Invoke(null, new object[] { doc, id, "isolated-test", "Opening deletion batch", null });
                    var events = ((Array)queueArray.Invoke(queue, null)).Cast<object>().Skip(initialCount).ToList();
                    Check(events.Count == ids.Count, "Production queue lost deleted roots");
                    var persisted = JArray.Parse(JsonConvert.SerializeObject(events));
                    Check(persisted.All(ev => {
                        var delta = ev["Delta"] as JObject;
                        var native = (delta?["recipe"] as JObject)?["Native"] as JObject;
                        return (bool?)delta?["snapshotMissing"] == true && (bool?)native?["WholeModel"] == true
                            && (int?)native?["SourceElementId"] > 0;
                    }),
                        "Uncaptured production event lost opening receipt or source ID during JSON persistence");
                    var requests = events.Select(ev => JsonConvert.DeserializeObject<HistoryRestoreRequest>(
                        JsonConvert.SerializeObject(convert.Invoke(null, new[] { ev })))).ToList();
                    Check(requests.All(r => string.IsNullOrEmpty(r.SourceUniqueId)),
                        "This scenario must test production conversion without captured UIDs");
                    Check(requests.Select(r => r.Label).Distinct().Count() == ids.Count,
                        "Uncaptured roots lost unique display labels");
                    var restored = ElementHistoryRestoration.Restore(doc, requests);
                    report["ProductionUncapturedBatch"] = new {
                        Requested = ids.Count, Events = persisted, Restore = restored
                    };
                    save("checking-production-deleted-event-pipeline");
                    Check(restored.Items.Count == ids.Count && restored.Failed == 0
                        && restored.Items.Select(i => i.SourceUniqueId).Distinct().Count() == ids.Count,
                        "Production uncaptured deletion batch collapsed identities or failed restoration");
                }
                finally { group.RollBack(); }
            }
            Check(uids.All(uid => doc.GetElement(uid) != null)
                && new FilteredElementCollector(doc).GetElementCount() == originalCount, "Production deletion batch rollback changed source elements");
            results.Add(ids.Count + " simultaneous production EnqueueDeleted events persist opening recipe/source IDs through JSON and ToRestoreRequest; null UIDs hydrate independently without grouping loss");
        }

        private HistoryRestoreBatch RestoreOpeningWithOwnership(Document doc, IEnumerable<HistoryRestoreRequest> requests)
        {
            int opened = 0, closed = 0;
            EventHandler<Autodesk.Revit.DB.Events.DocumentOpenedEventArgs> onOpen = (sender, args) => {
                if (args.Document != doc)
                {
                    Check(ElementHistoryNativeArchive.Owns(args.Document), "Opening fallback source not owned during DocumentOpened");
                    Check(!ElementHistoryNativeArchive.Owns(doc), "Opening fallback classified destination as archive");
                    opened++;
                }
            };
            EventHandler<Autodesk.Revit.DB.Events.DocumentClosingEventArgs> onClose = (sender, args) => {
                if (args.Document != doc)
                {
                    Check(ElementHistoryNativeArchive.Owns(args.Document), "Opening fallback source lost ownership before DocumentClosing");
                    closed++;
                }
            };
            doc.Application.DocumentOpened += onOpen;
            doc.Application.DocumentClosing += onClose;
            try
            {
                var result = ElementHistoryRestoration.Restore(doc, requests);
                Check(opened > 0 && closed == opened, "Opening source document lifecycle incomplete");
                return result;
            }
            finally
            {
                doc.Application.DocumentOpened -= onOpen;
                doc.Application.DocumentClosing -= onClose;
            }
        }

        private void VerifyProductionInventoryScheduler(Document doc, Type tracker, Dictionary<string, object> report,
            List<string> results, Action<string> save)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var schedule = tracker.GetMethod("ScheduleDeferredPrime", flags);
            var process = tracker.GetMethod("ProcessDeferredPrime", flags);
            var pending = tracker.GetMethod("HasPendingBasicInventory", flags);
            var audits = (System.Collections.IDictionary)tracker.GetField("PreloadAuditByDocumentKey", flags).GetValue(null);
            var snapshots = (System.Collections.IDictionary)tracker.GetField("SnapshotByElementId", flags).GetValue(null);
            var durations = new List<double>();
            var watch = Stopwatch.StartNew();
            schedule.Invoke(null, new object[] { doc });
            var nextProgress = TimeSpan.Zero;
            while ((bool)pending.Invoke(null, new object[] { doc }) && watch.Elapsed.TotalSeconds < 300)
            {
                var slice = Stopwatch.StartNew();
                process.Invoke(null, new object[] { doc });
                durations.Add(slice.Elapsed.TotalMilliseconds);
                if (watch.Elapsed >= nextProgress)
                {
                    report["SchedulerWallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
                    report["SchedulerSnapshots"] = snapshots.Count;
                    report["SchedulerAudits"] = audits.Values.Cast<object>().ToList();
                    save("running-production-inventory-cadence");
                    nextProgress = watch.Elapsed + TimeSpan.FromSeconds(2);
                }
                // Explicit isolated callback cadence; no work runs while the test sleeps.
                System.Threading.Thread.Sleep(50);
            }
            var remaining = (bool)pending.Invoke(null, new object[] { doc });
            var ordered = durations.OrderBy(value => value).ToList();
            report["SchedulerWallMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            report["SchedulerCompleteWithinFiveMinutes"] = !remaining;
            report["SchedulerSnapshots"] = snapshots.Count;
            report["SchedulerAudits"] = audits.Values.Cast<object>().ToList();
            report["SchedulerSlices"] = durations.Count;
            report["SchedulerSliceMaximumMilliseconds"] = ordered.Count == 0 ? 0 : ordered.Last();
            report["SchedulerSliceP95Milliseconds"] = ordered.Count == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Count * .95) - 1];
            report["SchedulerSliceOver100Milliseconds"] = ordered.Count(value => value > 100);
            report["SchedulerSliceOver1000Milliseconds"] = ordered.Count(value => value > 1000);
            save("production-inventory-cadence-measured");
            if (remaining) results.Add("FAILED: production inventory still pending after 300s in isolated 50ms callback cadence; see audits");
            else results.Add("production ScheduleDeferredPrime/ProcessDeferredPrime completed within 300s at isolated 50ms cadence; wall time, P95/max callback and coverage audits recorded");
        }

        private void VerifyTargetedOpeningFallbacks(Document doc, Assembly production, List<OpeningSample> captured,
            Dictionary<string, object> report, List<string> results, Action<string> save)
        {
            var targets = new[] {
                new { Uid = "ee974953-383f-4838-bbc8-67cddf0cf5e4-000939c6", ForceBackup = false },
                new { Uid = "2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f", ForceBackup = false },
                new { Uid = "2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f", ForceBackup = true }
            };
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var tracker = production.GetType("Analyse.ElementHistoryTracker", true);
            var enqueue = tracker.GetMethod("EnqueueDeleted", flags);
            var convert = production.GetType("Analyse.ElementHistoryWindow", true).GetMethod("ToRestoreRequest", flags);
            var queue = tracker.GetField("Queue", flags).GetValue(null);
            var queueArray = queue.GetType().GetMethod("ToArray");
            var outcomes = new List<object>();
            int failed = 0;
            foreach (var target in targets)
            {
                var uid = target.Uid;
                var sample = captured.Single(s => s.Uid == uid);
                var original = doc.GetElement(uid);
                if (target.ForceBackup)
                    Check((int)tracker.GetMethod("PrimeElementSnapshot", flags)
                        .Invoke(null, new object[] { original, "isolated-target-recapture", null }) > 0,
                        "Target snapshot could not be recaptured for independent fault injection");
                var id = original.Id;
                var originalType = original.GetTypeId().GetIdValue();
                var originalTypeUid = doc.GetElement(original.GetTypeId())?.UniqueId;
                var point = (original.Location as LocationPoint)?.Point;
                var curve = (original.Location as LocationCurve)?.Curve;
                var endpoints = curve == null ? null : new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) };
                var midpoint = curve?.Evaluate(.5, true);
                var endpointParameters = original is FamilyInstance ? new[] {
                    BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION
                }.Select(builtIn => {
                    var parameter = original.get_Parameter(builtIn);
                    return new { BuiltIn = builtIn.ToString(), Exists = parameter != null, ReadOnly = parameter?.IsReadOnly,
                        Value = parameter?.StorageType == StorageType.Double ? (double?)parameter.AsDouble() : null };
                }).ToList() : null;
                var systemParameter = original is MEPCurve ? original.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM) : null;
                var systemTypeId = systemParameter?.AsElementId().GetIdValue();
                var originalNetwork = original is MEPCurve ? NetworkState(original) : null;
                var originalPeers = original is MEPCurve ? ElementHistoryNetwork.CaptureConnections(original)
                    .Select(link => link.Peer).Distinct().Select(peer => NetworkState(doc.GetElement(peer))).ToList() : null;
                var originalPhysicalLinks = original is MEPCurve ? ElementHistoryNetwork.CaptureConnections(original) : null;
                var originalConnectionCount = original is MEPCurve ? ElementHistoryNetwork.CaptureConnections(original).Count : 0;
                var historicalPeerTypes = (originalPeers ?? new List<JObject>()).Where(peer => peer != null)
                    .SelectMany(peer => new[] { (int?)peer["SystemTypeId"], (int?)peer["MEPSystemTypeId"] }
                        .Concat(HvacPorts(peer).Select(port => (int?)port["SystemTypeId"])))
                    .Where(typeId => typeId.HasValue && typeId.Value > 0).Select(typeId => typeId.Value).Distinct().ToList();
                var originalCount = new FilteredElementCollector(doc).GetElementCount();
                var initialEvents = ((Array)queueArray.Invoke(queue, null)).Length;
                using (var group = new TransactionGroup(doc, "Antibes formerly failed recipe fallback rollback"))
                {
                    group.Start();
                    try
                    {
                        using (var tx = new Transaction(doc, "Delete target before historical restoration"))
                        {
                            tx.Start();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new FixtureFailures()).SetClearAfterRollback(true));
                            doc.Delete(id);
                            Check(tx.Commit() == TransactionStatus.Committed, "Target deletion rejected");
                        }
                        enqueue.Invoke(null, new object[] { doc, id, "isolated-test", "Exact Antibes fallback regression", null });
                        var ev = ((Array)queueArray.Invoke(queue, null)).Cast<object>().Skip(initialEvents).Single();
                        var persistedEvent = JObject.Parse(JsonConvert.SerializeObject(ev));
                        var request = JsonConvert.DeserializeObject<HistoryRestoreRequest>(
                            JsonConvert.SerializeObject(convert.Invoke(null, new[] { ev })));
                        var originalKind = request.Recipe?.Kind;
                        Check(request.Recipe != null, "Target did not persist a usable capture or opening reference");
                        if (target.ForceBackup)
                        {
                            Check(request.Recipe.OpeningFallback?.WholeModel == true, "Fault injection requires a persisted opening backup");
                            request.Recipe.Type = Guid.NewGuid().ToString() + "-00000001";
                            request = JsonConvert.DeserializeObject<HistoryRestoreRequest>(JsonConvert.SerializeObject(request));
                        }
                        else if (originalNetwork == null) request.Recipe.OpeningFallback = null;
                        var requestAtRestore = JObject.FromObject(request);
                        int sourcesOpened = 0;
                        bool sourcesOwned = true;
                        EventHandler<Autodesk.Revit.DB.Events.DocumentOpenedEventArgs> onOpen = (sender, args) => {
                            if (!doc.Equals(args.Document))
                            {
                                sourcesOpened++;
                                sourcesOwned &= ElementHistoryNativeArchive.Owns(args.Document);
                            }
                        };
                        doc.Application.DocumentOpened += onOpen;
                        var watch = Stopwatch.StartNew();
                        HistoryRestoreBatch restored;
                        JObject afterNativeCreation = null;
                        try { restored = ElementHistoryRestoration.Restore(doc, new[] { request }, refreshView: () => {
                            if (originalNetwork == null || afterNativeCreation != null) return;
                            var created = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Mechanical.Duct))
                                .FirstOrDefault(candidate => ElementHistoryRestoration.GetOrigins(candidate)?.Contains(uid) == true);
                            if (created != null) afterNativeCreation = NetworkState(created);
                        }); }
                        finally { doc.Application.DocumentOpened -= onOpen; }
                        var item = restored.Items.SingleOrDefault();
                        var actual = item?.UniqueId == null ? null : doc.GetElement(item.UniqueId);
                        var actualPoint = (actual?.Location as LocationPoint)?.Point;
                        var actualCurve = (actual?.Location as LocationCurve)?.Curve;
                        var actualMidpoint = actualCurve?.Evaluate(.5, true);
                        var pointDistance = point == null || actualPoint == null ? (double?)null : point.DistanceTo(actualPoint);
                        var curveDistances = endpoints == null || actualCurve == null ? null
                            : new[] { endpoints[0].DistanceTo(actualCurve.GetEndPoint(0)), endpoints[1].DistanceTo(actualCurve.GetEndPoint(1)) };
                        bool positionMatches = point == null || (pointDistance.HasValue && pointDistance.Value < 1e-5);
                        if (endpoints != null)
                            positionMatches = actualCurve != null &&
                                ((curveDistances[0] < 1e-5 && curveDistances[1] < 1e-5)
                                || (endpoints[0].DistanceTo(actualCurve.GetEndPoint(1)) < 1e-5 && endpoints[1].DistanceTo(actualCurve.GetEndPoint(0)) < 1e-5));
                        if (midpoint != null) positionMatches &= actualMidpoint != null && midpoint.DistanceTo(actualMidpoint) < 1e-5;
                        var actualSystemTypeId = actual is MEPCurve
                            ? actual.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId().GetIdValue() : null;
                        bool systemMatches = systemTypeId > 0
                            ? !string.IsNullOrWhiteSpace((string)originalNetwork?["SystemTypeUid"])
                                && (string)originalNetwork["SystemTypeUid"] == (string)NetworkState(actual)?["SystemTypeUid"]
                            : systemTypeId == actualSystemTypeId;
                        var adjustment = item?.SystemTypeAdjustment;
                        var finalNetwork = actual is MEPCurve ? NetworkState(actual) : null;
                        var finalPeers = actual is MEPCurve ? ElementHistoryNetwork.CaptureConnections(actual)
                            .Select(link => link.Peer).Distinct().Select(peer => NetworkState(doc.GetElement(peer))).ToList() : null;
                        var sourcePeerByUid = (originalPeers ?? new List<JObject>()).ToDictionary(peer => (string)peer["Uid"]);
                        var actualPeerByUid = (finalPeers ?? new List<JObject>()).ToDictionary(peer => (string)peer["Uid"]);
                        var samePeers = sourcePeerByUid.Keys.OrderBy(value => value).SequenceEqual(actualPeerByUid.Keys.OrderBy(value => value));
                        var sourcePortsPreserved = originalNetwork != null && finalNetwork != null && HvacPortsPreserved(originalNetwork, finalNetwork);
                        var peerPortsPreserved = samePeers && sourcePeerByUid.Count > 0 && sourcePeerByUid.All(pair =>
                            HvacPortsPreserved(pair.Value, actualPeerByUid[pair.Key])
                            && (string.IsNullOrWhiteSpace((string)pair.Value["SystemTypeUid"])
                                || (string)pair.Value["SystemTypeUid"] == (string)actualPeerByUid[pair.Key]["SystemTypeUid"]));
                        var finalPeerTypes = (finalPeers ?? new List<JObject>()).SelectMany(HvacPorts)
                            .Select(port => (int?)port["SystemTypeId"]).Where(value => value.HasValue && value.Value > 0)
                            .Select(value => value.Value).Distinct().ToList();
                        var warnedNormalization = originalNetwork != null && (!systemTypeId.HasValue || systemTypeId.Value <= 0)
                            && (bool?)originalNetwork["MEPSystemMissing"] == true && sourcePortsPreserved && peerPortsPreserved
                            && finalPeerTypes.Count == 1 && actualSystemTypeId == finalPeerTypes[0]
                            && restored.ConnectionsRestored == originalConnectionCount
                            && adjustment != null && !adjustment.SourceDefined && adjustment.ActualDefined
                            && adjustment.MatchesUniqueConnectedPeerType && !adjustment.ClassificationPreserved
                            && adjustment.RevitAssignedPreviouslyUndefinedType
                            && !string.IsNullOrWhiteSpace(adjustment.Note)
                            && item.Detail?.Contains(adjustment.Note) == true;
                        var expectedSources = originalNetwork != null || target.ForceBackup ? 1 : 0;
                        bool passed = restored.Failed == 0 && restored.Created == 1 && actual?.GetType().Name == sample.Class
                            && actual?.GetTypeId().GetIdValue() == originalType && positionMatches && (systemMatches || warnedNormalization)
                            && sourcesOpened == expectedSources && sourcesOwned
                            && (!target.ForceBackup || !string.IsNullOrWhiteSpace(item?.OpeningFallbackTrigger));
                        var outcome = JObject.FromObject(new {
                            Uid = uid, sample.Class, sample.Category, Passed = passed, OriginalRecipeKind = originalKind,
                            FaultInjectedMissingType = target.ForceBackup, OpeningBackupRemovedForDirectCheck = originalNetwork == null && !target.ForceBackup,
                            OriginalEndpointParameters = endpointParameters,
                            OriginalTypeId = originalType, ActualTypeId = actual?.GetTypeId().GetIdValue(),
                            OriginalTypeUid = originalTypeUid, ActualTypeUid = actual == null ? null : doc.GetElement(actual.GetTypeId())?.UniqueId,
                            ExpectedPoint = Coordinates(point), ActualPoint = Coordinates(actualPoint), PointDistance = pointDistance,
                            ExpectedCurve = endpoints?.Select(Coordinates).ToArray(),
                            ActualCurve = actualCurve == null ? null : new[] { Coordinates(actualCurve.GetEndPoint(0)), Coordinates(actualCurve.GetEndPoint(1)) },
                            CurveDistances = curveDistances, PositionMatches = positionMatches,
                            ExpectedMidpoint = Coordinates(midpoint), ActualMidpoint = Coordinates(actualMidpoint),
                            MidpointDistance = midpoint == null || actualMidpoint == null ? (double?)null : midpoint.DistanceTo(actualMidpoint),
                            OriginalSystemTypeId = systemTypeId, ActualSystemTypeId = actualSystemTypeId,
                            OriginalNetwork = originalNetwork, OriginalPeers = originalPeers,
                            AfterNativeCreationBeforeConnections = afterNativeCreation,
                            OriginalPhysicalConnections = originalPhysicalLinks,
                            FinalNetwork = finalNetwork,
                            HistoricalPeerSystemTypeIds = historicalPeerTypes, ExplicitRevitNormalizationVerified = warnedNormalization,
                            FinalPeerNetworks = finalPeers,
                            NormalizationCriteria = new {
                                SourceTypeUndefined = !systemTypeId.HasValue || systemTypeId.Value <= 0,
                                SourceMepSystemMissing = (bool?)originalNetwork?["MEPSystemMissing"],
                                OriginalPeerTypeUnique = historicalPeerTypes.Count == 1,
                                ActualMatchesOriginalPeerType = historicalPeerTypes.Count == 1 && actualSystemTypeId == historicalPeerTypes[0],
                                SamePhysicalPeerOwners = samePeers, SourceHvacPortsPreserved = sourcePortsPreserved,
                                PeerHvacPortsPreserved = peerPortsPreserved, FinalHvacPeerTypeUnique = finalPeerTypes.Count == 1,
                                HistoricalConnections = originalConnectionCount, RestoredConnections = restored.ConnectionsRestored,
                                AuditFound = adjustment != null, AuditMatchesUniqueConnectedPeer = adjustment?.MatchesUniqueConnectedPeerType,
                                AuditRevitAssignedUndefined = adjustment?.RevitAssignedPreviouslyUndefinedType,
                                DetailContainsExplicitNote = adjustment != null && !string.IsNullOrWhiteSpace(adjustment.Note)
                                    && item.Detail?.Contains(adjustment.Note) == true
                            },
                            SystemClassificationPreserved = systemMatches, SourcesOpened = sourcesOpened,
                            ExpectedSourcesOpened = expectedSources, SourcesOwned = sourcesOwned,
                            Milliseconds = watch.Elapsed.TotalMilliseconds, PersistedEvent = persistedEvent,
                            RequestAtRestore = requestAtRestore, Restore = restored
                        });
                        outcomes.Add(outcome);
                        report["TargetedFallbackRegressions"] = outcomes;
                        save("target-values-captured-before-unset-probe");
                        // A transaction rollback invalidates native geometry wrappers.
                        // Persist plain values before the optional setter diagnostic.
                        if (originalNetwork != null && actual != null)
                        {
                            try { outcome["UnsetSystemDiagnosticRollback"] = ProbeUndefinedSystemRollback(doc, actual); }
                            catch (Exception ex)
                            {
                                outcome["UnsetSystemDiagnosticError"] = ex.ToString();
                            }
                        }
                        outcome["Passed"] = passed;
                        if (!passed) failed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        outcomes.Add(new { Uid = uid, Passed = false, Error = ex.ToString() });
                    }
                    finally { group.RollBack(); }
                }
                Check(doc.GetElement(uid) != null && new FilteredElementCollector(doc).GetElementCount() == originalCount,
                    "Target regression rollback changed model elements");
                report["TargetedFallbackRegressions"] = outcomes;
                save("checking-exact-fallback-regressions");
            }
            results.Add(failed == 0
                ? "exact Antibes regressions pass: IPN direct without backup, isolated missing-type fault uses one native backup, undefined duct classification is preserved or explicitly audited against its unique original peer system; all geometry/rollback checks pass"
                : "FAILED: " + failed + " exact Antibes fallback regressions; see TargetedFallbackRegressions");
        }

        private void RunTargetsOnly(Document doc, Assembly production, Dictionary<string, object> report,
            List<string> results, Action<string> save)
        {
            report["Measurement"] = "Targeted diagnostics only; full model/scheduler checks are recorded in the unchanged-production v6 run";
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var opening = production.GetType("Analyse.ElementHistoryOpeningArchive", true);
            var timer = Stopwatch.StartNew();
            opening.GetMethod("Schedule", flags).Invoke(null, new object[] { doc });
            ((System.Threading.Tasks.Task)opening.GetMethod("GetCopyTask", flags).Invoke(null, new object[] { doc })).GetAwaiter().GetResult();
            report["OpeningCopyWallMilliseconds"] = timer.Elapsed.TotalMilliseconds;
            var tracker = production.GetType("Analyse.ElementHistoryTracker", true);
            var getCache = tracker.GetMethod("GetTypeParameterCache", flags);
            var cache = getCache.Invoke(null, new object[] { doc });
            var build = tracker.GetMethod("BuildSnapshot", flags);
            var prime = tracker.GetMethod("PrimeElementSnapshot", flags);
            var captured = new List<OpeningSample>();
            foreach (var uid in new[] { "ee974953-383f-4838-bbc8-67cddf0cf5e4-000939c6", "2f0d3229-e77f-4d56-8aca-0a52aee986c1-000f6c7f" })
            {
                var element = doc.GetElement(uid);
                Check(element != null, "Exact target missing from workspace copy");
                var snapshot = JObject.FromObject(build.Invoke(null, new object[] { element, false, 0, false, cache }));
                Check((int)prime.Invoke(null, new object[] { element, "preload", cache }) > 0,
                    "Production target snapshot was not stored");
                captured.Add(new OpeningSample {
                    Uid = uid, Class = element.GetType().Name, Category = element.Category?.Name,
                    Label = element.Name, TypeId = element.GetTypeId().GetIdValue(), Snapshot = snapshot
                });
            }
            save("target-snapshots-captured");
            VerifyTargetedOpeningFallbacks(doc, production, captured, report, results, save);
        }
    }
}
