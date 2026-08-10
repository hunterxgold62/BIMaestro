using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using DB = Autodesk.Revit.DB;

namespace Famille.Orbit3D
{
    /// <summary>
    /// Contrôleur de preview 3D : une seule fenêtre réutilisée (singleton).
    /// - 2023/2024 : fenêtre sur thread STA dédié (reste fluide pendant upgrade).
    /// - 2025+     : on privilégie également le STA pour conserver la même expérience,
    ///               avec un repli automatique sur le thread Revit si nécessaire.
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
        private static Dispatcher _uiDispatcher;        // dispatcher WPF principal (browser)

        public static event EventHandler<bool> PreviewVisibilityChanged;

        // ------------- Public API -------------

        public static void ShowPreview(UIApplication uiapp, string familyPath)
        {
            if (uiapp == null || string.IsNullOrWhiteSpace(familyPath) || !File.Exists(familyPath))
            {
                MessageBox.Show(UiLanguage.T("Fichier famille introuvable.", "Family file not found."), UiLanguage.T("Aperçu 3D", "3D Preview"),
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
                    _host.Window.ShowBusy(true, UiLanguage.T("Chargement de la famille…", "Loading family..."));
                    if (!_host.Window.IsVisible) _host.Window.Show();
                    RaisePreviewVisibilityChanged(_host.Window.IsVisible);
                    _host.Window.Activate();
                }
                catch { /* ignore */ }
            });

            // Flush visuel immédiat (utile si la fenêtre est hébergée sur le thread Revit)
            try { _host.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { })); } catch { }

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
                            _host.Window.ShowBusy(true, UiLanguage.T("Mise à niveau du fichier… Cela peut prendre du temps.", "Upgrading the file... This may take some time.")));
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
                        _host.Window.ShowBusy(true, UiLanguage.T("Aucune géométrie affichable trouvée.", "No displayable geometry was found."));
                    else
                        _host.Window.LoadMeshes(meshes);
                });
            }
            catch (Exception ex)
            {
                _host.Dispatcher.Invoke(() =>
                    _host.Window.ShowBusy(true, UiLanguage.T("Erreur : ", "Error: ") + ex.Message));
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

            if (_uiDispatcher == null || _uiDispatcher.HasShutdownStarted)
            {
                _uiDispatcher = Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
            }

            if (_revitMajor == 0)
                int.TryParse(uiapp.Application.VersionNumber, out _revitMajor);

            bool preferSta = true; // essaye toujours STA en premier (2023 → 2025+)

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
            if (preferSta)
            {
                _host = CreatePreviewWindowOnSta(_ownerHwnd);
                if (_host == null || !_host.IsAlive)
                    _host = CreatePreviewWindowOnRevitThread(_ownerHwnd);
            }
            else
            {
                _host = CreatePreviewWindowOnRevitThread(_ownerHwnd);
            }
        }

        private static void RaisePreviewVisibilityChanged(bool isVisible)
        {
            var dispatcher = _uiDispatcher;

            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        try { PreviewVisibilityChanged?.Invoke(null, isVisible); }
                        catch { }
                    }));
                }
                catch
                {
                    // en dernier recours (dispatcher arrêté ?)
                    try { PreviewVisibilityChanged?.Invoke(null, isVisible); }
                    catch { }
                }
                return;
            }

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
