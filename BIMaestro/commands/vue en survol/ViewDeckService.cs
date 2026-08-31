using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RevitView = Autodesk.Revit.DB.View;

namespace BIMaestro.ViewHover
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ToggleViewDeckCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "ViewDeckToggle";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (ViewDeckService.Toggle(data.Application, out string error)) return Result.Succeeded;
            TaskDialog.Show("BIMaestro - Onglets", error);
            return Result.Cancelled;
        }
    }

    /// <summary>
    /// One native tab per view: only its HeaderTemplate and size are decorated.
    /// Activation, closing, ordering, docking and context menus remain native.
    /// All Revit reads/exports run in a command/Idling context.
    /// </summary>
    internal static class ViewDeckService
    {
        private sealed class OpenView
        {
            internal Document Document;
            internal RevitView View;
            internal ViewDeckTabIdentity Identity;
        }

        private static readonly Dictionary<TabItem, ViewDeckTabPresentation> Tabs = new Dictionary<TabItem, ViewDeckTabPresentation>();
        private static readonly ViewDeckPreviewMemory<Document> PreviewMemory = new ViewDeckPreviewMemory<Document>();
        private static bool _enabled;
        private static bool _suspended;
        private static DateTime _nextRefreshUtc;
        private static DateTime _nextCaptureUtc;

        internal static bool Toggle(UIApplication app, out string error)
        {
            error = null;
            if (_enabled) { Disable(); return true; }
            if (app?.ActiveUIDocument == null)
            {
                error = UiLanguage.T("Ouvrez un document Revit avant d'activer les miniatures.",
                    "Open a Revit document before enabling thumbnails.");
                return false;
            }
            try
            {
                _suspended = false;
                _enabled = true;
                _nextRefreshUtc = DateTime.MinValue;
                _nextCaptureUtc = DateTime.UtcNow.AddSeconds(2);
                Refresh(app, false);
                if (!Tabs.Values.Any(tab => tab.IsExpanded))
                {
                    Disable();
                    error = UiLanguage.T("Les onglets de vues n'ont pas été trouvés. Repassez en vues à onglets (TW), puis réessayez.",
                        "View tabs were not found. Switch to tabbed views (TW), then retry.");
                    return false;
                }
                UpdateButton();
                return true;
            }
            catch (Exception ex)
            {
                Disable();
                error = UiLanguage.T("Impossible d'activer les miniatures : ", "Unable to enable thumbnails: ") + ex.Message;
                return false;
            }
        }

        internal static void ProcessIdling(UIApplication app)
        {
            if (_suspended || DateTime.UtcNow < _nextRefreshUtc) return;
            _nextRefreshUtc = DateTime.UtcNow.AddSeconds(1);
            try { Refresh(app, _enabled); } // OFF: cached hover only; no extra batch generation.
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("BIMaestro ViewDeck: " + ex);
                Disable();
                RestoreTabs();
                _suspended = true; // Don't repeatedly display an error every idle tick.
                TaskDialog.Show("BIMaestro - Onglets", UiLanguage.T(
                    "Les miniatures ont été désactivées après une erreur. Les onglets d'origine ont été restaurés.",
                    "Thumbnails were disabled after an error. The original tabs have been restored.") + "\n\n" + ex.Message);
            }
        }

        private static void Refresh(UIApplication app, bool allowCapture)
        {
            UIDocument activeDocument = app?.ActiveUIDocument;
            if (activeDocument == null) { RestoreTabs(); return; }
            DependencyObject root = HwndSource.FromHwnd(app.MainWindowHandle)?.RootVisual;
            var nativeTabs = new HashSet<TabItem>(FindDocumentTabs(root));
            foreach (TabItem removed in Tabs.Keys.Where(tab => !nativeTabs.Contains(tab)).ToList())
            {
                Tabs[removed].Dispose();
                Tabs.Remove(removed);
            }
            var views = ReadOpenViews(app);
            OpenView activeView = views.FirstOrDefault(item => item.Document.Equals(activeDocument.Document) &&
                item.View.Id == activeDocument.ActiveView?.Id);
            ViewDeckChangeService.Process(activeDocument.Document,
                views.Where(item => item.Document.Equals(activeDocument.Document)).Select(item => item.View),
                activeDocument.ActiveView);
            foreach (TabItem tab in nativeTabs)
            {
                if (!Tabs.TryGetValue(tab, out ViewDeckTabPresentation state))
                {
                    Tabs.Add(tab, state = new ViewDeckTabPresentation(tab));
                }
                object model = tab.Header;
                string title = ReadProperty(model, "Title") as string ?? string.Empty;
                var remembered = PreviewMemory.ForModel(model);
                if (remembered.Document != null && !remembered.Document.IsValidObject) remembered.Clear();
                // IsSelected is per pane; several tabs can be selected in tiled mode.
                // IsActive identifies AvalonDock's actually active document/view.
                OpenView match = ReadProperty(model, "IsActive") is bool isActive && isActive
                    ? activeView : null;
                if (match == null && remembered.Document != null)
                    match = views.FirstOrDefault(item => item.Document.Equals(remembered.Document) &&
                        item.View.UniqueId == remembered.ViewUniqueId);
                if (match == null)
                {
                    string tooltip = tab.ToolTip as string ?? (tab.ToolTip as ToolTip)?.Content as string;
                    match = ViewDeckTabIdentity.Resolve(title, tooltip, views, item => item.Identity);
                }
                if (match != null)
                    remembered.Remember(match.Document, match.View.UniqueId,
                        ViewHoverPreviewService.GetDeckPreviewPath(match.Document, match.View));
                // Keep the learned identity even if a transient UI refresh cannot
                // resolve it; OFF must not turn known native tabs into unknown ones.
                remembered.Preview.Refresh(remembered.PreviewPath);
                string placeholder = match == null ? UiLanguage.T("Activer pour l'aperçu", "Activate for preview") :
                    ViewHoverPreviewService.IsDeckPreviewUnavailable(match.Document, match.View)
                        ? UiLanguage.T("Aperçu indisponible", "Preview unavailable")
                        : UiLanguage.T("Aperçu en attente", "Preview pending");
                string signature = title + "|" + remembered.PreviewPath + "|" + remembered.Preview.Revision + "|" + placeholder;
                state.Update(_enabled, title, remembered.Preview.Image, placeholder, signature,
                    ViewDeckChangeService.GetCounts(match?.Document, match?.View));
            }

            if (!allowCapture || Tabs.Count == 0 || DateTime.UtcNow < _nextCaptureUtc) return;
            _nextCaptureUtc = DateTime.UtcNow.AddSeconds(2);
            foreach (OpenView view in views.Where(item => item.Document.Equals(activeDocument.Document)))
                if (ViewHoverPreviewService.TryCreateMissingDeckPreview(view.Document, view.View)) break;
        }

        private static List<OpenView> ReadOpenViews(UIApplication app)
        {
            var result = new List<OpenView>();
            foreach (Document document in app.Application.Documents)
            {
                if (!document.IsValidObject || document.IsLinked) continue;
                try
                {
                    using (var uiDocument = new UIDocument(document))
                        foreach (UIView window in uiDocument.GetOpenUIViews())
                        {
                            if (!(document.GetElement(window.ViewId) is RevitView view) || view.IsTemplate) continue;
                            string sheetTitle = view is ViewSheet sheet ? sheet.SheetNumber + " - " + view.Name : view.Name;
                            result.Add(new OpenView
                            {
                                Document = document, View = view,
                                Identity = new ViewDeckTabIdentity { DocumentTitle = document.Title, Titles = new[] { view.Name, sheetTitle } }
                            });
                        }
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* Background-only document. */ }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* No UI document. */ }
            }
            return result;
        }

        private static IEnumerable<TabItem> FindDocumentTabs(DependencyObject root)
        {
            if (root == null) yield break;
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            int visited = 0;
            while (pending.Count > 0 && visited++ < 25000)
            {
                DependencyObject current = pending.Pop();
                if (current is TabItem tab && tab.Header?.GetType().FullName ==
                    "Xceed.Wpf.AvalonDock.Layout.LayoutDocument")
                {
                    yield return tab;
                    continue; // Never traverse the view canvas/native document content.
                }
                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int index = count - 1; index >= 0; index--)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }

        private static object ReadProperty(object instance, string name)
        {
            try { return instance?.GetType().GetProperty(name)?.GetValue(instance, null); }
            catch { return null; }
        }

        private static void RestoreTabs()
        {
            foreach (ViewDeckTabPresentation tab in Tabs.Values) tab.Dispose();
            Tabs.Clear();
        }

        internal static void UpdateButton()
        {
            AppUI.UpdatePushButtonPresentation("ViewDeckToggle", _enabled ? "Onglets : ON" : "Onglets : OFF",
                UiLanguage.T("ON : miniatures dans les onglets. OFF : onglets compacts. Dans les deux modes, survolez un onglet 0,5 seconde pour afficher l'aperçu agrandi.",
                    "ON: inline thumbnails. OFF: compact tabs. In both modes, hover over a tab for 0.5 seconds to see the large preview."));
        }

        private static void Disable()
        {
            _enabled = false;
            foreach (ViewDeckTabPresentation tab in Tabs.Values) tab.SetExpanded(false);
            UpdateButton();
        }

        internal static void Shutdown()
        {
            Disable();
            _suspended = true;
            RestoreTabs();
            PreviewMemory.Clear(); // PNGs remain in the existing on-disk cache.
            ViewDeckChangeService.Clear();
        }
    }
}
