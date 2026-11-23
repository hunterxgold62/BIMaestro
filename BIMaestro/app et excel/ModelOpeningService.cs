using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using CellType = NPOI.SS.UserModel.CellType;
using FontFamily = System.Windows.Media.FontFamily;

namespace BIMaestro.ModelLoading
{
    // No Revit API calls on the UI worker. No image export or workbook read at opening.
    internal static class ModelOpeningService
    {
        internal sealed class Preview { public string Id, Name, Path, ViewType; public DateTime Seen; }
        internal sealed class Snapshot
        {
            public string Name, File;
            public DateTime LastOpened;
            public bool? IsWorkshared;
            public DateTime? LastSynchronized;
            public double? Hours, WeekHours, LastSessionHours;
            public List<Preview> Views = new List<Preview>();
            public List<string> Aliases = new List<string>();
        }
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMaestro", "Cache", "ModelOpening");
        private static readonly Dictionary<string, Snapshot> Models = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Snapshot> Catalog = new ConcurrentDictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        private static string _dataRoot;
        private static Task _bootstrap;
        private static readonly Queue<Document> PendingPreviewCatalogs = new Queue<Document>();
        private static Dispatcher _dispatcher;
        private static OpeningWindow _window;
        private static int _generation;
        private static int _tipGeneration = -1;
        private static ModelOpeningTips.Tip _openingTip;
        private static int _opening;
        private static bool _stopping;
        private static bool _databaseReady;
        private static string _requestedPath;
        private static IntPtr _hostMainWindow, _displayHandle;
        private static readonly object LogGate = new object();
        private static Task _logWrites = Task.CompletedTask;
        private static DateTime _nextSnapshot;
        private static Task _writes = Task.CompletedTask;

