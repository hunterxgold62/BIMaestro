using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using BIMaestro.Localization;

namespace BIMaestro.VideoGames
{
    internal sealed class GameReservationImport : IExternalEventHandler, IDisposable
    {
        private const string ApplicationId = "BIMaestro.WebReservations";
        private readonly ExternalEvent _event;
        private string? _path;
        public GameReservationImport() { _event = ExternalEvent.Create(this); }
        public void PickFile()
        {
            if (_event.IsPending) return;
            var dialog = new OpenFileDialog { Title = "Importer les volumes de réservation web", Filter = "Réservations BIMaestro (*.bimaestro-reservations.json)|*.bimaestro-reservations.json" };
            if (dialog.ShowDialog() != true) return;
            _path = dialog.FileName;
            if (_event.Raise() != ExternalEventRequest.Accepted) TaskDialog.Show(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.WebOpenings"), global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.RevitIsBusyTryImportingAgain"));
        }
        public string GetName() => "Importer les réservations web BIMaestro";
        public void Dispose() { if (!_event.IsPending) _event.Dispose(); }
        public void Execute(UIApplication application)
        {
            try
            {
                if (_path == null) return;
                var document = application.ActiveUIDocument?.Document;
                if (document == null || document.IsFamilyDocument || document.IsReadOnly) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.OpenTheOriginalRevitProjectInAnEditable"));
                if (new FileInfo(_path).Length > 10 * 1024 * 1024) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.FileTooLarge"));
                var data = JObject.Parse(File.ReadAllText(_path)); _path = null;
                if ((int?)data["schemaVersion"] != 1 || (string?)data["kind"] != "bimaestro-reservations" || (string?)data["units"] != "revit-internal-feet")
                    throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.UnrecognizedOpeningsFileFormat"));
                if ((string?)data["sourceDocumentId"] != document.ProjectInformation.UniqueId)
                    throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.ThisFileComesFromAnotherProjectOpenThe"));
                if (!Guid.TryParse((string?)data["publicationId"], out var publicationId)) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.InvalidPublication"));
                var reservations = data["reservations"] as JArray;
                if (reservations == null || reservations.Count == 0 || reservations.Count > 500) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.TheFileMustContainBetween1And500"));
                var seen = new HashSet<string>();
                // Validate and construct every solid before starting the Revit transaction.
                var prepared = new List<Tuple<string, Solid, string>>();
                foreach (var mark in reservations)
                {
                    if (!Guid.TryParse((string?)mark["id"], out var id) || !seen.Add(id.ToString())) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.InvalidOrDuplicateOpeningID"));
                    XYZ position = Vector(mark["position"]), normal = Vector(mark["normal"]), x = Vector(mark["xAxis"]);
                    if (Math.Abs(normal.GetLength() - 1) > .001 || Math.Abs(x.GetLength() - 1) > .001 || Math.Abs(normal.DotProduct(x)) > .001) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.InvalidOpeningOrientation"));
                    var y = normal.CrossProduct(x).Normalize();
                    double width = Dimension(mark["widthCm"]), height = Dimension(mark["heightCm"]), depth = Dimension(mark["depthCm"]);
                    var points = new[] { position - x * width / 2 - y * height / 2, position + x * width / 2 - y * height / 2,
                        position + x * width / 2 + y * height / 2, position - x * width / 2 + y * height / 2 };
                    var loop = new CurveLoop();
                    string form = (string?)mark["shape"] ?? "rectangle";
                    if (form != "rectangle" && form != "round") throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.UnrecognizedOpeningShape"));
                    if (form == "round")
                    {
                        double radius = Dimension(mark["diameterCm"]) / 2;
                        loop.Append(Arc.Create(position, radius, 0, Math.PI, x, y));
                        loop.Append(Arc.Create(position, radius, Math.PI, Math.PI * 2, x, y));
                    }
                    else for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
                    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, -normal, depth);
                    string lot = (string?)mark["lot"] ?? "MEP";
                    string dimensions = form == "round" ? "Ø " + mark["diameterCm"] : mark["widthCm"] + " × " + mark["heightCm"];
                    string description = "Réservation web · Lot " + lot + " · " + dimensions + " × " + mark["depthCm"] + " cm · " + (string?)mark["elementName"] + " · " + (string?)mark["text"];
                    prepared.Add(Tuple.Create(publicationId + "|" + id, solid, description));
                }
                var existing = new FilteredElementCollector(document).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                    .Where(shape => shape.ApplicationId == ApplicationId).GroupBy(shape => shape.ApplicationDataId).ToDictionary(group => group.Key, group => group.ToList());
                int created = 0, updated = 0;
                using (var transaction = new Transaction(document, "Importer les volumes de réservation web"))
                {
                    transaction.Start();
                    foreach (var item in prepared)
                    {
                        DirectShape shape;
                        if (existing.TryGetValue(item.Item1, out var matches))
                        {
                            if (matches.Count != 1) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.SeveralRevitObjectsShareTheSameOpeningID"));
                            shape = matches[0]; updated++;
                        }
                        else { shape = DirectShape.CreateElement(document, new ElementId(BuiltInCategory.OST_GenericModel)); created++; }
                        shape.ApplicationId = ApplicationId; shape.ApplicationDataId = item.Item1;
                        shape.Name = "BIMaestro — Réservation web";
                        shape.SetShape(new GeometryObject[] { item.Item2 });
                        var comment = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (comment != null && !comment.IsReadOnly) comment.Set(item.Item3);
                    }
                    if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.RevitDidNotCommitTheImport"));
                }
                TaskDialog.Show(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.WebOpenings"), global::BIMaestro.Localization.UiLanguage.Format("GameReservationImport.0VolumeSCreated1UpdatedTheVolumes", (object)(created), (object)(updated)));
            }
            catch (Exception exception) { TaskDialog.Show(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.ImportOpenings"), exception.Message); }
        }
        private static XYZ Vector(JToken? token)
        {
            if (!(token is JArray array) || array.Count != 3) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.InvalidCoordinates"));
            var values = array.Select(value => (double)value).ToArray();
            if (values.Any(value => double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e8)) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.CoordinatesOutOfRange"));
            return new XYZ(values[0], values[1], values[2]);
        }
        private static double Dimension(JToken? token)
        {
            double cm = token == null ? 0 : (double)token;
            if (double.IsNaN(cm) || double.IsInfinity(cm) || cm < 1 || cm > 1000) throw new InvalidOperationException(global::BIMaestro.Localization.UiLanguage.Get("GameReservationImport.DimensionsOutOfRange1To1000Cm"));
            return cm / 30.48;
        }
    }
}
