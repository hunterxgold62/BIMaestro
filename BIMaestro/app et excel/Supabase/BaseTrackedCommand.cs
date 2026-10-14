using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.UI;
using Page;
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB.Events;

namespace Licensing
{
    internal static class OperationTelemetry
    {
        internal static void Track(string toolId, string label, string mode, bool tutorial,
            IDictionary<string, int> counts)
        {
            Telemetry.TrackButton(label, true, new
            {
                command_id = toolId, event_kind = "operation_result",
                operation_result = new {
                    schema_version = 1, operation_id = Guid.NewGuid().ToString("D"),
                    tool_id = toolId, mode, is_tutorial = tutorial, committed = true, counts
                }
            });
        }
        internal static void Observe(UIApplication app, string toolId, string label,
            string mode, bool tutorial, Action action)
        {
            using (var measurement = new CommandChangeMeasurement(app))
            {
                try { action(); }
                finally
                {
                    var counts = measurement.Counts();
                    if (counts.Values.Any(count => count > 0)) Track(toolId, label, mode, tutorial, counts);
                }
            }
        }
    }

    // Only observes transactions during this command's API execution. Never
    // attributes later user edits to a palette that merely remains open.
    internal sealed class CommandChangeMeasurement : IDisposable
    {
        private sealed class Changes
        {
            internal readonly HashSet<string> Added = new HashSet<string>();
            internal readonly HashSet<string> Modified = new HashSet<string>();
            internal readonly HashSet<string> Deleted = new HashSet<string>();
        }
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly UIApplication _uiApplication;
        private readonly Document _initialDocument;
        private readonly HashSet<string> _initialSelection = new HashSet<string>();
        private readonly Dictionary<Document, Changes> _documents = new Dictionary<Document, Changes>();
        private bool _valid = true;
        internal CommandChangeMeasurement(UIApplication app)
        {
            _uiApplication = app;
            _initialDocument = app?.ActiveUIDocument?.Document;
            if (app?.ActiveUIDocument != null)
                foreach (ElementId id in app.ActiveUIDocument.Selection.GetElementIds()) _initialSelection.Add(id.ToString());
            _application = app?.Application;
            try { if (_application != null) _application.DocumentChanged += Changed; }
            catch { _valid = false; }
        }
        private void Changed(object sender, DocumentChangedEventArgs args)
        {
            try
            {
                if (args.Operation != UndoOperation.TransactionCommitted)
                {
                    // A transaction-group rollback invalidates intermediate commits.
                    _valid = false;
                    return;
                }
                Document doc = args.GetDocument();
                if (!_documents.TryGetValue(doc, out Changes changes))
                    _documents[doc] = changes = new Changes();
                foreach (ElementId id in args.GetAddedElementIds()) changes.Added.Add(id.ToString());
                foreach (ElementId id in args.GetModifiedElementIds()) changes.Modified.Add(id.ToString());
                foreach (ElementId id in args.GetDeletedElementIds())
                {
                    string key = id.ToString();
                    if (!changes.Added.Remove(key)) changes.Deleted.Add(key);
                    changes.Modified.Remove(key);
                }
            }
            catch { _valid = false; }
        }
        internal Dictionary<string, int> Counts()
        {
            if (!_valid) return new Dictionary<string, int>();
            int selected = 0;
            try
            {
                if (_initialDocument?.IsValidObject == true && _initialDocument.Equals(_uiApplication?.ActiveUIDocument?.Document))
                {
                    var finalSelection = new HashSet<string>(_uiApplication.ActiveUIDocument.Selection.GetElementIds().Select(id => id.ToString()));
                    if (!_initialSelection.SetEquals(finalSelection)) selected = finalSelection.Count;
                }
            }
            catch { }
            return new Dictionary<string, int>
            {
                ["elements_selected"] = selected,
                ["revit_elements_created"] = _documents.Values.Sum(c => c.Added.Count),
                ["revit_elements_modified"] = _documents.Values.Sum(c => c.Modified.Except(c.Added).Except(c.Deleted).Count()),
                ["revit_elements_deleted"] = _documents.Values.Sum(c => c.Deleted.Count)
            };
        }
        public void Dispose()
        {
            if (_application != null) _application.DocumentChanged -= Changed;
        }
    }