        public static void Initialize(UIControlledApplication app)
        {
            _dataRoot = Storage.UserDataStorage.Root;
            _hostMainWindow = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            _bootstrap = Task.Run(() => Bootstrap());
            var thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ModelOpeningTips.Initialize(Path.Combine(Root, "last-opening-tip.txt"));
                TraceOpening("dispatcher-ready");
                // Covers an open request received while this STA thread was still starting.
                int pending = _generation; string pendingPath = _requestedPath;
                if (_opening != 0 && !string.IsNullOrWhiteSpace(pendingPath))
                    _dispatcher.BeginInvoke(new Action(() => ShowOpening(pendingPath, pending)));
                Dispatcher.Run();
            }) { IsBackground = true, Name = "BIMaestro model opening" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            app.ControlledApplication.DocumentOpening += Opening;
            app.ControlledApplication.DocumentOpened += Opened;
            app.ControlledApplication.DocumentClosing += Closing;
            app.ControlledApplication.DocumentSynchronizedWithCentral += Synchronized;
            app.ViewActivated += Activated; app.Idling += Idle;
            app.DialogBoxShowing += DialogShowing;
        }
        public static void Shutdown(UIControlledApplication app)
        {
            app.ControlledApplication.DocumentOpening -= Opening;
            app.ControlledApplication.DocumentOpened -= Opened;
            app.ControlledApplication.DocumentClosing -= Closing;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= Synchronized;
            app.ViewActivated -= Activated; app.Idling -= Idle;
            app.DialogBoxShowing -= DialogShowing;
            _stopping = true; Interlocked.Increment(ref _generation);
            var d = _dispatcher;
            if (d != null && !d.HasShutdownStarted) d.BeginInvoke(new Action(() => { _window?.Close(); d.BeginInvokeShutdown(DispatcherPriority.Background); }));
        }
        private static void Opening(object sender, DocumentOpeningEventArgs e)
        {
            try
            {
                int generation = Interlocked.Increment(ref _generation);
                string path = e.PathName;
                if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return;
                _databaseReady = false;
                _requestedPath = path;
                Interlocked.Exchange(ref _opening, 1);
                TraceOpening("opening " + Path.GetFileName(path));
                var d = _dispatcher;
                if (d == null || d.HasShutdownStarted) { TraceOpening("queued-before-dispatcher"); return; }
                d.BeginInvoke(new Action(() =>
                {
                    if (_stopping || generation != _generation) return;
                    try
                    {
                        bool indexWasReady = _bootstrap == null || _bootstrap.IsCompleted;
                        ShowOpening(path, generation);
                        // Startup preloading normally finishes before the first model is chosen.
                        // If it is still running, refresh the visible card as soon as it is ready.
                        if (!indexWasReady)
                            _bootstrap.ContinueWith(_ =>
                            {
                                if (!d.HasShutdownStarted) d.BeginInvoke(new Action(() => ShowOpening(path, generation)));
                            }, TaskScheduler.Default);
                    }
                    catch (Exception ex) { TraceOpening("dispatch-failed " + ex); }
                }));
            }
            catch (Exception ex) { TraceOpening("opening-failed " + ex); }
        }
        private static void ShowOpening(string path, int generation)
        {
            if (_stopping || generation != _generation || _opening == 0) return;
            try
            {
                var model = Find(path) ?? new Snapshot { Name = Path.GetFileNameWithoutExtension(path), File = path };
                if (_tipGeneration != generation) { _openingTip = ModelOpeningTips.Next(); _tipGeneration = generation; }
                _window?.Close(); _window = new OpeningWindow(model, _openingTip);
                _window.Show();
                _window.PresentAboveHost();
                TraceOpening("shown generation=" + generation);
            }
            catch (Exception ex) { TraceOpening("show-failed " + ex); }
        }
        private static void TraceOpening(string message)
        {
            string line = DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine;
            lock (LogGate)
                _logWrites = _logWrites.ContinueWith(_ =>
                {
                    try
                    {
                        Directory.CreateDirectory(Root);
                        string file = Path.Combine(Root, "opening-display.log");
                        if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) System.IO.File.WriteAllText(file, line);
                        else System.IO.File.AppendAllText(file, line);
                    }
                    catch { }
                }, TaskScheduler.Default);
        }
        private static Snapshot Find(string path)
        {
            // O(1) in-memory lookup: no workbook access, cache scan or model analysis.
            if (Catalog.TryGetValue(ModelOpeningHistory.Normalize(path), out var model)) return model;
            return Read(path);
        }
        private static void Publish(Snapshot model)
        {
            model.Aliases ??= new List<string>();
            foreach (string alias in model.Aliases.Concat(new[] { model.File }).Where(v => !string.IsNullOrWhiteSpace(v)))
                Catalog[ModelOpeningHistory.Normalize(alias)] = model;
        }
        private static void Bootstrap()
        {
            try
            {
                Directory.CreateDirectory(Root);
                string catalogPath = Path.Combine(Root, "catalog-v2.json");
                if (File.Exists(catalogPath))
                    foreach (var model in JsonConvert.DeserializeObject<List<Snapshot>>(System.IO.File.ReadAllText(catalogPath)) ?? new List<Snapshot>()) Publish(model);
                foreach (string file in Directory.EnumerateFiles(Root, "*.json").Where(p => Path.GetFileName(p) != "catalog-v2.json"))
                {
                    try { var model = JsonConvert.DeserializeObject<Snapshot>(System.IO.File.ReadAllText(file)); if (model != null) Publish(model); } catch { }
                }
                var history = ModelOpeningHistory.ReadHistory(Path.Combine(_dataRoot, "Historique_Temps_Revit.xlsx"));
                foreach (var model in history)
                {
                    if (Catalog.TryGetValue(ModelOpeningHistory.Normalize(model.File), out var previous))
                    {
                        model.Views = previous.Views ?? new List<Preview>();
                        model.Aliases.AddRange(previous.Aliases ?? new List<string>());
                        if (previous.LastOpened > model.LastOpened) model.LastOpened = previous.LastOpened;
                        model.IsWorkshared ??= previous.IsWorkshared;
                        if (model.IsWorkshared == true) model.LastSynchronized = previous.LastSynchronized;
                    }
                    model.Aliases = model.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    model.Views = ModelOpeningHistory.ReadPreviews(model, Path.Combine(_dataRoot, "CacheVignettes", "VueEnSurvol"));
                    Publish(model);
                }
                var all = Catalog.Values.Distinct().ToList();
                string temp = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                System.IO.File.WriteAllText(temp, JsonConvert.SerializeObject(all));
                if (File.Exists(catalogPath)) File.Replace(temp, catalogPath, null); else File.Move(temp, catalogPath);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("BIMaestro opening index: " + ex.Message); }
        }
        private static void Hide()
        {
            if (Interlocked.Exchange(ref _opening, 0) == 0) return;
            TraceOpening("hide");
            Interlocked.Increment(ref _generation);
            // Do not wait on the Revit/UI thread. Queue a native hide immediately,
            // before a modal dialog is shown, then dispose the card on its dispatcher.
            IntPtr handle = Interlocked.CompareExchange(ref _displayHandle, IntPtr.Zero, IntPtr.Zero);
            if (handle != IntPtr.Zero) HideNativeWindowAsync(handle, 0);
            var d = _dispatcher;
            if (d != null && !d.HasShutdownStarted) d.BeginInvoke(DispatcherPriority.Send, new Action(() => { _window?.Close(); _window = null; }));
        }
        private static void DialogShowing(object sender, DialogBoxShowingEventArgs args)
            => DismissForDialog(args?.GetType().Name ?? "dialog");
        private static void DismissForDialog(string kind)
        {
            if (_opening == 0) return;
            TraceOpening("dialog-priority " + kind);
            // End this opening card permanently; accepting a dialog must never bring it back.
            // The dialog's result remains entirely under Revit/the user's control.
            Hide();
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindowAsync")]
        private static extern bool HideNativeWindowAsync(IntPtr window, int command);
        private static string Key(Document doc)
        {
            if (doc.IsWorkshared) { try { return ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch { } }
            return doc.PathName;
        }
        private static void Opened(object sender, DocumentOpenedEventArgs e)
        {
            TraceOpening("document-opened status=" + e.Status);
            if (e.Status.ToString() != "Succeeded") { Hide(); return; }
            // Database loading can finish before the view/interface is ready. Keep the
            // card until Revit returns to Idling rather than exposing its blank canvas.
            _databaseReady = true;
            try
            {
                var doc = e.Document;
                if (doc == null || doc.IsFamilyDocument) return;
                string key = Key(doc); if (string.IsNullOrWhiteSpace(key)) return;
                var prior = Find(key);
                Models[key] = new Snapshot { Name = doc.Title, File = key, LastOpened = DateTime.Now,
                    IsWorkshared = doc.IsWorkshared, LastSynchronized = doc.IsWorkshared ? prior?.LastSynchronized : null,
                    Aliases = new[] { key, doc.PathName }.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(), Views = prior?.Views?.ToList() ?? new List<Preview>() };
                PendingPreviewCatalogs.Enqueue(doc);
                Aliases[key] = doc.PathName; _nextSnapshot = DateTime.UtcNow.AddSeconds(15);
            }
            catch { }
        }
        private static void QueueLegacyPreviews(string key, List<Preview> previews)
        {
            _writes = _writes.ContinueWith(_ =>
            {
                try
                {
                    var model = Find(key);
                    if (model == null) return;
                    foreach (var preview in previews.Where(v => File.Exists(v.Path)))
                    {
                        preview.Seen = File.GetLastWriteTime(preview.Path);
                        string metadata = preview.Path + ".view.json";
                        System.IO.File.WriteAllText(metadata, JsonConvert.SerializeObject(preview));
                    }
                    var copy = JsonConvert.DeserializeObject<Snapshot>(JsonConvert.SerializeObject(model));
                    copy.Views = ModelOpeningHistory.ReadPreviews(copy, Path.Combine(_dataRoot, "CacheVignettes", "VueEnSurvol"));
                    Publish(copy); Write(key, copy);
                }
                catch { }
            }, TaskScheduler.Default);
        }
        private static void Activated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                var doc = e.Document; var view = e.CurrentActiveView;
                if (doc == null || doc.IsFamilyDocument || !(view is View3D) || view.IsTemplate) return;
                string key = Key(doc); if (string.IsNullOrWhiteSpace(key)) return;
                if (!Models.TryGetValue(key, out var model)) Models[key] = model = new Snapshot { Name = doc.Title, File = key, LastOpened = DateTime.Now };
                Aliases[key] = doc.PathName;
                model.Views.RemoveAll(v => v.Id == view.UniqueId);
                model.Views.Insert(0, new Preview { Id = view.UniqueId, Name = view.Name, Path = ViewHover.ViewHoverPreviewService.GetDeckPreviewPath(doc, view), ViewType = view.ViewType.ToString(), Seen = DateTime.Now });
                model.Views = model.Views.Take(3).ToList(); _nextSnapshot = DateTime.UtcNow.AddSeconds(10);
            }
            catch { }
        }
        private static void Idle(object sender, IdlingEventArgs e)
        {
            if (sender is UIApplication host) _hostMainWindow = host.MainWindowHandle;
            if (_databaseReady && _opening != 0) { _databaseReady = false; TraceOpening("interface-idle"); Hide(); }
            if (PendingPreviewCatalogs.Count > 0)
            {
                var doc = PendingPreviewCatalogs.Dequeue();
                try
                {
                    if (doc.IsValidObject)
                    {
                        var legacy = new List<Preview>();
                        // Once Revit is idle, associate existing PNGs with their real view identities.
                        // No geometry extraction, image export, or network model read.
                        foreach (Autodesk.Revit.DB.View view in new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.View)))
                            if (!view.IsTemplate) legacy.Add(new Preview { Id = view.UniqueId, Name = view.Name,
                                ViewType = view.ViewType.ToString(), Path = ViewHover.ViewHoverPreviewService.GetDeckPreviewPath(doc, view) });
                        QueueLegacyPreviews(Key(doc), legacy);
                    }
                }
                catch { }
            }
            if (DateTime.UtcNow < _nextSnapshot || !_writes.IsCompleted) return;
            _nextSnapshot = DateTime.UtcNow.AddMinutes(1); SaveSnapshots();
        }
        private static void Closing(object sender, DocumentClosingEventArgs e) { SaveSnapshots(); }
        private static void Synchronized(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            // Only a successfully completed sync observed on this workstation is recorded.
            if (e.Status.ToString() != "Succeeded") return;
            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;
                string key = Key(doc);
                if (string.IsNullOrWhiteSpace(key)) return;
                if (!Models.TryGetValue(key, out var model))
                    Models[key] = model = new Snapshot { Name = doc.Title, File = key };
                model.IsWorkshared = true; model.LastSynchronized = DateTime.Now;
                Aliases[key] = doc.PathName;
                SaveSnapshots();
            }
            catch (Exception ex) { TraceOpening("sync-cache-failed " + ex.Message); }
        }
        internal static void RecordPreview(Autodesk.Revit.DB.View view, string targetPath)
        {
            // Only detached metadata reaches the worker. The PNG already exists.
            var metadata = new Preview { Id = view.UniqueId, Name = view.Name, Path = targetPath, ViewType = view.ViewType.ToString(), Seen = DateTime.Now };
            Task.Run(() =>
            {
                try { System.IO.File.WriteAllText(targetPath + ".view.json", JsonConvert.SerializeObject(metadata)); }
                catch { }
            });
        }
        private static string CachePath(string key)
        {
            using var sha = SHA256.Create();
            return Path.Combine(Root, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key.Trim().ToUpperInvariant()))).Replace("-", "") + ".json");
        }
        private static Snapshot Read(string key)
        {
            try { string path = CachePath(key); return File.Exists(path) ? JsonConvert.DeserializeObject<Snapshot>(System.IO.File.ReadAllText(path)) : null; }
            catch { return null; }
        }
        private static void SaveSnapshots()
        {
            try
            {
                var live = ExcelLogger.GetDashboardEntries();
                var copies = Models.Select(pair => new { Key = pair.Key, Alias = Aliases.TryGetValue(pair.Key, out var alias) ? alias : pair.Key,
                    Model = JsonConvert.DeserializeObject<Snapshot>(JsonConvert.SerializeObject(pair.Value)) }).ToArray();
                _writes = _writes.ContinueWith(_ =>
                {
                    try
                    {
                        Directory.CreateDirectory(Root); var saved = ReadSavedHours(live);
                        foreach (var copy in copies)
                        {
                            var model = copy.Model; var old = Find(copy.Key);
                            model.IsWorkshared ??= old?.IsWorkshared;
                            if (model.IsWorkshared == true)
                            {
                                if (!model.LastSynchronized.HasValue || old?.LastSynchronized > model.LastSynchronized)
                                    model.LastSynchronized = old?.LastSynchronized;
                            }
                            else model.LastSynchronized = null;
                            model.Views = model.Views.Concat(old?.Views ?? new List<Preview>()).Where(v => !string.IsNullOrWhiteSpace(v.Path) && File.Exists(v.Path))
                                .OrderByDescending(v => v.Seen).GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(3).ToList();
                            var current = live.Where(v => string.Equals(v.DocumentId, copy.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                            double active = current.Sum(v => v.Hours);
                            SavedTime history = null;
                            saved?.TryGetValue(copy.Key, out history);
                            model.Hours = saved == null ? old?.Hours : (history?.Hours ?? 0) + active;
                            model.WeekHours = saved == null ? old?.WeekHours : (history?.WeekHours ?? 0) + current.Where(v => v.When.Date >= WeekStart()).Sum(v => v.Hours);
                            model.LastSessionHours = active > 0 ? (double?)active : history?.LastSessionHours ?? old?.LastSessionHours;
                            model.Aliases = (old?.Aliases ?? new List<string>()).Concat(new[] { copy.Key, copy.Alias }).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            Publish(model);
                            Write(copy.Key, model);
                            if (!string.IsNullOrWhiteSpace(copy.Alias) && !string.Equals(copy.Alias, copy.Key, StringComparison.OrdinalIgnoreCase)) Write(copy.Alias, model);
                        }
                    }
                    catch { }
                }, TaskScheduler.Default);
            }
            catch { }
        }
        private static void Write(string key, Snapshot model)
        {
            string target = CachePath(key), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { System.IO.File.WriteAllText(temp, JsonConvert.SerializeObject(model)); if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private sealed class SavedTime
        {
            public double Hours, WeekHours;
            public double? LastSessionHours;
        }
        private static DateTime WeekStart() => DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        private static Dictionary<string, SavedTime> ReadSavedHours(List<TimeDashboardEntry> live)
        {
            try
            {
                var exclude = new HashSet<string>(live.Select(v => v.SessionId).Where(v => !string.IsNullOrWhiteSpace(v)));
                var models = ModelOpeningHistory.ReadHistory(Path.Combine(_dataRoot, "Historique_Temps_Revit.xlsx"), exclude);
                var result = new Dictionary<string, SavedTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in models)
                {
                    var summary = new SavedTime { Hours = model.Hours ?? 0, WeekHours = model.WeekHours ?? 0, LastSessionHours = model.LastSessionHours };
                    foreach (string alias in model.Aliases.Concat(new[] { model.File })) result[ModelOpeningHistory.Normalize(alias)] = summary;
                }
                return result;
            }
            catch { return null; }
        }

        private sealed class OpeningWindow : Window
        {
            private readonly System.Windows.Controls.Image _image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
            private static readonly Lazy<BitmapSource> HeaderLogo = new Lazy<BitmapSource>(LoadHeaderLogo);
            private HwndSource _nativeSource;
            private IntPtr _foregroundHook;
            private WinEventProc _foregroundCallback;
            private bool _presentationRequested;
            private readonly uint _processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            private readonly int _generationAtCreation = _generation;
            private const int ExtendedStyle = -20;
            private const long NoActivate = 0x08000000, ToolWindow = 0x00000080;

            public OpeningWindow(Snapshot model, ModelOpeningTips.Tip openingTip = null)
            {
                Title = "BIMaestro · Ouverture de la maquette";
                Width = 940; Height = model.IsWorkshared == true ? 625 : 590;
                ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
                ShowInTaskbar = false; ShowActivated = false; Focusable = false;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = Brush("#FFFFFF"); FontFamily = new FontFamily("Segoe UI");
                // This window deliberately has no Revit owner: activation must never synchronize
                // its independent dispatcher with Revit's busy UI thread.
                SourceInitialized += (_, __) => ConfigurePassiveWindow();
                var available = (model.Views ?? new List<Preview>()).Where(v => File.Exists(v.Path)).OrderByDescending(v => v.Seen).ToList();
                var preview = available.FirstOrDefault(v => v.ViewType == "ThreeD") ?? available.FirstOrDefault();

                var layout = new Grid { IsHitTestVisible = false, Focusable = false };
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(69) });
                layout.RowDefinitions.Add(new RowDefinition());
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(55) });
                Content = new Border { Background = Brush("#FFFFFF"), BorderBrush = Brush("#DFE7E9"), BorderThickness = new Thickness(1), Child = layout };
                layout.Children.Add(new Border { Background = Brush("#26776E") });

                var header = new Grid { Margin = new Thickness(26, 0, 26, 0) };
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetRow(header, 1); layout.Children.Add(header);
                var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                brand.Children.Add(new System.Windows.Controls.Image { Source = HeaderLogo.Value, Width = 45, Height = 42, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
                var name = Text("BIMaestro", 18, "#28534F", true); name.Margin = new Thickness(11, 0, 0, 0); name.VerticalAlignment = VerticalAlignment.Center; brand.Children.Add(name); header.Children.Add(brand);
                var context = Text("OUVERTURE DE LA MAQUETTE", 10, "#7A8B94"); context.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(context, 1); header.Children.Add(context);

                var main = new Grid { Margin = new Thickness(24, 0, 28, 20) };
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.16, GridUnitType.Star) });
                main.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(main, 2); layout.Children.Add(main);
                var left = new Grid { Margin = new Thickness(18, 16, 18, 16) };
                left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) }); left.RowDefinitions.Add(new RowDefinition()); left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(49) });
                main.Children.Add(new Border { Background = Brush("#F2F6F6"), CornerRadius = new CornerRadius(10), Child = left });
                left.Children.Add(Text(preview?.ViewType == "ThreeD" ? "DERNIER APERÇU 3D" : "DERNIER APERÇU ENREGISTRÉ", 10, "#6A818B", true));
                _image.Margin = new Thickness(0, 6, 0, 13); Grid.SetRow(_image, 1); left.Children.Add(_image);
                var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                caption.Children.Add(Text(preview?.Name ?? "Votre aperçu apparaîtra ici", 12, "#405A67", true));
                var captured = Text(preview == null ? "Aucune image retrouvée pour cette maquette" : "Enregistré · " + DateLabel(preview.Seen), 11, "#7B8E98"); captured.Margin = new Thickness(0, 4, 0, 0); caption.Children.Add(captured); Grid.SetRow(caption, 2); left.Children.Add(caption);
                LoadPreview(preview);

                var right = new Grid { Margin = new Thickness(28, 10, 0, 0) };
                right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                right.RowDefinitions.Add(new RowDefinition());
                Grid.SetColumn(right, 1); main.Children.Add(right);
                var project = new StackPanel(); project.Children.Add(Text("VOTRE PROJET", 10, "#26776E", true));
                var title = Text(model.Name ?? "Maquette Revit", 25, "#223843", true); title.Margin = new Thickness(0, 9, 0, 0); title.MaxHeight = 101; title.TextTrimming = TextTrimming.CharacterEllipsis; project.Children.Add(title); right.Children.Add(project);
                var time = new StackPanel { Margin = new Thickness(0, 23, 0, 19) };
                time.Children.Add(Text("Temps de travail actif", 12, "#74858F"));
                bool shortTotal = model.Hours > 0 && model.Hours < 1.0 / 60;
                var total = Text(model.Hours.HasValue ? Duration(model.Hours.Value) : "—", shortTotal ? 21 : 37, "#223843", true); total.Margin = new Thickness(0, 4, 0, 3); time.Children.Add(total);
                if (model.WeekHours.HasValue) time.Children.Add(Text(Duration(model.WeekHours.Value) + " cette semaine", 12, "#26776E"));
                Grid.SetRow(time, 1); right.Children.Add(time);
                var history = new Grid { Margin = new Thickness(0, 0, 0, 20) };
                history.ColumnDefinitions.Add(new ColumnDefinition()); history.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                history.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) }); history.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
                AddFact(history, 0, "Dernière ouverture", model.LastOpened == default ? "—" : DateLabel(model.LastOpened));
                AddFact(history, 1, "Dernière session", model.LastSessionHours.HasValue ? Duration(model.LastSessionHours.Value) : "—");
                if (model.IsWorkshared == true)
                {
                    history.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
                    AddFact(history, 2, "Dernière synchro connue", model.LastSynchronized.HasValue ? DateLabel(model.LastSynchronized.Value) : "Pas encore observée");
                }
                Grid.SetRow(history, 2); right.Children.Add(history);
                var tip = new StackPanel(); tip.Children.Add(Text("LE SAVIEZ-VOUS ?", 10, "#26776E", true));
                // Reuse the same tip even if the startup history index refreshes this card.
                var selectedTip = openingTip ?? ModelOpeningTips.Next();
                var tipTitle = Text(selectedTip.Title, 15, "#315D56", true); tipTitle.Margin = new Thickness(0, 8, 0, 6);
                tip.Children.Add(tipTitle); tip.Children.Add(Text(selectedTip.Message, 12, "#59776F"));
                var tipPanel = new Border { Background = Brush("#F0F6F3"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16), Child = tip, VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(tipPanel, 3); right.Children.Add(tipPanel);

                var footer = new Grid { Margin = new Thickness(26, 0, 26, 0) };
                footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var status = Text("Revit ouvre votre maquette…", 12, "#405A67", true); status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(status);
                var note = Text("Données des sessions précédentes", 11, "#8A9AA2"); note.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(note, 1); footer.Children.Add(note);
                var footerBorder = new Border { BorderBrush = Brush("#EAF0F1"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }; Grid.SetRow(footerBorder, 3); layout.Children.Add(footerBorder);
                ContentRendered += (_, __) => TraceOpening("content-rendered");
                Closed += (_, __) =>
                {
                    _presentationRequested = false;
                    if (_nativeSource != null) Interlocked.CompareExchange(ref _displayHandle, IntPtr.Zero, _nativeSource.Handle);
                    if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
                    if (_nativeSource != null) _nativeSource.RemoveHook(PassiveHook);
                    _image.Source = null;
                };
            }
            internal void PresentAboveHost()
            {
                _presentationRequested = true;
                UpdatePresentation();
            }
            private void UpdatePresentation()
            {
                if (!_presentationRequested || _nativeSource == null || _nativeSource.IsDisposed) return;
                IntPtr foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint foregroundProcess);
                // Revit may raise its own main window after Show(). Keep this card above it
                // without an owner/activation link. Hide it when the user switches apps.
                // Same process does not mean main window: task dialogs also belong to Revit.
                bool inHost = CanPresentAboveHost(foregroundProcess, _processId, foreground, _hostMainWindow,
                    _opening != 0, _generationAtCreation == _generation);
                SetWindowPos(_nativeSource.Handle, new IntPtr(inHost ? -1 : -2), 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010 | (inHost ? 0x0040u : 0x0080u));
            }
            private static bool CanPresentAboveHost(uint foregroundProcess, uint hostProcess, IntPtr foreground,
                IntPtr mainWindow, bool opening, bool currentGeneration)
            {
                return opening && currentGeneration && mainWindow != IntPtr.Zero &&
                    foregroundProcess == hostProcess && foreground == mainWindow;
            }
            private void ConfigurePassiveWindow()
            {
                var handle = new WindowInteropHelper(this).Handle;
                Interlocked.Exchange(ref _displayHandle, handle);
                long styles = GetWindowLongPtr(handle, ExtendedStyle).ToInt64();
                SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(styles | NoActivate | ToolWindow));
                _nativeSource = HwndSource.FromHwnd(handle); _nativeSource?.AddHook(PassiveHook);
                _foregroundCallback = (_, __, ___, ____, _____, ______, _______) => UpdatePresentation();
                _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundCallback, 0, 0, 0);
            }
            private static IntPtr PassiveHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == 0x0021) { handled = true; return new IntPtr(4); } // WM_MOUSEACTIVATE: MA_NOACTIVATEANDEAT
                return IntPtr.Zero;
            }
            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
            private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
            private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool UnhookWinEvent(IntPtr hook);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
            private static BitmapSource LoadHeaderLogo()
            {
                using var stream = typeof(ModelOpeningService).Assembly.GetManifestResourceStream("BIMaestro.Resources.OpeningLogo.png");
                if (stream == null) return null;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 180; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                // Use the emblem above the wordmark; the header already displays the product name.
                var mark = new CroppedBitmap(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, (int)Math.Round(bitmap.PixelHeight * 0.77)));
                mark.Freeze(); return mark;
            }
            private void LoadPreview(Preview preview)
            {
                if (preview == null) return;
                try
                {
                    using var stream = new FileStream(preview.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 1000; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap;
                }
                catch { }
            }
            private static void AddFact(Grid grid, int row, string label, string value)
            {
                var key = Text(label, 12, "#74858F"); key.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(key, row); grid.Children.Add(key);
                var content = Text(value, 12, "#344E5B", true); content.VerticalAlignment = VerticalAlignment.Center; content.Margin = new Thickness(12, 0, 0, 0); Grid.SetRow(content, row); Grid.SetColumn(content, 1); grid.Children.Add(content);
            }
            private static string DateLabel(DateTime when)
            {
                if (when.Date == DateTime.Today) return "Aujourd’hui à " + when.ToString("HH:mm");
                if (when.Date == DateTime.Today.AddDays(-1)) return "Hier à " + when.ToString("HH:mm");
                return when.ToString("dd/MM/yyyy à HH:mm");
            }
            private static string Duration(double hours)
            {
                var t = TimeSpan.FromHours(Math.Max(0, hours));
                if (t.TotalHours >= 1) return ((int)t.TotalHours) + " h " + t.Minutes.ToString("00") + " min";
                if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes) + " min";
                return t.TotalSeconds > 0 ? "Moins d’une minute" : "0 min";
            }
            private static SolidColorBrush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            private static TextBlock Text(string text, double size, string color, bool bold = false) => new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        }
    }
}
