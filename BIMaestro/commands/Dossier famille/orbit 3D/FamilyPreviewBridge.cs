using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using DB = Autodesk.Revit.DB;

namespace Famille.Orbit3D
{
    /// <summary>
    /// Contrôleur de preview 3D : une seule fenêtre réutilisée (singleton).
    /// - 2023/2024 : fenêtre sur thread STA dédié (reste fluide pendant upgrade).
    /// - 2025+     : fenêtre sur thread Revit (plus robuste sur certaines configs).
    /// La fenêtre n'est jamais réellement fermée : on la cache et on réutilise.
    /// </summary>
    public static class FamilyPreviewBridge
    {
        private sealed class PreviewHost
        {
            public Family3DPreviewWindow Window;
            public Dispatcher Dispatcher;
            public bool IsStaThreadWindow;
            public bool IsAlive => Window != null && Dispatcher != null;
        }

        private static PreviewHost _host;               // singleton (multi-ouvertures sans crash)
        private static int _revitMajor;                 // cache version
        private static IntPtr _ownerHwnd = IntPtr.Zero; // owner Revit

        public static event EventHandler<bool> PreviewVisibilityChanged;

        // ------------- Public API -------------

        public static void ShowPreview(UIApplication uiapp, string familyPath)
        {
            if (uiapp == null || string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
            {
                MessageBox.Show("Fichier famille introuvable.", "Aperçu 3D",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnsureHost(uiapp);

            // Afficher "Chargement…" et (ré)afficher la fenêtre
            _host.Dispatcher.Invoke(() =>
            {
                try
                {
                    _host.Window.ClearScene(); // nettoie l'ancien contenu si besoin
                    _host.Window.ShowBusy(true, "Chargement de la famille…");
                    if (!_host.Window.IsVisible) _host.Window.Show();
                    RaisePreviewVisibilityChanged(_host.Window.IsVisible);
                    _host.Window.Activate();
                }
                catch { /* ignore */ }
            });

            DB.Document famDoc = null;

            try
            {
                var app = uiapp.Application;

                // Indication d'upgrade si versions différentes
                try
                {
                    var info = DB.BasicFileInfo.Extract(familyPath);
                    if (info != null && info.Format != app.VersionNumber)
                    {
                        _host.Dispatcher.Invoke(() =>
                            _host.Window.ShowBusy(true, "Mise à niveau du fichier… Cela peut prendre du temps."));
                    }
                }
                catch { /* non bloquant */ }

                // Ouvrir la famille (obligatoirement sur thread Revit)
                famDoc = app.OpenDocumentFile(familyPath);

                // Extraction maillages
                var meshes = FamilyMeshExtractor.ExtractFromFamilyDoc(famDoc);

                // Freeze (thread-safe) avant cross-dispatch
                if (meshes != null)
                    foreach (var m in meshes) m.MakeThreadSafe();

                // Affichage
                _host.Dispatcher.Invoke(() =>
                {
                    if (meshes == null || meshes.Count == 0)
                        _host.Window.ShowBusy(true, "Aucune géométrie affichable trouvée.");
                    else
                        _host.Window.LoadMeshes(meshes);
                });
            }
            catch (Exception ex)
            {
                _host.Dispatcher.Invoke(() =>
                    _host.Window.ShowBusy(true, "Erreur : " + ex.Message));
            }
            finally
            {
                try { famDoc?.Close(false); } catch { }
            }
        }

        // ------------- Host management -------------

        private static void EnsureHost(UIApplication uiapp)
        {
            _ownerHwnd = uiapp.MainWindowHandle;

            if (_revitMajor == 0)
                int.TryParse(uiapp.Application.VersionNumber, out _revitMajor);

            bool preferSta = _revitMajor <= 2024;

            // Si host existant et vivant → ok
            if (_host != null && _host.IsAlive)
            {
                // s'assurer que l'owner est à jour (cas multi-sessions)
                _host.Dispatcher.Invoke(() =>
                {
                    try { new WindowInteropHelper(_host.Window) { Owner = _ownerHwnd }; } catch { }
                });
                return;
            }

            // Sinon créer un nouveau host
            _host = preferSta
                ? CreatePreviewWindowOnSta(_ownerHwnd)
                : CreatePreviewWindowOnRevitThread(_ownerHwnd);

            // Si STA a échoué (rare), fallback sur Revit thread
            if (_host == null || !_host.IsAlive)
                _host = CreatePreviewWindowOnRevitThread(_ownerHwnd);
        }

        private static void RaisePreviewVisibilityChanged(bool isVisible)
        {
            try { PreviewVisibilityChanged?.Invoke(null, isVisible); }
            catch { }
        }

        private static PreviewHost CreatePreviewWindowOnSta(IntPtr ownerHwnd)
        {
            var host = new PreviewHost { IsStaThreadWindow = true };
            var ready = new AutoResetEvent(false);

            var t = new Thread(() =>
            {
                try
                {
                    var win = new Family3DPreviewWindow();
                    new WindowInteropHelper(win) { Owner = ownerHwnd };

                    // cache/fermeture → la fenêtre se cache au lieu de Close (géré dans xaml.cs)
                    win.Closed += (s, e) =>
                    {
                        try { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } catch { }
                    };

                    host.Window = win;
                    host.Dispatcher = win.Dispatcher;

                    win.IsVisibleChanged += (s, e) => RaisePreviewVisibilityChanged(win.IsVisible);

                    win.Show();               // modeless
                    RaisePreviewVisibilityChanged(win.IsVisible);
                    ready.Set();              // signal prêt
                    Dispatcher.Run();         // boucle WPF du thread
                }
                catch
                {
                    ready.Set();
                }
            });

            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();

            ready.WaitOne();
            return host;
        }

        private static PreviewHost CreatePreviewWindowOnRevitThread(IntPtr ownerHwnd)
        {
            var host = new PreviewHost { IsStaThreadWindow = false };
            try
            {
                var win = new Family3DPreviewWindow();
                new WindowInteropHelper(win) { Owner = ownerHwnd };
                win.Show();
                win.IsVisibleChanged += (s, e) => RaisePreviewVisibilityChanged(win.IsVisible);
                RaisePreviewVisibilityChanged(win.IsVisible);
                // flush visuel pour que "Chargement…" apparaisse même si Revit va mouliner ensuite
                win.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                host.Window = win;
                host.Dispatcher = win.Dispatcher;
                return host;
            }
            catch
            {
                return null;
            }
        }
    }
}
