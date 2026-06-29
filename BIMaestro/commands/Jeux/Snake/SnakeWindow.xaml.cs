using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

using IOPath = System.IO.Path;

namespace BIMaestro.Bonus
{
    public partial class SnakeWindow : Window
    {
        // -------------------------------
        // Core config
        // -------------------------------
        private const bool UseRenderLoop = true;
        private const int CellSize = 18;

        private const int BaseCols = 28;
        private const int BaseRows = 28;

        private const int GrowAtScore1 = 100;
        private const int GrowAtScore2 = 500;

        // ✅ Classic : nombre de fruits fixe
        private const int ClassicFruitCount = 5;

        // Arcade (optionnel) : un peu variable
        private const int ArcadeFruitMin = 4;
        private const int ArcadeFruitMax = 6;

        // Chances de base (avant ajustements par mode)
        private const double RainFruitChance = 0.06;
        private const double SpeedFruitChance = 0.12;
        private const double MultiplierFruitChance = 0.18;

        private const int MultiplierMax = 5;

        private const double SpeedFactor = 1.5;

        private const int RainExtraTarget = 10;
        private const int MaxTotalFruits = 18;

        private const int BonusSpawnMinManhattan = 3;

        private const int HardcoreWallSegmentsMin = 4;
        private const int HardcoreWallSegmentsMax = 7;
        private const int HardcoreWallLenMin = 3;
        private const int HardcoreWallLenMax = 7;
        private const int HardcoreWallMinDistFromHead = 6;

        private const double PulseHz = 2.2;
        private const double PulseStrength = 0.35;

        private const double BorderPulseHz = 1.6;
        private const double BorderPulseMinAlpha = 0.15;
        private const double BorderPulseMaxAlpha = 0.55;

        private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(420);
        private static readonly TimeSpan LevelUpMsgDuration = TimeSpan.FromSeconds(1.6);

        private const string SupabaseFunctionsBaseUrl = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co";

        private readonly Random _rng = new Random();

        // -------------------------------
        // Window sizing
        // -------------------------------
        private double _baseWindowWidth;
        private double _baseWindowHeight;

        // -------------------------------
        // Dynamic map
        // -------------------------------
        private int _cols = BaseCols;
        private int _rows = BaseRows;

        // -------------------------------
        // Bitmap renderer
        // -------------------------------
        private WriteableBitmap _wb;
        private int[] _px;
        private int _bmpW, _bmpH, _stride;
        private int[] _backgroundCache;
        private bool _backgroundDirty = true;
        private Color _cachedBackgroundColor;
        private Color _cachedGridColor;

        // -------------------------------
        // Timing (render loop)
        // -------------------------------
        private Stopwatch _clock;
        private TimeSpan _lastRenderTime;
        private double _accumMs;

        // -------------------------------
        // Game state
        // -------------------------------
        private readonly LinkedList<Cell> _snake = new LinkedList<Cell>();
        private Direction _dir = Direction.Right;
        private Direction _nextDir = Direction.Right;

        private readonly List<Fruit> _fruits = new List<Fruit>();
        private int _baseFruitTarget;
        private int _rainExtra;
        private int TargetFruitCount => Math.Min(_baseFruitTarget + _rainExtra, MaxTotalFruits);

        private int _score;

        // ✅ Pop uniquement par palier de 100
        private int _lastHundredsBucket = 0;

        // Top score par mode
        private int _topClassic;
        private int _topArcade;
        private int _topHardcore;

        private int CurrentModeTopScore
        {
            get
            {
                switch (_mode)
                {
                    case GameMode.Arcade: return _topArcade;
                    case GameMode.Hardcore: return _topHardcore;
                    default: return _topClassic;
                }
            }
        }

        private void SetCurrentModeTopScore(int value)
        {
            value = Math.Max(0, value);
            switch (_mode)
            {
                case GameMode.Arcade: _topArcade = value; break;
                case GameMode.Hardcore: _topHardcore = value; break;
                default: _topClassic = value; break;
            }
        }

        private int GlobalTopScore => Math.Max(_topClassic, Math.Max(_topArcade, _topHardcore));

        private bool _isGameOver;
        private bool _isPaused;
        private bool _isWaitingStart;

        // ✅ Timers en secondes (pause-friendly)
        private int _scoreMultiplier = 1;
        private double _multiplierRemainingSec = 0;

        private bool _recordThisRun;
        private int _recordScoreThisRun;

        private bool _speedActive;
        private double _speedRemainingSec = 0;

        private bool _rainActive;
        private double _rainRemainingSec = 0;

        private DateTime _flashUntilUtc = DateTime.MinValue;

        private DateTime _toastHideUtc = DateTime.MinValue;

        private readonly HashSet<Cell> _walls = new HashSet<Cell>();

        // Modes
        private GameMode _mode = GameMode.Classic;

        // -------------------------------
        // Skins
        // -------------------------------
        private readonly List<SkinDefinition> _skins = new List<SkinDefinition>
        {
            SkinDefinition.Create(
                SkinId.Classic, "Classic", 0,
                head: Colors.LimeGreen,
                body: Colors.Green,
                background: Color.FromRgb(0x0B, 0x0B, 0x0B),
                grid: Color.FromRgb(0xFF, 0xFF, 0xFF),
                wall: Color.FromRgb(0x2A, 0x2A, 0x2F),
                fruitNormal: Colors.OrangeRed,
                fruitMult: Colors.DeepSkyBlue,
                fruitSpeed: Colors.HotPink,
                fruitRain: Colors.Gold,
                scanlinesOpacity: 0.10,
                vignetteOpacity: 0.22
            ),

            SkinDefinition.Create(
                SkinId.Yellow, "Jaune", 20,
                head: Colors.Gold,
                body: Color.FromRgb(0xDA, 0xA5, 0x20),
                background: Color.FromRgb(0x0A, 0x0A, 0x12),
                grid: Color.FromRgb(0xFF, 0xF4, 0xC2),
                wall: Color.FromRgb(0x2C, 0x25, 0x20),
                fruitNormal: Color.FromRgb(0xFF, 0x6A, 0x2B),
                fruitMult: Color.FromRgb(0x4D, 0xC6, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x6D, 0xC7),
                fruitRain: Color.FromRgb(0xFF, 0xD8, 0x4A),
                scanlinesOpacity: 0.11,
                vignetteOpacity: 0.23
            ),

            SkinDefinition.Create(
                SkinId.Cyan, "Cyan", 50,
                head: Colors.Cyan,
                body: Colors.Teal,
                background: Color.FromRgb(0x06, 0x0F, 0x10),
                grid: Color.FromRgb(0xC8, 0xFF, 0xFF),
                wall: Color.FromRgb(0x1F, 0x2F, 0x33),
                fruitNormal: Color.FromRgb(0xFF, 0x5A, 0x4E),
                fruitMult: Color.FromRgb(0x2F, 0xB7, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x5A, 0xB6),
                fruitRain: Color.FromRgb(0xFF, 0xD0, 0x4A),
                scanlinesOpacity: 0.10,
                vignetteOpacity: 0.22
            ),

            SkinDefinition.Create(
                SkinId.Purple, "Violet", 100,
                head: Colors.Violet,
                body: Colors.MediumPurple,
                background: Color.FromRgb(0x10, 0x06, 0x12),
                grid: Color.FromRgb(0xF2, 0xDA, 0xFF),
                wall: Color.FromRgb(0x2C, 0x20, 0x2F),
                fruitNormal: Color.FromRgb(0xFF, 0x5A, 0x4E),
                fruitMult: Color.FromRgb(0x50, 0xB8, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x66, 0xC4),
                fruitRain: Color.FromRgb(0xFF, 0xD8, 0x4A),
                scanlinesOpacity: 0.12,
                vignetteOpacity: 0.24
            ),

            SkinDefinition.Create(
                SkinId.Crimson, "Crimson", 250,
                head: Color.FromRgb(0xFF, 0x3B, 0x3B),
                body: Color.FromRgb(0xB8, 0x18, 0x2A),
                background: Color.FromRgb(0x12, 0x06, 0x06),
                grid: Color.FromRgb(0xFF, 0xD7, 0xD7),
                wall: Color.FromRgb(0x2F, 0x1D, 0x1D),
                fruitNormal: Color.FromRgb(0xFF, 0x6A, 0x2B),
                fruitMult: Color.FromRgb(0x4D, 0xC6, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x66, 0xB3),
                fruitRain: Color.FromRgb(0xFF, 0xD8, 0x4A),
                scanlinesOpacity: 0.12,
                vignetteOpacity: 0.25
            ),

            SkinDefinition.Create(
                SkinId.ElectricBlue, "Electric Blue", 500,
                head: Color.FromRgb(0x2F, 0xB7, 0xFF),
                body: Color.FromRgb(0x00, 0x6F, 0xB3),
                background: Color.FromRgb(0x05, 0x0A, 0x12),
                grid: Color.FromRgb(0xD6, 0xEE, 0xFF),
                wall: Color.FromRgb(0x1C, 0x23, 0x2F),
                fruitNormal: Color.FromRgb(0xFF, 0x5A, 0x4E),
                fruitMult: Color.FromRgb(0x5A, 0xD0, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x66, 0xC4),
                fruitRain: Color.FromRgb(0xFF, 0xD8, 0x4A),
                scanlinesOpacity: 0.10,
                vignetteOpacity: 0.22
            ),

            SkinDefinition.Create(
                SkinId.Obsidian, "Obsidian", 1000,
                head: Color.FromRgb(0xE8, 0xE8, 0xE8),
                body: Color.FromRgb(0x9A, 0x9A, 0x9A),
                background: Color.FromRgb(0x05, 0x05, 0x07),
                grid: Color.FromRgb(0xEE, 0xEE, 0xEE),
                wall: Color.FromRgb(0x22, 0x22, 0x26),
                fruitNormal: Color.FromRgb(0xFF, 0x6A, 0x2B),
                fruitMult: Color.FromRgb(0x60, 0xCC, 0xFF),
                fruitSpeed: Color.FromRgb(0xFF, 0x66, 0xC4),
                fruitRain: Color.FromRgb(0xFF, 0xD8, 0x4A),
                scanlinesOpacity: 0.13,
                vignetteOpacity: 0.26
            ),
        };

