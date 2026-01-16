using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Famille
{
    public partial class ReviewWindow : Window
    {
        private readonly List<ReviewItem> _items;
        private readonly Stack<UndoRecord> _undo = new Stack<UndoRecord>();
        private readonly string _undoRoot;
        private readonly Action<string> _log;

        private int _index = 0;

        public bool ClosedEarly { get; private set; } = false;

        public ReviewWindow(List<ReviewItem> items, string undoRoot, Action<string> log)
        {
            // ✅ FORCE WPF SOFTWARE RENDER (process-wide)
            try { RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; } catch { }

            InitializeComponent();

            _items = items ?? new List<ReviewItem>();
            _undoRoot = undoRoot;
            _log = log;

            // ✅ FORCE SOFTWARE RENDER (window/hwnd)
            SourceInitialized += (_, __) =>
            {
                try
                {
                    var src = PresentationSource.FromVisual(this) as HwndSource;
                    if (src?.CompositionTarget != null)
                        src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                }
                catch { }
            };

            Loaded += (_, __) =>
            {
                try { ShowCurrent(); }
                catch (Exception ex) { _log?.Invoke("❌ ReviewWindow ShowCurrent: " + ex); }
            };

            Closing += (_, __) => { ClosedEarly = _index < _items.Count; };
        }

        private void ShowCurrent()
        {
            if (_items.Count == 0)
            {
                TxtTitle.Text = "Aucun item à vérifier.";
                TxtCounter.Text = "";
                ImgA.Source = null;
                ImgB.Source = null;
                TxtMissingA.Visibility = Visibility.Visible;
                TxtMissingB.Visibility = Visibility.Visible;
                return;
            }

            _index = Math.Max(0, Math.Min(_index, _items.Count - 1));
            var it = _items[_index];

            TxtTitle.Text = it.Title ?? Path.GetFileName(it.FinalPath);
            TxtCounter.Text = $"{_index + 1}/{_items.Count}";

            bool aOk = File.Exists(it.CandidateAPath);
            bool bOk = File.Exists(it.CandidateBPath);

            ImgA.Source = aOk ? LoadBitmapNoLock(it.CandidateAPath) : null;
            ImgB.Source = bOk ? LoadBitmapNoLock(it.CandidateBPath) : null;

            TxtMissingA.Visibility = aOk ? Visibility.Collapsed : Visibility.Visible;
            TxtMissingB.Visibility = bOk ? Visibility.Collapsed : Visibility.Visible;

            // Auto-commit si un seul existe
            if (aOk && !bOk) { Choose(ReviewChoice.LeftA); return; }
            if (!aOk && bOk) { Choose(ReviewChoice.RightB); return; }
            if (!aOk && !bOk) { Next(); return; }
        }

        private void Choose(ReviewChoice choice)
        {
            if (_items.Count == 0) return;
            if (_index < 0 || _index >= _items.Count) return;

            var it = _items[_index];

            try
            {
                var rec = ReviewFileOps.CommitChoice(it, choice, _undoRoot);
                _undo.Push(rec);
            }
            catch (Exception ex)
            {
                _log?.Invoke("⚠️ CommitChoice échoué : " + ex.Message);
            }

            Next();
        }

        private void Next()
        {
            _index++;
            if (_index >= _items.Count)
            {
                DialogResult = true;
                Close();
                return;
            }
            ShowCurrent();
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;

            var rec = _undo.Pop();
            try { ReviewFileOps.UndoLast(rec); }
            catch (Exception ex) { _log?.Invoke("⚠️ Undo échoué : " + ex.Message); }

            _index = Math.Max(0, _index - 1);
            ShowCurrent();
        }

        private static BitmapImage LoadBitmapNoLock(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad; // pas de lock fichier
                    img.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
            }
            catch { return null; }
        }

        // Events
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.NumPad4 || e.Key == Key.Left)
            {
                Choose(ReviewChoice.LeftA);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.NumPad6 || e.Key == Key.Right)
            {
                Choose(ReviewChoice.RightB);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back || e.Key == Key.NumPad0)
            {
                Undo();
                e.Handled = true;
                return;
            }
        }

        private void CardLeft_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Choose(ReviewChoice.LeftA);
        private void CardRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Choose(ReviewChoice.RightB);

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => Undo();
        private void BtnLeft_Click(object sender, RoutedEventArgs e) => Choose(ReviewChoice.LeftA);
        private void BtnRight_Click(object sender, RoutedEventArgs e) => Choose(ReviewChoice.RightB);
    }
}