    /// <summary>
    /// À hériter pour tracer automatiquement (succès/échec + contexte)
    /// + hook "Welcome" après premier usage.
    /// </summary>
    public abstract class BaseTrackedCommand : IExternalCommand
    {
        protected abstract string ButtonId { get; }
        private object _operationResult;
        private string _commandStatus;
        private string _usageId;

        // Attach one aggregate result to the existing usage event, after commit.
        protected void RecordOperationResult(string toolId, string mode, bool isTutorial,
            IDictionary<string, int> counts)
        {
            _operationResult = new
            {
                schema_version = 1,
                operation_id = Guid.NewGuid().ToString("D"),
                tool_id = toolId,
                mode,
                is_tutorial = isTutorial,
                committed = true,
                counts
            };
        }

        protected virtual object BuildContext(ExternalCommandData data)
        {
            var doc = data.Application?.ActiveUIDocument?.Document;
            var view = data.Application?.ActiveUIDocument?.ActiveView;
            return new
            {
                doc_title = doc?.Title ?? "",
                view = view?.Name ?? "",
                view_type = view?.ViewType.ToString() ?? "",
                revit_username = data.Application?.Application?.Username ?? Environment.UserName,
                windows_user = Environment.UserName,
                revit_version = data.Application?.Application?.VersionNumber,
                command_id = ButtonId,
                event_kind = "command_usage",
                command_status = _commandStatus,
                usage_id = _usageId,
                operation_result = _operationResult
            };
        }

        protected abstract Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements);

        private string ResolveButtonDisplayName()
        {
            var fullName = GetType().FullName;
            var info = AppUI.GetRibbonButtonByCommandClass(fullName);
            if (!string.IsNullOrWhiteSpace(info?.DisplayName))
            {
                return NormalizeLabel(info.DisplayName);
            }

            return NormalizeLabel(ButtonId);
        }

        private static string NormalizeLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return string.Join(" ", value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            _operationResult = null;
            _commandStatus = "unknown";
            _usageId = Guid.NewGuid().ToString("D");
            CommandChangeMeasurement measurement = null;
            // ✅ Hook global : au tout premier bouton BIMaestro, start timer (2 min), puis popup.
            // Le JWT (si dispo) permet ensuite de sync l'email (opt-in) vers Supabase.
            try
            {
                BIMaestro.Welcome.WelcomeManager.NotifyFirstCommandUsed(
                    data.Application,
                    Licensing.LicenseSession.CurrentJwt
                );
            }
            catch
            {
                // Jamais bloquer une commande pour ça
            }

            try
            {
                AppUI.SetUiApplication(data.Application);
            }
            catch
            {
                // Non bloquant
            }

            try
            {
                var trackedButtonLabel = ResolveButtonDisplayName();
                ButtonUpdateNotesManager.TryShowButtonNotesBeforeLaunch(ButtonId, GetType().FullName, data.Application);
                measurement = new CommandChangeMeasurement(data.Application);
                var res = OnExecute(data, ref message, elements);
                _commandStatus = res == Result.Succeeded ? "succeeded" : res == Result.Cancelled ? "cancelled" : "failed";
                CaptureGenericResult(measurement);
                Telemetry.TrackButton(trackedButtonLabel, res == Result.Succeeded, BuildContext(data));
                if (res == Result.Succeeded)
                {
                    ButtonRecentManager.RegisterUse(trackedButtonLabel, GetType().FullName);
                    SupportPromptManager.RegisterSuccessfulUse(ButtonId);
                }
                return res;
            }
            catch (Exception ex)
            {
                CaptureGenericResult(measurement);
                var trackedButtonLabel = ResolveButtonDisplayName();
                Telemetry.TrackButton(trackedButtonLabel, false, new { error = ex.GetType().Name, ex.Message,
                    command_id = ButtonId, event_kind = "command_usage", command_status = "failed", usage_id = _usageId,
                    operation_result = _operationResult });
                message = ex.Message;
                IssueReporter.ShowCommandError(trackedButtonLabel, ex, data);
                return Result.Failed;
            }
            finally { measurement?.Dispose(); }
        }

        private void CaptureGenericResult(CommandChangeMeasurement measurement)
        {
            if (_operationResult != null || measurement == null) return;
            var counts = measurement.Counts();
            if (counts.Values.Any(count => count > 0))
                RecordOperationResult(ButtonId, "revit_transactions",
                    GetType().FullName?.StartsWith("BIMaestro.Tutorials.", StringComparison.Ordinal) == true, counts);
        }
    }
}
