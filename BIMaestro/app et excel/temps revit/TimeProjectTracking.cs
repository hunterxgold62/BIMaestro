using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using BIMaestro.Localization;

namespace BIMaestro.Dashboard
{
    internal static class TimeProjectTracking
    {
        internal static string RegistryPath => Path.Combine(BIMaestro.Storage.UserDataStorage.Root, "TimeTracking.xml");
        private static bool _showing;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        internal static string LastError { get; private set; }
        internal static void Observe(WorkSession session)
        {
            if (session == null) return;
            string identity = session.Classification?.DocumentIdentity;
            string file = session.DocumentKey;
            if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(session.Classification?.LocalPath)
                && string.IsNullOrWhiteSpace(session.Classification?.CentralPath)) return;
            string key = identity + "|" + TimeTrackingRegistry.CanonicalPath(file);
            if (session.TrackingLocation == key) return;
            try
            {
                TimeTrackingRegistry.Observe(RegistryPath, identity, file, session.DocumentName,
                    TimeLogMetadataExtractor.GetOlderDocumentIdentity(session.LastKnownDocument));
                LastError = null;
                session.TrackingLocation = key;
                session.TrackingPromptHandled = false;
            }
            catch (Exception ex)
            {
                LastError = UiLanguage.Get("TimeTracking.SaveError") + ex.Message;
                session.TrackingRetryUtc = DateTime.UtcNow.AddMinutes(1);
                Debug.WriteLine(LastError);
            }
        }
        internal static void Process(WorkSession session, UIApplication application)
        {
            if (_showing || session == null || application == null || session.TrackingRetryUtc > DateTime.UtcNow) return;
            string identity = session.Classification?.DocumentIdentity;
            string file = session.DocumentKey;
            // Unsaved documents have no durable location yet. Observe them after their first save.
            if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(session.Classification?.LocalPath)
                && string.IsNullOrWhiteSpace(session.Classification?.CentralPath)) return;
            bool resume = false;
            try
            {
                Observe(session);
                if (session.TrackingLocation == null) return;
                if (session.TrackingPromptHandled) return;
                var registry = TimeTrackingRegistry.Load(RegistryPath);
                var location = registry.Find(identity, file);
                if (location == null || !location.Pending) { session.TrackingPromptHandled = true; return; }
                // Wait for the foreground document and an idle Revit window; never show from DocumentOpened.
                GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
                if (application.ActiveUIDocument?.Document?.Equals(session.LastKnownDocument) != true
                    || foregroundProcess != (uint)System.Diagnostics.Process.GetCurrentProcess().Id || !IsWindowEnabled(application.MainWindowHandle)
                    || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
                var candidates = registry.Candidates(location);
                if (candidates.Count == 0) return;
                var history = ExcelLogger.ReadTrackingHistory().Concat(ExcelLogger.GetDashboardEntries()).ToList();
                var manual = TimeProjectGroups.Load(Path.Combine(BIMaestro.Storage.UserDataStorage.Root, "TimeProjectGroups.xml"));
                var choices = candidates.Select(x => new TimeTrackingDecisionWindow.ProjectChoice
                {
                    ProjectId = x.Project, Name = x.Name, Path = x.Path,
                    Hours = history.Where(row => manual.Resolve(registry.Resolve(row.DocumentIdentity, row.DocumentId)) == manual.Resolve(x.Project)).Sum(row => row.Hours)
                }).ToList();
                double pendingHours = history.Where(row => registry.Resolve(row.DocumentIdentity, row.DocumentId) == location.PendingId).Sum(row => row.Hours);
                _showing = true;
                // One question per opening. Closing it means later; the rows and pending location remain durable.
                session.TrackingPromptHandled = true;
                resume = session.IsActivelyCounting;
                session.Pause();
                var window = new TimeTrackingDecisionWindow(choices, file, pendingHours);
                new WindowInteropHelper(window).Owner = application.MainWindowHandle;
                window.ShowDialog();
                if (window.Choice != TimeTrackingDecisionWindow.Decision.Later)
                    TimeTrackingRegistry.Decide(RegistryPath, identity, file, window.SelectedProjectId,
                        window.Choice == TimeTrackingDecisionWindow.Decision.NewProject);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = UiLanguage.Get("TimeTracking.SaveError") + ex.Message;
                Debug.WriteLine(LastError);
                session.TrackingRetryUtc = DateTime.UtcNow.AddMinutes(1);
                // Never pretend a failed choice has been saved; report it once and offer again on next opening.
                if (_showing) MessageBox.Show(LastError, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (_showing)
                {
                    _showing = false;
                    if (resume && session.HasOpenSegment) session.Resume();
                }
            }
        }
    }
}
