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
        private const int DoubleClickThresholdMs = 300;

        protected override string ButtonId => "ShowTimeDashboard";

        protected override Result OnExecute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try
            {
                string activePath = cdata.Application?.ActiveUIDocument?.Document?.PathName;

                if (_pendingOpen != null)
                {
                    _pendingOpen.Stop();
                    _pendingOpen = null;
                    OpenDocumentLocation(activePath);
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
                        var window = new TimeSeriesDashboardWindow(activePath);
                        new WindowInteropHelper(window).Owner = owner;
                        window.Show();
                        window.Activate();
                    }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("Temps par projet", ex.ToString());
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

        private static void OpenDocumentLocation(string activePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(activePath)) return;

                string fullPath = Path.GetFullPath(activePath);
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
