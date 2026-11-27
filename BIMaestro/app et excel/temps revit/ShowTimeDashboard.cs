using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : BaseTrackedCommand
    {
        private static DispatcherTimer _pendingOpen;
        private static TimeSeriesDashboardWindow _window;
        private const int DoubleClickThresholdMs = 300;

        protected override string ButtonId => "ShowTimeDashboard";

        protected override Result OnExecute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try
            {
                var document = cdata.Application?.ActiveUIDocument?.Document;
                string activePath = document?.PathName;

                if (_pendingOpen != null)
                {
                    _pendingOpen.Stop();
                    _pendingOpen = null;
                    string localPath = GetExplorerPath(activePath);
                    string centralPath = GetExplorerPath(TimeLogMetadataExtractor.TryGetCentralPath(document));
                    OpenDocumentLocation(localPath);
                    if (!string.Equals(centralPath, localPath, StringComparison.OrdinalIgnoreCase))
                        OpenDocumentLocation(centralPath);
                    return Result.Succeeded;
                }

                // Revit may have no WPF Application.Current. Use its command UI thread.
                var owner = cdata.Application.MainWindowHandle;
                var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(DoubleClickThresholdMs)
                };
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();
                    _pendingOpen = null;
                    try
                    {
                        if (_window == null)
                        {
                            var window = new TimeSeriesDashboardWindow(activePath);
                            new WindowInteropHelper(window).Owner = owner;
                            window.Closed += (s, e) => _window = null;
                            window.Show();
                            _window = window;
                        }
                        if (_window.WindowState == System.Windows.WindowState.Minimized)
                            _window.WindowState = System.Windows.WindowState.Normal;
                        _window.Activate();
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show(global::BIMaestro.Localization.UiLanguage.Get("UiDialog.ProjectTime"), ex.ToString());
                    }
                };
                _pendingOpen = timer;
                timer.Start();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Dashboard", ex.ToString());
                return Result.Failed;
            }
        }

        private static string GetExplorerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static void OpenDocumentLocation(string fullPath)
        {
            if (fullPath == null) return;

            try
            {
                if (File.Exists(fullPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
                    return;
                }

                string dir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }
            }
            catch
            {
                // Ne jamais bloquer la commande si l'ouverture du dossier échoue.
            }
        }
    }
}
