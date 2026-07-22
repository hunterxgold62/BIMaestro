using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace BIMaestro.Dashboard
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimeDashboard : BaseTrackedCommand
    {
        private static readonly object _clickLock = new object();
        private static int _clickCount = 0;
        private static DateTime _lastClickTime = DateTime.MinValue;
        private const int DoubleClickThresholdMs = 300;

        protected override string ButtonId => "ShowTimeDashboard";

        protected override Result OnExecute(ExternalCommandData cdata, ref string message, ElementSet elements)
        {
            try
            {
                string activePath = cdata.Application?.ActiveUIDocument?.Document?.PathName;

                DateTime now = DateTime.Now;
                lock (_clickLock)
                {
                    _clickCount++;

                    if ((now - _lastClickTime).TotalMilliseconds <= DoubleClickThresholdMs && _clickCount >= 2)
                    {
                        _clickCount = 0;
                        OpenDocumentLocation(activePath);
                        return Result.Succeeded;
                    }

                    _lastClickTime = now;

                    Task.Delay(DoubleClickThresholdMs).ContinueWith(_ =>
                    {
                        lock (_clickLock)
                        {
                            if (_clickCount != 1)
                            {
                                return;
                            }

                            _clickCount = 0;
                        }

                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            new TimeSeriesDashboardWindow(activePath).Show();
                        });
                    });
                }

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