        private bool _skinAuto = true;
        private SkinId _manualSkin = SkinId.Classic;
        private SkinDefinition _activeSkin;

        private int _lastGlobalTopForSkinList = -1;

        // Persistance
        private string _stateFile;

        // UI brushes
        private SolidColorBrush _playfieldBorderBrush;
        private SolidColorBrush _playfieldBackgroundBrush;

        // Shake/Scale transform
        private readonly TranslateTransform _shake = new TranslateTransform(0, 0);

        public SnakeWindow()
        {
            InitializeComponent();
        }

        // -------------------------------
        // Window lifecycle
        // -------------------------------
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _baseWindowWidth = this.Width;
            _baseWindowHeight = this.Height;

            _playfieldBorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            PlayfieldBorder.BorderBrush = _playfieldBorderBrush;

            _playfieldBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0B));
            PlayfieldBorder.Background = _playfieldBackgroundBrush;

            var g = new TransformGroup();
            g.Children.Add(new ScaleTransform(1, 1));
            g.Children.Add(_shake);
            PlayfieldBorder.RenderTransform = g;
            PlayfieldBorder.RenderTransformOrigin = new Point(0.5, 0.5);

            _stateFile = GetStateFilePath();
            LoadState();

            BuildModeChoices();
            EnsureSkinChoicesUpToDate(preserveSelection: false);
            UpdateActiveSkin();

            Focus();

            ResetGame(waitingStart: true);

            StartRenderLoop();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopRenderLoop();
            SaveStateSafe();
            base.OnClosed(e);
        }

        private void StartRenderLoop()
        {
            _clock = Stopwatch.StartNew();
            _lastRenderTime = _clock.Elapsed;
            _accumMs = 0;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRenderLoop()
        {
            try { CompositionTarget.Rendering -= OnRendering; } catch { }
            try { _clock?.Stop(); } catch { }
        }

        // -------------------------------
        // Mode UI
        // -------------------------------
        private void BuildModeChoices()
        {
            var items = new List<ModeChoice>
            {
                new ModeChoice(GameMode.Classic,  "Classic",  "Murs mortels, stable (bonus plus rares)"),
                new ModeChoice(GameMode.Arcade,   "Arcade",   "Wrap-around, plus fun (bonus un peu plus fréquents)"),
                new ModeChoice(GameMode.Hardcore, "Hardcore", "Accélère + murs internes (speed désactivée)"),
            };

            ModeCombo.ItemsSource = items;

            var selected = items.FirstOrDefault(x => x.Mode == _mode) ?? items.First();
            ModeCombo.SelectedItem = selected;

            if (ModeHintText != null)
                ModeHintText.Text = selected.Hint;
        }

        private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ModeCombo.SelectedItem is ModeChoice mc)
            {
                _mode = mc.Mode;
                if (ModeHintText != null)
                    ModeHintText.Text = mc.Hint;

                SaveStateSafe();
                ResetGame(waitingStart: true);
            }
        }

        // -------------------------------
        // Input
        // -------------------------------
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space)
            {
                if (_isWaitingStart)
                {
                    if (_isGameOver) ResetGame(waitingStart: false);
                    else StartRun();
                }
                else
                {
                    ResetGame(waitingStart: false);
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.P)
            {
                if (!_isGameOver && !_isWaitingStart)
                {
                    _isPaused = !_isPaused;

                    if (_isPaused)
                    {
                        Overlay.Visibility = Visibility.Visible;
                        OverlayText.Text = "PAUSE\n(P pour reprendre)";
                        ShowToast("PAUSE", TimeSpan.FromSeconds(0.8));
                    }
                    else
                    {
                        Overlay.Visibility = Visibility.Collapsed;
                        HideToast();
                    }
                }
                e.Handled = true;
                return;
            }

            if (_isGameOver || _isPaused || _isWaitingStart)
                return;

            Direction wanted;
            switch (e.Key)
            {
                // Flèches
                case Key.Up: wanted = Direction.Up; break;
                case Key.Down: wanted = Direction.Down; break;
                case Key.Left: wanted = Direction.Left; break;
                case Key.Right: wanted = Direction.Right; break;

                // ZQSD (AZERTY)
                case Key.Z: wanted = Direction.Up; break;
                case Key.S: wanted = Direction.Down; break;
                case Key.Q: wanted = Direction.Left; break;
                case Key.D: wanted = Direction.Right; break;

                default: return;
            }

            if (!IsOpposite(_dir, wanted))
                _nextDir = wanted;

            e.Handled = true;
        }

        private static bool IsOpposite(Direction a, Direction b)
        {
            return (a == Direction.Up && b == Direction.Down) ||
                   (a == Direction.Down && b == Direction.Up) ||
                   (a == Direction.Left && b == Direction.Right) ||
                   (a == Direction.Right && b == Direction.Left);
        }

        // -------------------------------
        // Rendering loop
        // -------------------------------
        private void OnRendering(object sender, EventArgs e)
        {
            var now = _clock.Elapsed;
            var delta = now - _lastRenderTime;
            _lastRenderTime = now;

            AutoHideToast();

            if (!_isPaused && !_isGameOver && !_isWaitingStart)
            {
                UpdateEffectTimers(delta.TotalSeconds);

                _accumMs += delta.TotalMilliseconds;

                double stepMs = GetCurrentStepMs();
                int safety = 0;
                while (_accumMs >= stepMs && safety < 6)
                {
                    _accumMs -= stepMs;
                    StepLogic();
                    safety++;
                }
            }

            RenderFrame(now.TotalSeconds);
            UpdateBorderVisual(now.TotalSeconds);
            UpdateScoreHud(fullRefresh: false);
        }

        private double GetCurrentStepMs()
        {
            double baseMs =
                (_mode == GameMode.Classic) ? 110.0 :
                (_mode == GameMode.Arcade) ? 100.0 :
                90.0;

            if (_mode == GameMode.Hardcore)
            {
                double scale = 1.0 - Math.Min(0.45, _score * 0.0004);
                baseMs *= Math.Max(0.55, scale);
            }

            double factor = (_speedActive && _mode != GameMode.Hardcore) ? SpeedFactor : 1.0;

            double ms = baseMs / factor;
            if (ms < 30) ms = 30;
            return ms;
        }

        // -------------------------------
        // Start / Reset
        // -------------------------------
        private void StartRun()
        {
            _isWaitingStart = false;
            _isPaused = false;
            _isGameOver = false;

            Overlay.Visibility = Visibility.Collapsed;
            HideToast();

            _accumMs = 0;
        }

        private void ResetGame(bool waitingStart)
        {
            _snake.Clear();
            _fruits.Clear();
            _walls.Clear();

            _score = 0;
            _lastHundredsBucket = 0;
            _recordThisRun = false;
            _recordScoreThisRun = 0;

            _dir = Direction.Right;
            _nextDir = Direction.Right;

            _isGameOver = false;
            _isPaused = false;
            _isWaitingStart = waitingStart;

            _scoreMultiplier = 1;
            _multiplierRemainingSec = 0;

            _speedActive = false;
            _speedRemainingSec = 0;

            _rainActive = false;
            _rainRemainingSec = 0;
            _rainExtra = 0;

            _flashUntilUtc = DateTime.MinValue;

            _accumMs = 0;

            RebuildMap(BaseCols, BaseRows, animateWindow: false);

            int startX = _cols / 2;
            int startY = _rows / 2;

            _snake.AddFirst(new Cell(startX, startY));
            _snake.AddLast(new Cell(startX - 1, startY));
            _snake.AddLast(new Cell(startX - 2, startY));
            _snake.AddLast(new Cell(startX - 3, startY));
            _snake.AddLast(new Cell(startX - 4, startY));

            if (_mode == GameMode.Hardcore)
                GenerateHardcoreWalls();

            // ✅ ICI : Classic = 5 fruits fixes
            if (_mode == GameMode.Hardcore)
                _baseFruitTarget = 1;
            else if (_mode == GameMode.Arcade)
                _baseFruitTarget = _rng.Next(ArcadeFruitMin, ArcadeFruitMax + 1);
            else
                _baseFruitTarget = ClassicFruitCount;

            EnsureFruits(targetCount: TargetFruitCount, maxAddThisCall: 999);

            UpdateScoreHud(fullRefresh: true);
            EnsureSkinChoicesUpToDate(preserveSelection: true);
            UpdateActiveSkin();

            if (_isWaitingStart)
            {
                Overlay.Visibility = Visibility.Visible;
                OverlayText.Text = "SNAKE\n\nEspace = démarrer";
                ShowToast("⏯ Appuie sur Espace pour démarrer", TimeSpan.FromSeconds(2.2));
            }
            else
            {
                Overlay.Visibility = Visibility.Collapsed;
                HideToast();
            }
        }

        // -------------------------------
        // Timers (pause-friendly)
        // -------------------------------
        private void UpdateEffectTimers(double dtSec)
        {
            if (dtSec <= 0) return;

            if (_scoreMultiplier > 1)
            {
                _multiplierRemainingSec -= dtSec;
                if (_multiplierRemainingSec <= 0)
                {
                    _multiplierRemainingSec = 0;
                    _scoreMultiplier = 1;
                }
            }

            if (_speedActive)
            {
                _speedRemainingSec -= dtSec;
                if (_speedRemainingSec <= 0)
                {
                    _speedRemainingSec = 0;
                    _speedActive = false;
                }
            }

            if (_rainActive)
            {
                _rainRemainingSec -= dtSec;
                if (_rainRemainingSec <= 0)
                {
                    _rainRemainingSec = 0;
                    _rainActive = false;
                    _rainExtra = 0;

                    TrimFruitsToTarget(targetCount: TargetFruitCount);
                }
            }
        }

        // -------------------------------
        // Map + Bitmap init
        // -------------------------------
        private void RebuildMap(int cols, int rows, bool animateWindow)
        {
            _cols = cols;
            _rows = rows;

            _bmpW = _cols * CellSize;
            _bmpH = _rows * CellSize;
            _stride = _bmpW * 4;

            _wb = new WriteableBitmap(_bmpW, _bmpH, 96, 96, PixelFormats.Bgra32, null);
            _px = new int[_bmpW * _bmpH];
            _backgroundCache = null;
            _backgroundDirty = true;

            GameImage.Source = _wb;
            GameImage.Width = _bmpW;
            GameImage.Height = _bmpH;

            // ✅ force le conteneur à demander exactement la bonne taille
            PlayfieldBorder.Width = _bmpW + 8; // 2 (BorderThickness) *2 + 2 (Padding) *2 = 8
            PlayfieldBorder.Height = _bmpH + 8;

            MapInfoText.Text = $"Map: {_cols}×{_rows}";

            double targetW = _baseWindowWidth + (_cols - BaseCols) * CellSize;
            double targetH = _baseWindowHeight + (_rows - BaseRows) * CellSize;

            if (animateWindow)
            {
                AnimateWindowSize(targetW, targetH);
                PunchPlayfield();
            }
            else
            {
                this.Width = targetW;
                this.Height = targetH;
            }
        }

        private void AnimateWindowSize(double w, double h)
        {
            BeginAnimation(Window.WidthProperty, null);
            BeginAnimation(Window.HeightProperty, null);

            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            BeginAnimation(Window.WidthProperty, new DoubleAnimation { To = w, Duration = dur, EasingFunction = ease });
            BeginAnimation(Window.HeightProperty, new DoubleAnimation { To = h, Duration = dur, EasingFunction = ease });
        }

        private void PunchPlayfield()
        {
            var g = PlayfieldBorder.RenderTransform as TransformGroup;
            var scale = g?.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (scale == null) return;

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var dur = TimeSpan.FromMilliseconds(180);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation { From = 1.0, To = 1.04, Duration = dur, AutoReverse = true, EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation { From = 1.0, To = 1.04, Duration = dur, AutoReverse = true, EasingFunction = ease });
        }

        private void ShakePlayfield(double strength = 3.0)
        {
            _shake.BeginAnimation(TranslateTransform.XProperty, null);
            _shake.BeginAnimation(TranslateTransform.YProperty, null);

            var dur = TimeSpan.FromMilliseconds(180);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            _shake.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = -strength, To = strength, Duration = dur, AutoReverse = true, EasingFunction = ease });
            _shake.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { From = strength, To = -strength, Duration = dur, AutoReverse = true, EasingFunction = ease });
        }

        private void PopScoreText()
        {
            ScorePopScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ScorePopScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var dur = TimeSpan.FromMilliseconds(160);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            ScorePopScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation { From = 1.0, To = 1.12, Duration = dur, AutoReverse = true, EasingFunction = ease });
            ScorePopScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation { From = 1.0, To = 1.12, Duration = dur, AutoReverse = true, EasingFunction = ease });
        }

        private void ApplyMapSizeForScore()
        {
            int cols = BaseCols;
            int rows = BaseRows;

            if (_score >= GrowAtScore1) { cols += 1; rows += 1; }
            if (_score >= GrowAtScore2) { cols += 1; rows += 1; }

            if (cols != _cols || rows != _rows)
            {
                RebuildMap(cols, rows, animateWindow: true);

                _flashUntilUtc = DateTime.UtcNow.Add(FlashDuration);
                ShowToast($"⬛ LEVEL UP : Map → {_cols}×{_rows}", LevelUpMsgDuration);

                if (_mode == GameMode.Hardcore)
                    AddHardcoreWallSegments(countSegments: 1);

                EnsureFruits(targetCount: TargetFruitCount, maxAddThisCall: 2);
            }
        }

        // -------------------------------
        // Hardcore walls
        // -------------------------------
        private void GenerateHardcoreWalls()
        {
            int segments = _rng.Next(HardcoreWallSegmentsMin, HardcoreWallSegmentsMax + 1);
            AddHardcoreWallSegments(segments);
            ShowToast($"🧱 Hardcore : {segments} murs générés", TimeSpan.FromSeconds(1.2));
        }

        private void AddHardcoreWallSegments(int countSegments)
        {
            var head = _snake.First.Value;

            int addedSegments = 0;
            int tries = 0;

            while (addedSegments < countSegments && tries < 200)
            {
                tries++;

                bool horizontal = _rng.NextDouble() < 0.5;
                int len = _rng.Next(HardcoreWallLenMin, HardcoreWallLenMax + 1);

                int x = _rng.Next(2, Math.Max(3, _cols - 2));
                int y = _rng.Next(2, Math.Max(3, _rows - 2));

                var cells = new List<Cell>();
                for (int i = 0; i < len; i++)
                {
                    int cx = x + (horizontal ? i : 0);
                    int cy = y + (horizontal ? 0 : i);

                    if (cx < 0 || cx >= _cols || cy < 0 || cy >= _rows)
                        break;

                    var c = new Cell(cx, cy);

                    int manhattan = Math.Abs(c.X - head.X) + Math.Abs(c.Y - head.Y);
                    if (manhattan < HardcoreWallMinDistFromHead) { cells.Clear(); break; }

                    if (_snake.Any(s => s.Equals(c))) { cells.Clear(); break; }
                    if (_walls.Contains(c)) { cells.Clear(); break; }

                    cells.Add(c);
                }

                if (cells.Count < 3) continue;

                foreach (var c in cells)
                    _walls.Add(c);

                for (int i = _fruits.Count - 1; i >= 0; i--)
                    if (_walls.Contains(_fruits[i].Pos))
                        _fruits.RemoveAt(i);

                addedSegments++;
            }
        }

        // -------------------------------
        // Step logic
        // -------------------------------
        private void StepLogic()
        {
            if (_rainActive && _fruits.Count < TargetFruitCount)
                EnsureFruits(targetCount: TargetFruitCount, maxAddThisCall: 2);

            _dir = _nextDir;

            var head = _snake.First.Value;
            Cell next;

            switch (_dir)
            {
                case Direction.Up: next = new Cell(head.X, head.Y - 1); break;
                case Direction.Down: next = new Cell(head.X, head.Y + 1); break;
                case Direction.Left: next = new Cell(head.X - 1, head.Y); break;
                default: next = new Cell(head.X + 1, head.Y); break;
            }

            if (_mode == GameMode.Arcade)
            {
                if (next.X < 0) next = new Cell(_cols - 1, next.Y);
                else if (next.X >= _cols) next = new Cell(0, next.Y);

                if (next.Y < 0) next = new Cell(next.X, _rows - 1);
                else if (next.Y >= _rows) next = new Cell(next.X, 0);
            }
            else
            {
                if (next.X < 0 || next.X >= _cols || next.Y < 0 || next.Y >= _rows)
                {
                    GameOver("💥 Mur.");
                    return;
                }
            }

            if (_mode == GameMode.Hardcore && _walls.Contains(next))
            {
                GameOver("🧱 Mur interne.");
                return;
            }

            int fruitIndex = _fruits.FindIndex(f => f.Pos.Equals(next));
            bool willEat = fruitIndex >= 0;

            var tail = _snake.Last.Value;
            bool hitsBody = _snake.Any(c => c.Equals(next));
            if (hitsBody)
            {
                bool isHittingTail = next.Equals(tail);
                if (!(isHittingTail && !willEat))
                {
                    GameOver("🐍 Auto-collision.");
                    return;
                }
            }

            _snake.AddFirst(next);

            if (willEat)
            {
                var eaten = _fruits[fruitIndex];
                _fruits.RemoveAt(fruitIndex);

                HandleEatenFruit(eaten);

                int maxAdd = _rainActive ? 2 : 999;
                EnsureFruits(targetCount: TargetFruitCount, maxAddThisCall: maxAdd);
            }
            else
            {
                _snake.RemoveLast();
            }
        }

        private void HandleEatenFruit(Fruit fruit)
        {
            switch (fruit.Type)
            {
                case FruitType.Multiplier:
                    _scoreMultiplier = Math.Min(_scoreMultiplier + 1, MultiplierMax);
                    _multiplierRemainingSec = GetMultiplierDuration().TotalSeconds;
                    AddScore(1);
                    ShowToast($"⚡ Multiplicateur x{_scoreMultiplier}", TimeSpan.FromSeconds(1.0));
                    PunchPlayfield();
                    break;

                case FruitType.Speed:
                    if (_mode == GameMode.Hardcore)
                    {
                        AddScore(1);
                        break;
                    }
                    _speedActive = true;
                    _speedRemainingSec = GetSpeedDuration().TotalSeconds;
                    AddScore(1);
                    ShowToast("💨 Speed +50% !", TimeSpan.FromSeconds(1.0));
                    PunchPlayfield();
                    break;

                case FruitType.Rain:
                    StartRain();
                    AddScore(1);
                    ShowToast("🌧️ Pluie de fruits !", TimeSpan.FromSeconds(1.0));
                    ShakePlayfield(2.6);
                    break;

                default:
                    AddScore(1);
                    break;
            }
        }

        private TimeSpan GetSpeedDuration() => TimeSpan.FromSeconds(30);
        private TimeSpan GetMultiplierDuration() => TimeSpan.FromSeconds(30);
        private TimeSpan GetRainDuration() => (_mode == GameMode.Hardcore) ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(10);

        private void StartRain()
        {
            _rainActive = true;
            _rainRemainingSec = GetRainDuration().TotalSeconds;

            _rainExtra = RainExtraTarget;
            _flashUntilUtc = DateTime.UtcNow.Add(FlashDuration);

            EnsureFruits(targetCount: TargetFruitCount, maxAddThisCall: 2);
        }

        private void AddScore(int basePoints)
        {
            int oldGlobalTop = GlobalTopScore;

            int gained = basePoints * _scoreMultiplier;
            _score += gained;

            int bucket = _score / 100;
            if (bucket > _lastHundredsBucket)
            {
                _lastHundredsBucket = bucket;
                PopScoreText();
                ShowToast($"🏁 Palier atteint : {bucket * 100}", TimeSpan.FromSeconds(1.1));
            }

            if (_score > CurrentModeTopScore)
            {
                SetCurrentModeTopScore(_score);
                _recordThisRun = true;
                _recordScoreThisRun = _score;
                SaveStateSafe();
            }

            ApplyMapSizeForScore();

            UpdateScoreHud(fullRefresh: true);

            int newGlobalTop = GlobalTopScore;
            if (newGlobalTop != oldGlobalTop)
                EnsureSkinChoicesUpToDate(preserveSelection: true);
        }

        // -------------------------------
        // Fruits spawn
        // -------------------------------
        private void EnsureFruits(int targetCount, int maxAddThisCall)
        {
            if (_snake.Count + _fruits.Count + _walls.Count >= _cols * _rows)
            {
                GameOver("🏆 Grille pleine. Victoire.");
                return;
            }

            int added = 0;
            while (_fruits.Count < targetCount && _fruits.Count < MaxTotalFruits && added < maxAddThisCall)
            {
                var type = RollFruitType();
                var pos = GetRandomFreeCellForType(type);
                if (pos == null) break;

                _fruits.Add(Fruit.Create(pos.Value, type));
                added++;
            }
        }

        private void TrimFruitsToTarget(int targetCount)
        {
            if (_fruits.Count <= targetCount) return;

            for (int i = _fruits.Count - 1; i >= 0 && _fruits.Count > targetCount; i--)
                if (_fruits[i].Type == FruitType.Normal)
                    _fruits.RemoveAt(i);

            while (_fruits.Count > targetCount)
                _fruits.RemoveAt(_fruits.Count - 1);
        }

        private FruitType RollFruitType()
        {
            double rain = RainFruitChance;
            double speed = (_mode == GameMode.Hardcore) ? 0.0 : SpeedFruitChance;
            double mult = MultiplierFruitChance;

            // Classic = bonus ÷2 (comme demandé)
            if (_mode == GameMode.Classic)
            {
                rain *= 0.5;
                speed *= 0.5;
                mult *= 0.5;
            }

            if (_mode == GameMode.Arcade)
            {
                rain *= 1.2;
                speed *= 1.2;
                mult *= 1.2;
            }
            else if (_mode == GameMode.Hardcore)
            {
                rain *= 0.55;
                mult *= 0.90;
            }

            double totalBonus = rain + speed + mult;
            if (totalBonus > 0.65)
            {
                double scale = 0.65 / totalBonus;
                rain *= scale; speed *= scale; mult *= scale;
            }

            double r = _rng.NextDouble();
            if (r < rain) return FruitType.Rain;
            if (r < rain + speed) return FruitType.Speed;
            if (r < rain + speed + mult) return FruitType.Multiplier;
            return FruitType.Normal;
        }

        private Cell? GetRandomFreeCellForType(FruitType type)
        {
            var head = _snake.First.Value;

            for (int tries = 0; tries < 800; tries++)
            {
                var c = new Cell(_rng.Next(0, _cols), _rng.Next(0, _rows));

                if (_walls.Contains(c)) continue;
                if (_snake.Any(s => s.Equals(c))) continue;
                if (_fruits.Any(f => f.Pos.Equals(c))) continue;

                if (type != FruitType.Normal)
                {
                    int manhattan = Math.Abs(c.X - head.X) + Math.Abs(c.Y - head.Y);
                    if (manhattan < BonusSpawnMinManhattan) continue;
                }

                return c;
            }

            return null;
        }

        // -------------------------------
        // Rendering + UI + persistence + helpers
        // (inchangés)
        // -------------------------------

        private void RenderFrame(double t)
        {
            if (_wb == null || _px == null || _activeSkin == null)
                return;

            EnsureBackgroundCache();

            Buffer.BlockCopy(
                _backgroundCache,
                0,
                _px,
                0,
                _backgroundCache.Length * sizeof(int));

            if (_mode == GameMode.Hardcore && _walls.Count > 0)
            {
                Color baseWall = _activeSkin.WallColor;

                foreach (var c in _walls)
                {
                    DrawWallCell(c, baseWall);
                }
            }

            foreach (var fruit in _fruits)
            {
                Color c = _activeSkin.GetFruitColor(fruit.Type);

                if (fruit.IsPulsing)
                {
                    double s = 0.5 + 0.5 * Math.Sin(2 * Math.PI * PulseHz * t);
                    double k = PulseStrength * s;
                    c = LerpColor(c, Colors.White, k);
                }

                DrawFruitPremium(fruit.Pos, c, fruit.Type, t);
            }

            int segIndex = 0;
            int snakeCount = Math.Max(1, _snake.Count);

            foreach (var seg in _snake)
            {
                bool isHead = segIndex == 0;

                if (isHead)
                {
                    DrawHeadPremium(seg, _dir, _activeSkin.HeadColor);

                    if (_speedActive && _mode != GameMode.Hardcore)
                    {
                        DrawSpeedParticles(seg, _dir, t);
                    }
                }
                else
                {
                    double progress = segIndex / (double)snakeCount;
                    double shade = 1.0 - Math.Min(0.28, progress * 0.34);

                    Color body = Shade(_activeSkin.BodyColor, shade);
                    DrawSnakeBodyCell(seg, body, segIndex);
                }

                segIndex++;
            }

            _wb.WritePixels(new Int32Rect(0, 0, _bmpW, _bmpH), _px, _stride, 0);
        }

        private void EnsureBackgroundCache()
        {
            if (_backgroundCache == null || _backgroundCache.Length != _bmpW * _bmpH)
            {
                _backgroundCache = new int[_bmpW * _bmpH];
                _backgroundDirty = true;
            }

            Color bg = _playfieldBackgroundBrush.Color;

            Color gridBase = _activeSkin.GridColor;
            Color grid = Color.FromArgb(22, gridBase.R, gridBase.G, gridBase.B);

            bool sameColors =
                _cachedBackgroundColor.Equals(bg) &&
                _cachedGridColor.Equals(grid);

            if (!_backgroundDirty && sameColors)
                return;

            _cachedBackgroundColor = bg;
            _cachedGridColor = grid;

            int w = _bmpW;
            int h = _bmpH;

            for (int y = 0; y < h; y++)
            {
                double vertical = h <= 1 ? 0.0 : y / (double)(h - 1);
                double vignetteY = Math.Abs(vertical - 0.5) * 2.0;

                double f = 0.92 + 0.10 * vertical - 0.05 * vignetteY;
                Color row = Shade(bg, f);
                int argb = Pack(row);

                int rowStart = y * w;

                for (int x = 0; x < w; x++)
                {
                    double horizontal = w <= 1 ? 0.0 : x / (double)(w - 1);
                    double vignetteX = Math.Abs(horizontal - 0.5) * 2.0;
                    double edge = Math.Max(vignetteX, vignetteY);

                    if (edge > 0.72)
                    {
                        double darken = 1.0 - ((edge - 0.72) * 0.16);
                        Color darker = Shade(row, darken);
                        _backgroundCache[rowStart + x] = Pack(darker);
                    }
                    else
                    {
                        _backgroundCache[rowStart + x] = argb;
                    }
                }
            }

            int gridC = Pack(grid);

            for (int gx = 0; gx < w; gx += CellSize)
                DrawVLineOnBuffer(_backgroundCache, gx, 0, h - 1, gridC);

            for (int gy = 0; gy < h; gy += CellSize)
                DrawHLineOnBuffer(_backgroundCache, 0, w - 1, gy, gridC);

            DrawVLineOnBuffer(_backgroundCache, w - 1, 0, h - 1, gridC);
            DrawHLineOnBuffer(_backgroundCache, 0, w - 1, h - 1, gridC);

            _backgroundDirty = false;
        }
        private void DrawVLineOnBuffer(int[] buffer, int x, int y1, int y2, int color)
        {
            if (buffer == null || x < 0 || x >= _bmpW)
                return;

            if (y1 > y2)
            {
                int tmp = y1;
                y1 = y2;
                y2 = tmp;
            }

            y1 = Math.Max(0, y1);
            y2 = Math.Min(_bmpH - 1, y2);

            for (int y = y1; y <= y2; y++)
            {
                buffer[y * _bmpW + x] = color;
            }
        }

        private void DrawHLineOnBuffer(int[] buffer, int x1, int x2, int y, int color)
        {
            if (buffer == null || y < 0 || y >= _bmpH)
                return;

            if (x1 > x2)
            {
                int tmp = x1;
                x1 = x2;
                x2 = tmp;
            }

            x1 = Math.Max(0, x1);
            x2 = Math.Min(_bmpW - 1, x2);

            int offset = y * _bmpW;

            for (int x = x1; x <= x2; x++)
            {
                buffer[offset + x] = color;
            }
        }

        private void DrawSnakeBodyCell(Cell cell, Color baseColor, int index)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            Color shadow = Color.FromArgb(70, 0, 0, 0);
            FillRect(x0 + 3, y0 + 3, CellSize - 4, CellSize - 4, Pack(shadow));

            Color edge = Darken(baseColor, 0.20);
            Color center = Lighten(baseColor, 0.12);
            Color highlight = Lighten(baseColor, 0.32);

            FillRect(x0 + 2, y0 + 2, CellSize - 4, CellSize - 4, Pack(edge));
            FillRect(x0 + 3, y0 + 3, CellSize - 6, CellSize - 6, Pack(baseColor));
            FillRect(x0 + 5, y0 + 5, CellSize - 10, CellSize - 10, Pack(center));

            DrawHLine(x0 + 4, x0 + CellSize - 5, y0 + 3, Pack(Color.FromArgb(120, highlight.R, highlight.G, highlight.B)));
            DrawVLine(x0 + 3, y0 + 4, y0 + CellSize - 5, Pack(Color.FromArgb(70, highlight.R, highlight.G, highlight.B)));

            if (index % 3 == 0)
            {
                Color dot = Lighten(baseColor, 0.42);
                SetPixel(x0 + CellSize / 2, y0 + CellSize / 2, Pack(Color.FromArgb(115, dot.R, dot.G, dot.B)));
            }
        }

        private void DrawHeadPremium(Cell cell, Direction dir, Color baseColor)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            Color shadow = Color.FromArgb(90, 0, 0, 0);
            FillRect(x0 + 3, y0 + 3, CellSize - 4, CellSize - 4, Pack(shadow));

            Color edge = Darken(baseColor, 0.22);
            Color hi = Lighten(baseColor, 0.36);

            FillRect(x0 + 1, y0 + 1, CellSize - 2, CellSize - 2, Pack(edge));

            for (int py = 2; py < CellSize - 2; py++)
            {
                for (int px = 2; px < CellSize - 2; px++)
                {
                    double f;

                    switch (dir)
                    {
                        case Direction.Right:
                            f = px / (double)(CellSize - 1);
                            break;

                        case Direction.Left:
                            f = 1.0 - px / (double)(CellSize - 1);
                            break;

                        case Direction.Down:
                            f = py / (double)(CellSize - 1);
                            break;

                        default:
                            f = 1.0 - py / (double)(CellSize - 1);
                            break;
                    }

                    Color c = LerpColor(baseColor, hi, 0.55 * f);
                    SetPixel(x0 + px, y0 + py, Pack(c));
                }
            }

            DrawRectOutline(x0 + 1, y0 + 1, CellSize - 2, CellSize - 2, Pack(Color.FromArgb(125, 255, 255, 255)));

            DrawEyesPremium(cell, dir);
        }

        private void DrawEyesPremium(Cell cell, Direction dir)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            int white = Pack(Color.FromArgb(235, 255, 255, 255));
            int pupil = Pack(Color.FromArgb(235, 0, 0, 0));

            int ax = x0 + CellSize / 2;
            int ay = y0 + CellSize / 2;

            int ex1 = ax - 4;
            int ex2 = ax + 3;
            int ey1 = ay - 4;
            int ey2 = ay - 4;

            int pxOffset1 = 0;
            int pxOffset2 = 0;
            int pyOffset1 = 0;
            int pyOffset2 = 0;

            switch (dir)
            {
                case Direction.Down:
                    ey1 = ay + 3;
                    ey2 = ay + 3;
                    pyOffset1 = 1;
                    pyOffset2 = 1;
                    break;

                case Direction.Left:
                    ex1 = ax - 5;
                    ex2 = ax - 5;
                    ey1 = ay - 4;
                    ey2 = ay + 3;
                    pxOffset1 = -1;
                    pxOffset2 = -1;
                    break;

                case Direction.Right:
                    ex1 = ax + 5;
                    ex2 = ax + 5;
                    ey1 = ay - 4;
                    ey2 = ay + 3;
                    pxOffset1 = 1;
                    pxOffset2 = 1;
                    break;

                default:
                    pyOffset1 = -1;
                    pyOffset2 = -1;
                    break;
            }

            DrawCircle(ex1, ey1, 2, white, true);
            DrawCircle(ex2, ey2, 2, white, true);

            SetPixel(ex1 + pxOffset1, ey1 + pyOffset1, pupil);
            SetPixel(ex2 + pxOffset2, ey2 + pyOffset2, pupil);
        }

        private void DrawWallCell(Cell cell, Color baseColor)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            Color dark = Darken(baseColor, 0.24);
            Color light = Lighten(baseColor, 0.16);

            FillRect(x0 + 1, y0 + 1, CellSize - 2, CellSize - 2, Pack(dark));
            FillRect(x0 + 3, y0 + 3, CellSize - 6, CellSize - 6, Pack(baseColor));

            DrawHLine(x0 + 3, x0 + CellSize - 4, y0 + 5, Pack(Color.FromArgb(110, light.R, light.G, light.B)));
            DrawHLine(x0 + 3, x0 + CellSize - 4, y0 + CellSize - 5, Pack(Color.FromArgb(130, 0, 0, 0)));

            int crack = Pack(Color.FromArgb(110, 0, 0, 0));
            SetPixel(x0 + 6, y0 + 6, crack);
            SetPixel(x0 + 7, y0 + 7, crack);
            SetPixel(x0 + 8, y0 + 8, crack);
            SetPixel(x0 + 8, y0 + 9, crack);
        }

        private void DrawFruitPremium(Cell cell, Color baseColor, FruitType type, double t)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            int cx = x0 + CellSize / 2;
            int cy = y0 + CellSize / 2;

            double pulse = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2.0 * 2.0);
            int haloAlpha = 45 + (int)(pulse * 35);

            Color halo = Color.FromArgb((byte)haloAlpha, baseColor.R, baseColor.G, baseColor.B);
            DrawCircle(cx, cy, (int)(CellSize * 0.43), Pack(halo), true);

            Color shadow = Color.FromArgb(85, 0, 0, 0);
            DrawCircle(cx + 1, cy + 2, (int)(CellSize * 0.29), Pack(shadow), true);

            Color body = baseColor;
            Color hi = Lighten(baseColor, 0.38);
            Color edge = Darken(baseColor, 0.20);

            DrawCircle(cx, cy, (int)(CellSize * 0.31), Pack(edge), true);
            DrawCircle(cx, cy, (int)(CellSize * 0.26), Pack(body), true);
            DrawCircle(cx - 3, cy - 3, 2, Pack(Color.FromArgb(170, hi.R, hi.G, hi.B)), true);

            DrawFruitIcon(cx, cy, type);
        }

        private void DrawFruitIcon(int cx, int cy, FruitType type)
        {
            int dark = Pack(Color.FromArgb(185, 0, 0, 0));
            int light = Pack(Color.FromArgb(210, 255, 255, 255));

            switch (type)
            {
                case FruitType.Multiplier:
                    DrawHLine(cx - 3, cx + 3, cy, dark);
                    DrawVLine(cx, cy - 3, cy + 3, dark);
                    SetPixel(cx - 2, cy - 2, light);
                    SetPixel(cx + 2, cy + 2, light);
                    break;

                case FruitType.Speed:
                    DrawHLine(cx - 4, cx + 2, cy - 2, dark);
                    DrawHLine(cx - 2, cx + 4, cy, dark);
                    DrawHLine(cx - 4, cx + 2, cy + 2, dark);
                    SetPixel(cx + 4, cy, light);
                    break;

                case FruitType.Rain:
                    DrawVLine(cx - 2, cy - 3, cy + 2, dark);
                    DrawVLine(cx + 2, cy - 3, cy + 2, dark);
                    SetPixel(cx, cy + 3, light);
                    break;

                default:
                    DrawCircle(cx, cy, 1, dark, true);
                    SetPixel(cx - 2, cy - 2, light);
                    break;
            }
        }
        private void DrawFruit(Cell cell, Color baseColor, FruitType type)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            Color halo = Color.FromArgb(70, baseColor.R, baseColor.G, baseColor.B);
            DrawCircle(x0 + CellSize / 2, y0 + CellSize / 2, (int)(CellSize * 0.40), Pack(halo), fill: true);

            DrawCircle(x0 + CellSize / 2, y0 + CellSize / 2, (int)(CellSize * 0.28), Pack(baseColor), fill: true);

            Color hi = Color.FromArgb(160, 255, 255, 255);
            DrawCircle(x0 + CellSize / 2 - 3, y0 + CellSize / 2 - 3, (int)(CellSize * 0.10), Pack(hi), fill: true);

            int cx = x0 + CellSize / 2;
            int cy = y0 + CellSize / 2;
            int icon = Pack(Color.FromArgb(180, 0, 0, 0));

            if (type == FruitType.Multiplier) { SetPixel(cx, cy, icon); SetPixel(cx - 1, cy, icon); SetPixel(cx + 1, cy, icon); }
            else if (type == FruitType.Speed) { SetPixel(cx, cy, icon); SetPixel(cx + 1, cy, icon); SetPixel(cx + 2, cy, icon); }
            else if (type == FruitType.Rain) { SetPixel(cx, cy, icon); SetPixel(cx, cy + 1, icon); SetPixel(cx, cy - 1, icon); }
        }

        private void DrawHead(Cell cell, Direction dir, Color baseColor)
        {
            Color hi = Lighten(baseColor, 0.30);

            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            for (int py = 0; py < CellSize; py++)
            {
                for (int px = 0; px < CellSize; px++)
                {
                    double f;
                    switch (dir)
                    {
                        case Direction.Right: f = px / (double)(CellSize - 1); break;
                        case Direction.Left: f = 1.0 - (px / (double)(CellSize - 1)); break;
                        case Direction.Down: f = py / (double)(CellSize - 1); break;
                        default: f = 1.0 - (py / (double)(CellSize - 1)); break;
                    }

                    Color c = LerpColor(baseColor, hi, 0.55 * f);
                    SetPixel(x0 + px, y0 + py, Pack(c));
                }
            }

            int outline = Pack(Color.FromArgb(120, 255, 255, 255));
            DrawRectOutline(x0 + 1, y0 + 1, CellSize - 2, CellSize - 2, outline);

            DrawEyes(cell, dir);
        }

        private void DrawEyes(Cell cell, Direction dir)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            int eyeW = Pack(Color.FromArgb(220, 255, 255, 255));
            int pupil = Pack(Color.FromArgb(220, 0, 0, 0));

            int ax = x0 + CellSize / 2;
            int ay = y0 + CellSize / 2;

            int ex1 = ax - 4, ex2 = ax + 2;
            int ey1 = ay - 3, ey2 = ay - 3;

            if (dir == Direction.Down) { ey1 = ay + 1; ey2 = ay + 1; }
            if (dir == Direction.Left) { ex1 = ax - 5; ex2 = ax - 1; ey1 = ay - 2; ey2 = ay + 2; }
            if (dir == Direction.Right) { ex1 = ax + 1; ex2 = ax + 5; ey1 = ay - 2; ey2 = ay + 2; }

            SetPixel(ex1, ey1, eyeW);
            SetPixel(ex2, ey2, eyeW);

            SetPixel(ex1 + (dir == Direction.Left ? -1 : 1), ey1, pupil);
            SetPixel(ex2 + (dir == Direction.Right ? 1 : -1), ey2, pupil);
        }

        private void DrawSpeedParticles(Cell head, Direction dir, double t)
        {
            int frame = (int)(t * 60.0);

            for (int i = 0; i < 8; i++)
            {
                int seed = frame * 997 + i * 7919 + (int)dir * 31;
                double r1 = Hash01(seed);
                double r2 = Hash01(seed + 17);

                int bx = head.X * CellSize + CellSize / 2;
                int by = head.Y * CellSize + CellSize / 2;

                int dx = 0, dy = 0;
                switch (dir)
                {
                    case Direction.Right: dx = -1; break;
                    case Direction.Left: dx = 1; break;
                    case Direction.Up: dy = 1; break;
                    case Direction.Down: dy = -1; break;
                }

                int px = bx + dx * (8 + (int)(r1 * 10)) + (int)((r2 - 0.5) * 10);
                int py = by + dy * (8 + (int)(r1 * 10)) + (int)((r1 - 0.5) * 10);

                Color c = Color.FromArgb((byte)(120 + r1 * 80), 255, 255, 255);
                SetPixel(px, py, Pack(c));
                SetPixel(px + 1, py, Pack(Color.FromArgb(90, 255, 255, 255)));
            }
        }

        private static double Hash01(int x)
        {
            unchecked
            {
                uint n = (uint)x;
                n ^= n >> 16;
                n *= 0x7feb352d;
                n ^= n >> 15;
                n *= 0x846ca68b;
                n ^= n >> 16;
                return (n & 0x00FFFFFF) / (double)0x01000000;
            }
        }

        private void DrawBevelCell(Cell cell, Color baseColor, double highlight, double shadow)
        {
            int x0 = cell.X * CellSize;
            int y0 = cell.Y * CellSize;

            FillRect(x0 + 1, y0 + 1, CellSize - 2, CellSize - 2, Pack(baseColor));

            Color hi = Lighten(baseColor, highlight);
            Color sh = Darken(baseColor, shadow);

            int chi = Pack(Color.FromArgb(200, hi.R, hi.G, hi.B));
            int csh = Pack(Color.FromArgb(200, sh.R, sh.G, sh.B));

            DrawHLine(x0 + 1, x0 + CellSize - 2, y0 + 1, chi);
            DrawVLine(x0 + 1, y0 + 1, y0 + CellSize - 2, chi);

            DrawHLine(x0 + 1, x0 + CellSize - 2, y0 + CellSize - 2, csh);
            DrawVLine(x0 + CellSize - 2, y0 + 1, y0 + CellSize - 2, csh);
        }

        private void UpdateBorderVisual(double t)
        {
            bool anyEffect = (_mode != GameMode.Hardcore && _speedActive) || _rainActive || _scoreMultiplier > 1;
            bool flash = DateTime.UtcNow < _flashUntilUtc;

            double alpha;
            if (flash) alpha = 0.85;
            else if (anyEffect)
            {
                double s = 0.5 + 0.5 * Math.Sin(2 * Math.PI * BorderPulseHz * t);
                alpha = BorderPulseMinAlpha + (BorderPulseMaxAlpha - BorderPulseMinAlpha) * s;
            }
            else alpha = 0.22;

            _playfieldBorderBrush.Color = Color.FromArgb((byte)(alpha * 255), 255, 255, 255);
        }

        private void UpdateScoreHud(bool fullRefresh)
        {
            ScoreText.Text = $"Score: {_score}";
            TopText.Text = $"Top Mode: {CurrentModeTopScore} • Global: {GlobalTopScore}";

            ModeText.Text = $"Mode: {_mode}";
            MapInfoText.Text = $"Map: {_cols}×{_rows}";

            if (_scoreMultiplier <= 1)
            {
                MultiplierText.Text = "x1";
                MultiplierBarScale.ScaleX = 0;
            }
            else
            {
                double secs = Math.Max(0, _multiplierRemainingSec);
                MultiplierText.Text = $"x{_scoreMultiplier} • {Math.Ceiling(secs)}s";
                MultiplierBarScale.ScaleX = Clamp01(secs / GetMultiplierDuration().TotalSeconds);
            }

            if (_mode == GameMode.Hardcore)
            {
                SpeedBadge.Visibility = Visibility.Collapsed;
                SpeedBarScale.ScaleX = 0;
            }
            else if (_speedActive)
            {
                double secs = Math.Max(0, _speedRemainingSec);
                SpeedText.Text = $"Speed x1.5 • {Math.Ceiling(secs)}s";
                SpeedBadge.Visibility = Visibility.Visible;
                SpeedBarScale.ScaleX = Clamp01(secs / GetSpeedDuration().TotalSeconds);
            }
            else
            {
                SpeedBadge.Visibility = Visibility.Collapsed;
                SpeedBarScale.ScaleX = 0;
            }

            if (_rainActive)
            {
                double secs = Math.Max(0, _rainRemainingSec);
                RainText.Text = $"Pluie • {Math.Ceiling(secs)}s";
                RainBadge.Visibility = Visibility.Visible;
                RainBarScale.ScaleX = Clamp01(secs / GetRainDuration().TotalSeconds);
            }
            else
            {
                RainBadge.Visibility = Visibility.Collapsed;
                RainBarScale.ScaleX = 0;
            }

            if (fullRefresh)
                UpdateActiveSkin();
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private void SkinCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SkinCombo.SelectedItem is SkinChoice choice)
            {
                if (choice.IsAuto) _skinAuto = true;
                else { _skinAuto = false; _manualSkin = choice.SkinId; }

                UpdateActiveSkin();
                SaveStateSafe();
            }
        }

        private void EnsureSkinChoicesUpToDate(bool preserveSelection)
        {
            int global = GlobalTopScore;
            if (SkinCombo.ItemsSource == null || _lastGlobalTopForSkinList != global)
            {
                RebuildSkinChoices(preserveSelection);
                _lastGlobalTopForSkinList = global;
            }
        }

        private void RebuildSkinChoices(bool preserveSelection)
        {
            var previous = SkinCombo.SelectedItem as SkinChoice;

            var choices = new List<SkinChoice>
            {
                new SkinChoice { Display = "Auto (par score)", IsEnabled = true, IsAuto = true, SkinId = SkinId.Classic }
            };

            int globalTop = GlobalTopScore;

            foreach (var s in _skins.OrderBy(x => x.UnlockScore))
            {
                bool unlocked = globalTop >= s.UnlockScore;
                string label = unlocked ? $"{s.Name} (≥ {s.UnlockScore})" : $"🔒 {s.Name} (≥ {s.UnlockScore})";

                choices.Add(new SkinChoice
                {
                    Display = label,
                    IsEnabled = unlocked,
                    IsAuto = false,
                    SkinId = s.Id
                });
            }

            SkinCombo.ItemsSource = choices;

            if (preserveSelection && previous != null)
            {
                var match = choices.FirstOrDefault(c => c.IsAuto == previous.IsAuto && c.SkinId == previous.SkinId);
                if (match != null) { SkinCombo.SelectedItem = match; return; }
            }

            if (_skinAuto) SkinCombo.SelectedItem = choices.First();
            else
            {
                var manual = choices.FirstOrDefault(c => !c.IsAuto && c.SkinId == _manualSkin && c.IsEnabled);
                SkinCombo.SelectedItem = manual ?? choices.First();
                if (manual == null) _skinAuto = true;
            }
        }

        private void UpdateActiveSkin()
        {
            SkinDefinition chosen;

            if (_skinAuto)
            {
                chosen = _skins.OrderBy(s => s.UnlockScore)
                               .Where(s => _score >= s.UnlockScore)
                               .LastOrDefault()
                         ?? _skins.First(s => s.Id == SkinId.Classic);
            }
            else
            {
                var wanted = _skins.FirstOrDefault(s => s.Id == _manualSkin) ?? _skins.First(s => s.Id == SkinId.Classic);
                chosen = (GlobalTopScore >= wanted.UnlockScore) ? wanted : _skins.First(s => s.Id == SkinId.Classic);
            }

            if (_activeSkin != null && _activeSkin.Id == chosen.Id)
                return;

            AnimatePlayfieldBackground(to: chosen.BackgroundColor);

            _activeSkin = chosen;
            _backgroundDirty = true;

            ScanlinesOverlay.Opacity = _activeSkin.ScanlinesOpacity;
            VignetteOverlay.Opacity = _activeSkin.VignetteOpacity;

            ApplyThemeToHudBadges();
        }

        private void ApplyThemeToHudBadges()
        {
            if (_activeSkin == null) return;

            var mult = MakePastel(_activeSkin.FruitMultiplierColor, 0.68, 0.88);
            var speed = MakePastel(_activeSkin.FruitSpeedColor, 0.68, 0.88);
            var rain = MakePastel(_activeSkin.FruitRainColor, 0.70, 0.88);

            MultiplierBadge.Background = new SolidColorBrush(mult);
            SpeedBadge.Background = new SolidColorBrush(speed);
            RainBadge.Background = new SolidColorBrush(rain);

            MultiplierBarFill.Fill = new SolidColorBrush(Color.FromArgb(190, _activeSkin.FruitMultiplierColor.R, _activeSkin.FruitMultiplierColor.G, _activeSkin.FruitMultiplierColor.B));
            SpeedBarFill.Fill = new SolidColorBrush(Color.FromArgb(190, _activeSkin.FruitSpeedColor.R, _activeSkin.FruitSpeedColor.G, _activeSkin.FruitSpeedColor.B));
            RainBarFill.Fill = new SolidColorBrush(Color.FromArgb(190, _activeSkin.FruitRainColor.R, _activeSkin.FruitRainColor.G, _activeSkin.FruitRainColor.B));

            var fg = new SolidColorBrush(Color.FromArgb(235, 15, 15, 15));
            MultiplierText.Foreground = fg;
            SpeedText.Foreground = fg;
            RainText.Foreground = fg;
        }

        private static Color MakePastel(Color c, double towardWhite, double alpha)
        {
            var p = LerpColor(c, Colors.White, towardWhite);
            return Color.FromArgb((byte)(alpha * 255), p.R, p.G, p.B);
        }

        private void AnimatePlayfieldBackground(Color to)
        {
            var from = _playfieldBackgroundBrush.Color;
            _playfieldBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);

            var anim = new ColorAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            _playfieldBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        // -------------------------------
        // Toast
        // -------------------------------
        private void ShowToast(string txt, TimeSpan duration)
        {
            ToastText.Text = txt ?? "";

            if (string.IsNullOrWhiteSpace(txt))
            {
                HideToast();
                return;
            }

            Toast.Visibility = Visibility.Visible;

            Toast.BeginAnimation(UIElement.OpacityProperty, null);

            var animIn = new DoubleAnimation
            {
                From = Toast.Opacity,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Toast.BeginAnimation(UIElement.OpacityProperty, animIn);

            _toastHideUtc = (duration > TimeSpan.Zero)
                ? DateTime.UtcNow.Add(duration)
                : DateTime.MinValue;
        }

        private void HideToast()
        {
            if (Toast.Visibility != Visibility.Visible)
                return;

            Toast.BeginAnimation(UIElement.OpacityProperty, null);

            var animOut = new DoubleAnimation
            {
                From = Toast.Opacity,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            animOut.Completed += (s, e) =>
            {
                Toast.Visibility = Visibility.Collapsed;
                _toastHideUtc = DateTime.MinValue;
            };

            Toast.BeginAnimation(UIElement.OpacityProperty, animOut);
        }

        private void AutoHideToast()
        {
            if (_toastHideUtc == DateTime.MinValue) return;
            if (DateTime.UtcNow >= _toastHideUtc && !_isGameOver && !_isWaitingStart)
                HideToast();
        }

        private void GameOver(string shortReason)
        {
            _isGameOver = true;
            _isWaitingStart = true;

            Overlay.Visibility = Visibility.Visible;
            OverlayText.Text = "GAME OVER\n\nEspace = restart";

            ShowToast(shortReason + "  (Espace = restart)", TimeSpan.FromSeconds(2.0));
            ShakePlayfield(3.3);
            SaveStateSafe();
            SubmitRecordIfNeeded();
        }

        private async void LeaderboardButton_Click(object sender, RoutedEventArgs e)
        {
            LeaderboardButton.IsEnabled = false;
            try
            {
                var jwt = Licensing.LicenseSession.CurrentJwt ?? global::BIMaestroApp.LicenseJwt;
                if (string.IsNullOrWhiteSpace(jwt))
                    throw new InvalidOperationException("JWT manquant.");

                var data = await SnakeLeaderboardClient.FetchLeaderboardAsync(SupabaseFunctionsBaseUrl, jwt)
                    .ConfigureAwait(true);

                var window = new SnakeLeaderboardWindow(data)
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                window.ShowDialog();
            }
            catch
            {
                ShowToast("📊 Classement indisponible (réseau).", TimeSpan.FromSeconds(1.6));
            }
            finally
            {
                LeaderboardButton.IsEnabled = true;
            }
        }

        private void SubmitRecordIfNeeded()
        {
            if (!_recordThisRun || _recordScoreThisRun <= 0)
                return;

            var jwt = Licensing.LicenseSession.CurrentJwt ?? global::BIMaestroApp.LicenseJwt;
            if (string.IsNullOrWhiteSpace(jwt))
                return;

            _recordThisRun = false;

            var mode = GetLeaderboardMode();
            var playerName = Environment.UserName;
            var installId = global::BIMaestroApp.InstallId ?? global::BIMaestroApp.MachineId ?? Environment.MachineName;

            _ = Task.Run(() => SnakeLeaderboardClient.SubmitRecordAsync(
                SupabaseFunctionsBaseUrl,
                jwt,
                mode,
                _recordScoreThisRun,
                playerName,
                installId));
        }

        private string GetLeaderboardMode()
        {
            return _mode switch
            {
                GameMode.Arcade => "arcade",
                GameMode.Hardcore => "hardcore",
                _ => "classic"
            };
        }

        // -------------------------------
        // Persistence
        // -------------------------------
        private string GetStateFilePath()
        {
            string logsFolder = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "SauvegardePréférence");
            Directory.CreateDirectory(logsFolder);
            return IOPath.Combine(logsFolder, "snake.json");
        }

        private void LoadState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_stateFile) || !File.Exists(_stateFile))
                    return;

                var json = File.ReadAllText(_stateFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                _topClassic = Math.Max(0, ReadInt(json, "TopScoreClassic", -1));
                _topArcade = Math.Max(0, ReadInt(json, "TopScoreArcade", -1));
                _topHardcore = Math.Max(0, ReadInt(json, "TopScoreHardcore", -1));

                if (_topClassic < 0 || _topArcade < 0 || _topHardcore < 0)
                {
                    int old = Math.Max(0, ReadInt(json, "TopScore", 0));
                    if (_topClassic < 0) _topClassic = old;
                    if (_topArcade < 0) _topArcade = old;
                    if (_topHardcore < 0) _topHardcore = old;
                }

                _skinAuto = ReadBool(json, "SkinAuto", true);

                var manual = ReadString(json, "ManualSkinId", SkinId.Classic.ToString());
                if (Enum.TryParse(manual, out SkinId sid))
                    _manualSkin = sid;

                var modeStr = ReadString(json, "GameMode", GameMode.Classic.ToString());
                if (Enum.TryParse(modeStr, out GameMode gm))
                    _mode = gm;
            }
            catch { }
        }

        private void SaveStateSafe()
        {
            try { SaveState(); } catch { }
        }

        private void SaveState()
        {
            if (string.IsNullOrWhiteSpace(_stateFile))
                _stateFile = GetStateFilePath();

            string json = "{"
                + "\"TopScoreClassic\":" + _topClassic + ","
                + "\"TopScoreArcade\":" + _topArcade + ","
                + "\"TopScoreHardcore\":" + _topHardcore + ","
                + "\"SkinAuto\":" + (_skinAuto ? "true" : "false") + ","
                + "\"ManualSkinId\":\"" + JsonEscape(_manualSkin.ToString()) + "\","
                + "\"GameMode\":\"" + JsonEscape(_mode.ToString()) + "\""
                + "}";

            string tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);

            File.Copy(tmp, _stateFile, true);
            File.Delete(tmp);
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int ReadInt(string json, string key, int fallback)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;
            return fallback;
        }

        private static bool ReadBool(string json, string key, bool fallback)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success)
                return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        private static string ReadString(string json, string key, string fallback)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success)
                return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            return fallback;
        }

        // -------------------------------
        // Bitmap helpers
        // -------------------------------
        private void SetPixel(int x, int y, int argb)
        {
            if (x < 0 || y < 0 || x >= _bmpW || y >= _bmpH) return;
            _px[y * _bmpW + x] = argb;
        }

        private void FillRect(int x, int y, int w, int h, int argb)
        {
            int x2 = x + w;
            int y2 = y + h;

            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x2 > _bmpW) x2 = _bmpW;
            if (y2 > _bmpH) y2 = _bmpH;

            for (int yy = y; yy < y2; yy++)
            {
                int row = yy * _bmpW;
                for (int xx = x; xx < x2; xx++)
                    _px[row + xx] = argb;
            }
        }

        private void DrawRectOutline(int x, int y, int w, int h, int argb)
        {
            DrawHLine(x, x + w - 1, y, argb);
            DrawHLine(x, x + w - 1, y + h - 1, argb);
            DrawVLine(x, y, y + h - 1, argb);
            DrawVLine(x + w - 1, y, y + h - 1, argb);
        }

        private void DrawHLine(int x1, int x2, int y, int argb)
        {
            if (y < 0 || y >= _bmpH) return;
            if (x1 > x2) { int t = x1; x1 = x2; x2 = t; }
            if (x1 < 0) x1 = 0;
            if (x2 >= _bmpW) x2 = _bmpW - 1;

            int row = y * _bmpW;
            for (int x = x1; x <= x2; x++)
                _px[row + x] = argb;
        }

        private void DrawVLine(int x, int y1, int y2, int argb)
        {
            if (x < 0 || x >= _bmpW) return;
            if (y1 > y2) { int t = y1; y1 = y2; y2 = t; }
            if (y1 < 0) y1 = 0;
            if (y2 >= _bmpH) y2 = _bmpH - 1;

            for (int y = y1; y <= y2; y++)
                _px[y * _bmpW + x] = argb;
        }

        private void DrawCircle(int cx, int cy, int r, int argb, bool fill)
        {
            int r2 = r * r;
            for (int y = -r; y <= r; y++)
            {
                int yy = cy + y;
                if (yy < 0 || yy >= _bmpH) continue;

                int row = yy * _bmpW;
                for (int x = -r; x <= r; x++)
                {
                    int xx = cx + x;
                    if (xx < 0 || xx >= _bmpW) continue;

                    int d2 = x * x + y * y;
                    if (fill)
                    {
                        if (d2 <= r2) _px[row + xx] = argb;
                    }
                    else
                    {
                        if (Math.Abs(d2 - r2) <= r) _px[row + xx] = argb;
                    }
                }
            }
        }

        private static int Pack(Color c)
        {
            return (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
        }

        private static Color Shade(Color c, double mul)
        {
            mul = Math.Max(0, mul);
            return Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, c.R * mul)),
                (byte)Math.Max(0, Math.Min(255, c.G * mul)),
                (byte)Math.Max(0, Math.Min(255, c.B * mul))
            );
        }

        private static Color Darken(Color c, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Shade(c, 1.0 - amount);
        }

        private static Color Lighten(Color c, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromRgb(
                (byte)(c.R + (255 - c.R) * amount),
                (byte)(c.G + (255 - c.G) * amount),
                (byte)(c.B + (255 - c.B) * amount)
            );
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t)
            );
        }

        // -------------------------------
        // Types
        // -------------------------------
        private enum Direction { Up, Down, Left, Right }

        private readonly struct Cell : IEquatable<Cell>
        {
            public int X { get; }
            public int Y { get; }
            public Cell(int x, int y) { X = x; Y = y; }
            public bool Equals(Cell other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is Cell other && Equals(other);
            public override int GetHashCode() => (X * 397) ^ Y;
        }

        private enum FruitType { Normal, Multiplier, Speed, Rain }

        private sealed class Fruit
        {
            public Cell Pos { get; }
            public FruitType Type { get; }
            public bool IsPulsing { get; }

            private Fruit(Cell pos, FruitType type, bool pulsing)
            {
                Pos = pos;
                Type = type;
                IsPulsing = pulsing;
            }

            public static Fruit Create(Cell pos, FruitType type)
            {
                bool pulsing = type != FruitType.Normal;
                return new Fruit(pos, type, pulsing);
            }
        }

        private enum GameMode { Classic, Arcade, Hardcore }

        private sealed class ModeChoice
        {
            public GameMode Mode { get; }
            public string Name { get; }
            public string Hint { get; }

            public ModeChoice(GameMode mode, string name, string hint)
            {
                Mode = mode;
                Name = name;
                Hint = hint;
            }

            public override string ToString() => Name;
        }

        private enum SkinId { Classic, Yellow, Cyan, Purple, Crimson, ElectricBlue, Obsidian }

        private sealed class SkinDefinition
        {
            public SkinId Id { get; }
            public string Name { get; }
            public int UnlockScore { get; }

            public Color HeadColor { get; }
            public Color BodyColor { get; }
            public Color BackgroundColor { get; }

            public Color GridColor { get; }
            public Color WallColor { get; }

            public Color FruitNormalColor { get; }
            public Color FruitMultiplierColor { get; }
            public Color FruitSpeedColor { get; }
            public Color FruitRainColor { get; }

            public double ScanlinesOpacity { get; }
            public double VignetteOpacity { get; }

            private SkinDefinition(
                SkinId id, string name, int unlockScore,
                Color head, Color body, Color background,
                Color grid, Color wall,
                Color fruitNormal, Color fruitMult, Color fruitSpeed, Color fruitRain,
                double scanlinesOpacity, double vignetteOpacity)
            {
                Id = id;
                Name = name;
                UnlockScore = unlockScore;

                HeadColor = head;
                BodyColor = body;
                BackgroundColor = background;

                GridColor = grid;
                WallColor = wall;

                FruitNormalColor = fruitNormal;
                FruitMultiplierColor = fruitMult;
                FruitSpeedColor = fruitSpeed;
                FruitRainColor = fruitRain;

                ScanlinesOpacity = scanlinesOpacity;
                VignetteOpacity = vignetteOpacity;
            }

            public static SkinDefinition Create(
                SkinId id, string name, int unlockScore,
                Color head, Color body, Color background,
                Color grid, Color wall,
                Color fruitNormal, Color fruitMult, Color fruitSpeed, Color fruitRain,
                double scanlinesOpacity, double vignetteOpacity)
            {
                return new SkinDefinition(
                    id, name, unlockScore,
                    head, body, background,
                    grid, wall,
                    fruitNormal, fruitMult, fruitSpeed, fruitRain,
                    scanlinesOpacity, vignetteOpacity
                );
            }

            public Color GetFruitColor(FruitType type)
            {
                switch (type)
                {
                    case FruitType.Multiplier: return FruitMultiplierColor;
                    case FruitType.Speed: return FruitSpeedColor;
                    case FruitType.Rain: return FruitRainColor;
                    default: return FruitNormalColor;
                }
            }
        }

        private sealed class SkinChoice
        {
            public string Display { get; set; }
            public bool IsEnabled { get; set; }
            public bool IsAuto { get; set; }
            public SkinId SkinId { get; set; }
        }
    }
}
