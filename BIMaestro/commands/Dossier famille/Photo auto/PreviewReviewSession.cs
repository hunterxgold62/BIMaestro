using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Famille.PreviewReview
{
    public sealed class ReviewItem
    {
        public string FamilyDisplayName { get; set; }
        public string LeftCandidatePath { get; set; }   // __A
        public string RightCandidatePath { get; set; }  // __B
        public string FinalPath { get; set; }

        public string LeftCaption { get; set; }
        public string RightCaption { get; set; }
    }

    public sealed class RequestCloseEventArgs : EventArgs
    {
        public bool? DialogResult { get; }
        public RequestCloseEventArgs(bool? dialogResult) => DialogResult = dialogResult;
    }

    internal sealed class UndoState
    {
        public ReviewItem Item;
        public string UndoFolder;

        public string FinalBackupPath;
        public bool HadFinalBefore;

        public string LeftCachedPath;
        public string RightCachedPath;

        public string OriginalLeftPath;
        public string OriginalRightPath;
        public string OriginalFinalPath;
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class PreviewReviewViewModel : INotifyPropertyChanged
    {
        private readonly List<ReviewItem> _items;

        private int _index = 0;
        private UndoState _undo;
        private string _sessionUndoRoot;

        private bool _isBatchEndPromptVisible;
        private bool _isChoiceEnabled = true;
        private bool _stopped = false;

        private ImageSource _leftImage;
        private ImageSource _rightImage;

        private string _familyDisplayName;
        private string _leftCaption;
        private string _rightCaption;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<RequestCloseEventArgs> RequestClose;

        // Bindings
        public string FamilyDisplayName { get => _familyDisplayName; private set { _familyDisplayName = value; OnPropertyChanged(); } }
        public string LeftCaption { get => _leftCaption; private set { _leftCaption = value; OnPropertyChanged(); } }
        public string RightCaption { get => _rightCaption; private set { _rightCaption = value; OnPropertyChanged(); } }

        public ImageSource LeftImage { get => _leftImage; private set { _leftImage = value; OnPropertyChanged(); } }
        public ImageSource RightImage { get => _rightImage; private set { _rightImage = value; OnPropertyChanged(); } }

        public int BatchSize => _items.Count;
        public int BatchIndex => Math.Max(1, Math.Min(_index + 1, BatchSize));

        public bool IsBatchEndPromptVisible { get => _isBatchEndPromptVisible; private set { _isBatchEndPromptVisible = value; OnPropertyChanged(); RefreshCommands(); } }
        public bool IsChoiceEnabled { get => _isChoiceEnabled; private set { _isChoiceEnabled = value; OnPropertyChanged(); RefreshCommands(); } }
        public bool CanGoBack => _undo != null && !_stopped && IsChoiceEnabled && !IsBatchEndPromptVisible;

        // Commands
        public ICommand ChooseLeftCommand { get; }
        public ICommand ChooseRightCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand ContinueBatchCommand { get; }
        public ICommand StopCommand { get; }

        private readonly RelayCommand _chooseLeftCmd;
        private readonly RelayCommand _chooseRightCmd;
        private readonly RelayCommand _backCmd;
        private readonly RelayCommand _continueCmd;
        private readonly RelayCommand _stopCmd;

        public PreviewReviewViewModel(IEnumerable<ReviewItem> items)
        {
            _items = (items ?? Enumerable.Empty<ReviewItem>()).Where(i => i != null).ToList();
            _sessionUndoRoot = CreateSessionUndoRoot();

            _chooseLeftCmd = new RelayCommand(() => Choose(true), CanChoose);
            _chooseRightCmd = new RelayCommand(() => Choose(false), CanChoose);
            _backCmd = new RelayCommand(UndoLast, () => CanGoBack);
            _continueCmd = new RelayCommand(ContinueNextBatch, () => !_stopped && IsBatchEndPromptVisible);
            _stopCmd = new RelayCommand(Stop, () => !_stopped);

            ChooseLeftCommand = _chooseLeftCmd;
            ChooseRightCommand = _chooseRightCmd;
            BackCommand = _backCmd;
            ContinueBatchCommand = _continueCmd;
            StopCommand = _stopCmd;

            if (_items.Count == 0)
            {
                RequestClose?.Invoke(this, new RequestCloseEventArgs(true));
                return;
            }

            LoadCurrent();
        }

        private bool CanChoose()
        {
            if (_stopped) return false;
            if (!IsChoiceEnabled) return false;
            if (IsBatchEndPromptVisible) return false;
            return _index >= 0 && _index < _items.Count;
        }

        private void Choose(bool keepLeft)
        {
            if (!CanChoose()) return;

            IsChoiceEnabled = false;

            try
            {
                var item = _items[_index];
                CommitChoice(item, keepLeft);

                _index++;

                if (_index >= _items.Count)
                {
                    // Au lieu de fermer direct, on affiche le prompt Continuer/Arrêter
                    IsBatchEndPromptVisible = true;
                    return;
                }

                LoadCurrent();
            }
            finally
            {
                IsChoiceEnabled = true;
            }
        }

        private void ContinueNextBatch()
        {
            if (_stopped) return;

            CleanupUndo();
            RequestClose?.Invoke(this, new RequestCloseEventArgs(true));
        }

        private void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            CleanupUndo();
            RequestClose?.Invoke(this, new RequestCloseEventArgs(false));
        }

        private void UndoLast()
        {
            if (!CanGoBack) return;

            IsChoiceEnabled = false;

            try
            {
                var u = _undo;
                if (u == null) return;

                // restore final
                try { Directory.CreateDirectory(Path.GetDirectoryName(u.OriginalFinalPath) ?? ""); } catch { }

                try
                {
                    if (u.HadFinalBefore && File.Exists(u.FinalBackupPath))
                    {
                        CopyThenReplace(u.FinalBackupPath, u.OriginalFinalPath);
                    }
                    else
                    {
                        if (File.Exists(u.OriginalFinalPath))
                        {
                            try { File.Delete(u.OriginalFinalPath); } catch { }
                        }
                    }
                }
                catch { }

                // restore candidates
                TryRestoreCachedCandidate(u.LeftCachedPath, u.OriginalLeftPath);
                TryRestoreCachedCandidate(u.RightCachedPath, u.OriginalRightPath);

                _index = Math.Max(0, _index - 1);

                IsBatchEndPromptVisible = false;
                CleanupUndo();

                LoadCurrent();
            }
            finally
            {
                IsChoiceEnabled = true;
            }
        }

        private void LoadCurrent()
        {
            if (_index < 0) _index = 0;
            if (_index >= _items.Count) _index = _items.Count - 1;

            var item = _items[_index];

            FamilyDisplayName = item.FamilyDisplayName ?? Path.GetFileNameWithoutExtension(item.FinalPath ?? "") ?? "Famille";
            LeftCaption = item.LeftCaption ?? "Gauche";
            RightCaption = item.RightCaption ?? "Droite";

            LeftImage = LoadImageNoLock(item.LeftCandidatePath);
            RightImage = LoadImageNoLock(item.RightCandidatePath);

            OnPropertyChanged(nameof(BatchIndex));
            OnPropertyChanged(nameof(BatchSize));
            OnPropertyChanged(nameof(CanGoBack));
        }

        // commit : écrit Final.png, et enlève les candidats du dossier images (move vers temp) + Undo 1 niveau
        private void CommitChoice(ReviewItem item, bool keepLeft)
        {
            if (item == null) return;

            CleanupUndo();

            string chosen = keepLeft ? item.LeftCandidatePath : item.RightCandidatePath;

            var undo = new UndoState
            {
                Item = item,
                UndoFolder = Path.Combine(_sessionUndoRoot, "undo_last"),
                OriginalLeftPath = item.LeftCandidatePath,
                OriginalRightPath = item.RightCandidatePath,
                OriginalFinalPath = item.FinalPath
            };

            try { Directory.CreateDirectory(undo.UndoFolder); } catch { }

            // backup final si existant
            undo.HadFinalBefore = File.Exists(item.FinalPath);
            if (undo.HadFinalBefore)
            {
                undo.FinalBackupPath = Path.Combine(undo.UndoFolder, "__final_backup.png");
                try { CopyThenReplace(item.FinalPath, undo.FinalBackupPath); } catch { }
            }

            // write final = chosen
            try { Directory.CreateDirectory(Path.GetDirectoryName(item.FinalPath) ?? ""); } catch { }

            if (File.Exists(chosen))
                CopyThenReplace(chosen, item.FinalPath);

            // move candidates -> undo cache (ça les “supprime” du dossier image)
            undo.LeftCachedPath = Path.Combine(undo.UndoFolder, "__left.png");
            undo.RightCachedPath = Path.Combine(undo.UndoFolder, "__right.png");

            MoveOrCopyCandidateToCache(item.LeftCandidatePath, undo.LeftCachedPath);
            MoveOrCopyCandidateToCache(item.RightCandidatePath, undo.RightCachedPath);

            _undo = undo;

            OnPropertyChanged(nameof(CanGoBack));
        }

        private static void MoveOrCopyCandidateToCache(string source, string cacheDest)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source)) return;
                if (!File.Exists(source)) return;

                try
                {
                    if (File.Exists(cacheDest)) File.Delete(cacheDest);
                    File.Move(source, cacheDest);
                }
                catch
                {
                    try
                    {
                        CopyThenReplace(source, cacheDest);
                        try { File.Delete(source); } catch { }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void TryRestoreCachedCandidate(string cached, string original)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cached) || string.IsNullOrWhiteSpace(original)) return;
                if (!File.Exists(cached)) return;

                try { Directory.CreateDirectory(Path.GetDirectoryName(original) ?? ""); } catch { }

                try
                {
                    if (File.Exists(original)) File.Delete(original);
                    File.Move(cached, original);
                }
                catch
                {
                    try
                    {
                        CopyThenReplace(cached, original);
                        try { File.Delete(cached); } catch { }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void CopyThenReplace(string source, string target)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return;

            try { Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ""); } catch { }

            string tmp = target + ".tmp";
            try
            {
                File.Copy(source, tmp, overwrite: true);
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                File.Move(tmp, target);
            }
            catch
            {
                try { File.Copy(source, target, overwrite: true); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private void CleanupUndo()
        {
            try
            {
                _undo = null;
                OnPropertyChanged(nameof(CanGoBack));

                var undoLast = Path.Combine(_sessionUndoRoot, "undo_last");
                if (Directory.Exists(undoLast))
                {
                    try { Directory.Delete(undoLast, recursive: true); } catch { }
                }
            }
            catch { }
        }

        private static string CreateSessionUndoRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "BIMaestro_PreviewReview", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            try { Directory.CreateDirectory(root); } catch { }
            return root;
        }

        // IMPORTANT: pas de lock fichier
        private static ImageSource LoadImageNoLock(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        private void RefreshCommands()
        {
            _chooseLeftCmd?.RaiseCanExecuteChanged();
            _chooseRightCmd?.RaiseCanExecuteChanged();
            _backCmd?.RaiseCanExecuteChanged();
            _continueCmd?.RaiseCanExecuteChanged();
            _stopCmd?.RaiseCanExecuteChanged();

            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(IsChoiceEnabled));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } catch { }
        }
    }

    public static class PreviewReviewSession
    {
        /// <summary>
        /// Ouvre une fenêtre de review pour UN lot (ex: 20 items).
        /// Retourne TRUE = continuer prochain lot, FALSE = arrêter.
        /// </summary>
        public static bool RunBatch(IntPtr revitMainWindowHandle, IEnumerable<ReviewItem> batchItems)
        {
            var vm = new PreviewReviewViewModel(batchItems);
            var win = new PreviewReviewWindow(revitMainWindowHandle, vm);

            bool? res = null;

            try
            {
                res = win.ShowDialog();
            }
            catch
            {
                // si crash UI => stop
                return false;
            }

            return res == true;
        }
    }
}
