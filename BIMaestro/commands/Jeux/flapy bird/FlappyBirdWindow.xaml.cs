using Licensing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Threading.Tasks;
using System.Diagnostics;
using BIMaestro.Localization;

using IOPath = System.IO.Path;

namespace BIMaestro.Bonus
{
    public partial class FlappyBirdWindow : Window
    {
        // Zone de jeu (taille fixe, voir XAML)
        private const double FieldWidth = 420;
        private const double GroundHeight = 70;
        private const double PlayHeight = 470;          // hauteur au-dessus du sol

        private const double BirdSize = 24;
        // Boîte de collision : un cercle centré sur le corps de l'oiseau (et non le carré 24×24 du dessin)
        private const double HitCenterX = 13;
        private const double HitCenterY = 12.5;
        private const double HitRadius = 9;

        private const double PipeWidth = 56;
        private const double GapSize = 138;
        private const double GapEdgeMargin = 28;        // longueur minimale de tuyau visible au-dessus / au-dessous du trou
        private const double MaxGapShift = 240;         // écart maxi de hauteur entre deux trous qui se suivent (reste passable)
        private const double MinGapShift = 55;          // écart mini : deux trous ne sont jamais presque alignés
        private const double Gravity = 0.45;
        private const double FlapStrength = -7.5;
        private const double MaxFallSpeed = 10;
        private const double PipeSpeed = 3.2;
        private const double TargetFps = 60.0;
        private const double BaseFrameSeconds = 1.0 / TargetFps;
        private const double MaxDeltaSeconds = 0.05;
        private const double BirdX = 90;
        private const double PipeCapHeight = 18;
        private const int PipeSpawnTicks = 70;
        private const double PipeSpawnIntervalSeconds = PipeSpawnTicks * BaseFrameSeconds;
        private const double RestartCooldownSeconds = 0.45;
        private const string SupabaseFunctionsBaseUrl = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co";

        // Médailles : bronze, argent, or, platine
        private static readonly int[] MedalThresholds = { 10, 20, 30, 40 };

        // Parallaxe : vitesse de chaque plan par rapport aux tuyaux
        private const double CloudParallax = 0.15;
        private const double FarHillParallax = 0.3;
        private const double NearHillParallax = 0.55;
        private const double GroundStripeSpacing = 28;

        private readonly Random _random = new Random();
        private readonly List<Pipe> _pipes = new List<Pipe>();
        private readonly Brush _birdFill = CreateBirdFill();
        private readonly Brush _birdStroke = CreateBirdStroke();
        private readonly Brush _pipeFill = CreatePipeFill();
        private readonly Brush _pipeStroke = CreatePipeStroke();
        private Canvas _bird;
        private RotateTransform _birdRotation;
        private TranslateTransform _birdTranslate;
        private TranslateTransform _cloudLayer;
        private TranslateTransform _farHillLayer;
        private TranslateTransform _nearHillLayer;
        private TranslateTransform _groundStripes;
        private double _scroll;
        private double _birdY;
        private double _birdVelocity;
        private double _lastGapCenter;
        private double _pipeSpawnAccumulator;
        private bool _running;
        private bool _gameOver;
        private int _score;
        private int _bestScore;
        private int _bestAtRunStart;
        private int _bestMedalTierShown = -1;
        private int _lastSyncedBestScore;
        private bool _recordThisRun;
        private int _recordScoreThisRun;
        private bool _isSyncingBestScore;
        private bool _isRendering;
        private bool _stateDirty;
        private readonly string _stateFile;
        private readonly Stopwatch _frameStopwatch = new Stopwatch();
        private readonly Stopwatch _sinceGameOver = new Stopwatch();

        public FlappyBirdWindow()
        {
            InitializeComponent();
            _stateFile = GetStateFilePath();
            LoadState();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyStaticTexts();
            BuildScenery();
            Focus();
            ResetGame();
            TriggerBestScoreSync();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            StopRenderLoop();
            _frameStopwatch.Reset();
            SaveStateIfDirty();
        }

        private void ApplyStaticTexts()
        {
            LeaderboardButtonText.Text = UiLanguage.T("Classement global", "Global leaderboard");
            ScoreLabel.Text = "SCORE";
            BestLabel.Text = UiLanguage.T("RECORD", "BEST");
            ResultScoreLabel.Text = "SCORE";
            ResultBestLabel.Text = UiLanguage.T("RECORD", "BEST");
            NewRecordText.Text = UiLanguage.T("Nouveau record !", "New high score!");
            OverlayKeyText.Text = UiLanguage.T("ESPACE", "SPACE");
            ShortcutsText.Text = UiLanguage.T("Espace ou clic = sauter • Échap = fermer", "Space or click = flap • Esc = close");
        }

        // ===================== ENTRÉES =====================

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            TriggerFlap();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.Key != Key.Space)
                return;

            e.Handled = true;
            if (e.IsRepeat)
                return;

            TriggerFlap();
        }

        private void TriggerFlap()
        {
            if (!_running)
            {
                // Petit délai après une défaite pour ne pas relancer sans le vouloir en tapant Espace
                if (_gameOver && _sinceGameOver.Elapsed.TotalSeconds < RestartCooldownSeconds)
                    return;

                StartGame();
                return;
            }

            if (!_gameOver)
            {
                _birdVelocity = FlapStrength;
            }
        }

        // ===================== PARTIE =====================

        private void StartGame()
        {
            if (_gameOver)
            {
                ResetGame();
            }

            _running = true;
            _bestAtRunStart = _bestScore;
            Overlay.Visibility = Visibility.Collapsed;
            LeaderboardButton.IsEnabled = false;
            _birdVelocity = FlapStrength;
            _frameStopwatch.Restart();
            StartRenderLoop();
        }

        private void ResetGame()
        {
            StopRenderLoop();
            _pipes.Clear();
            GameCanvas.Children.Clear();

            _birdRotation = new RotateTransform(0, BirdSize / 2, BirdSize / 2);
            _birdTranslate = new TranslateTransform(BirdX, 0);

            var birdTransform = new TransformGroup();
            birdTransform.Children.Add(_birdRotation);
            birdTransform.Children.Add(_birdTranslate);

            _bird = CreateBirdSprite(birdTransform);

            Canvas.SetLeft(_bird, 0);
            Canvas.SetTop(_bird, 0);
            Panel.SetZIndex(_bird, 10);
            GameCanvas.Children.Add(_bird);

            _birdY = PlayHeight / 2 - BirdSize / 2;
            _lastGapCenter = PlayHeight / 2;
            _birdVelocity = 0;
            _pipeSpawnAccumulator = 0;
            _score = 0;
            _recordThisRun = false;
            _recordScoreThisRun = 0;
            _running = false;
            _gameOver = false;
            _frameStopwatch.Reset();
            LeaderboardButton.IsEnabled = true;

            UpdateScore(false);
            UpdateBestScoreUi();
            PositionBird(BirdX);
            ShowReadyOverlay();
        }

        private void StartRenderLoop()
        {
            if (_isRendering)
                return;

            _isRendering = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRenderLoop()
        {
            if (!_isRendering)
                return;

            CompositionTarget.Rendering -= OnRendering;
            _isRendering = false;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (_gameOver)
            {
                return;
            }

            double dt = _frameStopwatch.IsRunning ? _frameStopwatch.Elapsed.TotalSeconds : BaseFrameSeconds;
            _frameStopwatch.Restart();
            dt = Math.Min(MaxDeltaSeconds, Math.Max(BaseFrameSeconds * 0.35, dt));
            double frameScale = dt / BaseFrameSeconds;

            UpdateBird(frameScale);
            UpdatePipes(frameScale, dt);
            UpdateScenery();
            CheckCollision();
        }

        private void UpdateBird(double frameScale)
        {
            _birdVelocity += Gravity * frameScale;
            if (_birdVelocity > MaxFallSpeed)
            {
                _birdVelocity = MaxFallSpeed;
            }

            _birdY += _birdVelocity * frameScale;

            // Plafond : l'oiseau bute contre le haut au lieu de perdre
            double minY = HitRadius - HitCenterY;
            if (_birdY < minY)
            {
                _birdY = minY;
                if (_birdVelocity < 0) _birdVelocity = 0;
            }

            PositionBird(BirdX);
        }

        private void PositionBird(double x)
        {
            if (_birdTranslate != null)
            {
                _birdTranslate.X = x;
                _birdTranslate.Y = _birdY;
            }

            double tilt = Math.Max(-30, Math.Min(40, _birdVelocity * 5.5));
            if (_birdRotation != null)
            {
                _birdRotation.Angle = tilt;
            }
        }

        private void UpdatePipes(double frameScale, double dt)
        {
            _scroll += PipeSpeed * frameScale;

            _pipeSpawnAccumulator += dt;
            while (_pipeSpawnAccumulator >= PipeSpawnIntervalSeconds)
            {
                _pipeSpawnAccumulator -= PipeSpawnIntervalSeconds;
                SpawnPipe();
            }

            for (int i = _pipes.Count - 1; i >= 0; i--)
            {
                var pipe = _pipes[i];
                pipe.X -= PipeSpeed * frameScale;
                pipe.Transform.X = pipe.X - 4;

                if (!pipe.Passed && pipe.X + PipeWidth < BirdX)
                {
                    pipe.Passed = true;
                    _score++;
                    UpdateScore(true);

                    if (_score > _bestScore)
                    {
                        _bestScore = _score;
                        _recordThisRun = true;
                        _recordScoreThisRun = _score;
                        UpdateBestScoreUi();
                        _stateDirty = true;
                    }
                }

                if (pipe.X + PipeWidth < -8)
                {
                    GameCanvas.Children.Remove(pipe.Root);
                    _pipes.RemoveAt(i);
                }
            }
        }

        private void SpawnPipe()
        {
            // Trou toujours atteignable (écart borné avec le précédent), mais jamais à la même hauteur :
            // chaque tuyau impose de monter ou descendre d'au moins MinGapShift.
            double minGapCenter = GapSize / 2 + GapEdgeMargin;
            double maxGapCenter = PlayHeight - GapSize / 2 - GapEdgeMargin;
            double lo = Math.Max(minGapCenter, _lastGapCenter - MaxGapShift);
            double hi = Math.Min(maxGapCenter, _lastGapCenter + MaxGapShift);
            double upLength = Math.Max(0, (_lastGapCenter - MinGapShift) - lo);
            double downLength = Math.Max(0, hi - (_lastGapCenter + MinGapShift));
            double gapCenter;
            if (upLength + downLength <= 0)
            {
                gapCenter = lo + _random.NextDouble() * (hi - lo);
            }
            else
            {
                double r = _random.NextDouble() * (upLength + downLength);
                gapCenter = r < upLength ? lo + r : _lastGapCenter + MinGapShift + (r - upLength);
            }
            _lastGapCenter = gapCenter;

            double topHeight = gapCenter - GapSize / 2;
            double bottomY = gapCenter + GapSize / 2;
            double bottomHeight = PlayHeight - bottomY;
            double startX = FieldWidth;

            var pipeTransform = new TranslateTransform(startX - 4, 0);
            var pipeRoot = new Canvas
            {
                Width = PipeWidth + 8,
                Height = PlayHeight,
                IsHitTestVisible = false,
                RenderTransform = pipeTransform,
                CacheMode = new BitmapCache()
            };

            // Le corps des tuyaux dépasse de 12 px hors champ (haut et sol) pour masquer les coins arrondis
            var topRect = new Rectangle
            {
                Width = PipeWidth,
                Height = topHeight + 12,
                RadiusX = 8,
                RadiusY = 8,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            var bottomRect = new Rectangle
            {
                Width = PipeWidth,
                Height = bottomHeight + 12,
                RadiusX = 8,
                RadiusY = 8,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            var topCap = CreatePipeCap();
            var bottomCap = CreatePipeCap();

            Canvas.SetLeft(topRect, 4);
            Canvas.SetTop(topRect, -12);

            Canvas.SetLeft(bottomRect, 4);
            Canvas.SetTop(bottomRect, bottomY);

            Canvas.SetLeft(topCap, 0);
            Canvas.SetTop(topCap, topHeight - PipeCapHeight);

            Canvas.SetLeft(bottomCap, 0);
            Canvas.SetTop(bottomCap, bottomY);

            pipeRoot.Children.Add(topRect);
            pipeRoot.Children.Add(bottomRect);
            pipeRoot.Children.Add(topCap);
            pipeRoot.Children.Add(bottomCap);

            GameCanvas.Children.Add(pipeRoot);

            _pipes.Add(new Pipe
            {
                X = startX,
                TopHeight = topHeight,
                BottomY = bottomY,
                BottomHeight = bottomHeight,
                TopCapY = topHeight - PipeCapHeight,
                Root = pipeRoot,
                Transform = pipeTransform
            });
        }

        private Rectangle CreatePipeCap()
        {
            return new Rectangle
            {
                Width = PipeWidth + 8,
                Height = PipeCapHeight,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };
        }

        private void CheckCollision()
        {
            double cx = BirdX + HitCenterX;
            double cy = _birdY + HitCenterY;

            // Sol : l'oiseau s'arrête sur l'herbe
            if (cy + HitRadius >= PlayHeight)
            {
                _birdY = PlayHeight - HitRadius - HitCenterY;
                PositionBird(BirdX);
                EndGame();
                return;
            }

            foreach (var pipe in _pipes)
            {
                if (pipe.X - 4 > cx + HitRadius || pipe.X + PipeWidth + 4 < cx - HitRadius)
                    continue;

                if (CircleHitsRect(cx, cy, pipe.X, -20, PipeWidth, pipe.TopHeight + 20)
                    || CircleHitsRect(cx, cy, pipe.X - 4, pipe.TopCapY, PipeWidth + 8, PipeCapHeight)
                    || CircleHitsRect(cx, cy, pipe.X, pipe.BottomY, PipeWidth, pipe.BottomHeight + 20)
                    || CircleHitsRect(cx, cy, pipe.X - 4, pipe.BottomY, PipeWidth + 8, PipeCapHeight))
                {
                    EndGame();
                    return;
                }
            }
        }

        private static bool CircleHitsRect(double cx, double cy, double x, double y, double w, double h)
        {
            double nx = Math.Max(x, Math.Min(cx, x + w));
            double ny = Math.Max(y, Math.Min(cy, y + h));
            double dx = cx - nx;
            double dy = cy - ny;
            return dx * dx + dy * dy < HitRadius * HitRadius;
        }

        private void EndGame()
        {
            _gameOver = true;
            _running = false;
            StopRenderLoop();
            _frameStopwatch.Reset();
            _sinceGameOver.Restart();
            LeaderboardButton.IsEnabled = true;

            UpdateBestScoreUi();
            ShowGameOverOverlay();
            SaveStateIfDirty();
            SubmitRecordIfNeeded();
            TriggerBestScoreSync();
        }

        // ===================== ÉCRANS =====================

        private void ShowReadyOverlay()
        {
            OverlayTitle.Text = "Flappy Bird";
            NewRecordBadge.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            OverlayMessage.Text = UiLanguage.T(
                "Appuie sur Espace ou clique pour t'envoler. Évite les tuyaux !",
                "Press Space or click to take off. Avoid the pipes!");
            OverlayBestScore.Visibility = Visibility.Visible;
            OverlayBestScore.Text = UiLanguage.Get("FlappyBirdWindow.xaml.HighScore") + _bestScore;
            Overlay.Visibility = Visibility.Visible;
        }

        private void ShowGameOverOverlay()
        {
            int tier = MedalTier(_score);
            OverlayTitle.Text = UiLanguage.Get("FlappyBirdWindow.xaml.GameOver");
            NewRecordBadge.Visibility = _score > _bestAtRunStart && _score > 0 ? Visibility.Visible : Visibility.Collapsed;

            ResultPanel.Visibility = Visibility.Visible;
            ResultMedalHost.Child = CreateMedal(tier);
            ResultMedalText.Text = tier > 0 ? MedalName(tier) : UiLanguage.T("Pas de médaille", "No medal");
            ResultScoreText.Text = _score.ToString();
            ResultBestText.Text = _bestScore.ToString();

            string hint = tier < MedalThresholds.Length
                ? UiLanguage.T(
                    $"Encore {MedalThresholds[tier] - _score} pour la médaille {MedalName(tier + 1).ToLowerInvariant()}.",
                    $"{MedalThresholds[tier] - _score} more for the {MedalName(tier + 1).ToLowerInvariant()} medal.") + "\n"
                : "";
            OverlayMessage.Text = hint + UiLanguage.T("Espace ou clic pour rejouer", "Space or click to play again");
            OverlayBestScore.Visibility = Visibility.Collapsed;
            Overlay.Visibility = Visibility.Visible;
        }

        private void UpdateScore(bool pop)
        {
            ScoreText.Text = _score.ToString();

            int tier = MedalTier(_score);
            NextMedalText.Text = tier < MedalThresholds.Length
                ? UiLanguage.T(
                    $"Prochaine : {MedalName(tier + 1).ToLowerInvariant()} à {MedalThresholds[tier]}",
                    $"Next: {MedalName(tier + 1).ToLowerInvariant()} at {MedalThresholds[tier]}")
                : UiLanguage.T("Platine atteint !", "Platinum reached!");

            if (pop)
            {
                bool medal = Array.IndexOf(MedalThresholds, _score) >= 0;
                var anim = new DoubleAnimation(medal ? 1.45 : 1.18, 1.0, TimeSpan.FromMilliseconds(medal ? 320 : 160))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ScorePopScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                ScorePopScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        }

        private void UpdateBestScoreUi()
        {
            BestScoreText.Text = _bestScore.ToString();

            int tier = MedalTier(_bestScore);
            if (tier == _bestMedalTierShown)
                return;

            _bestMedalTierShown = tier;
            BestMedalHost.Child = CreateMedal(tier);
            BestMedalText.Text = tier > 0
                ? UiLanguage.T("Médaille : ", "Medal: ") + MedalName(tier).ToLowerInvariant()
                : UiLanguage.T("Aucune médaille", "No medal yet");
        }

        // ===================== MÉDAILLES =====================

        private static int MedalTier(int score)
        {
            int tier = 0;
            foreach (int threshold in MedalThresholds)
            {
                if (score >= threshold) tier++;
            }
            return tier;
        }

        private static string MedalName(int tier)
        {
            switch (tier)
            {
                case 1: return UiLanguage.T("Bronze", "Bronze");
                case 2: return UiLanguage.T("Argent", "Silver");
                case 3: return UiLanguage.T("Or", "Gold");
                case 4: return UiLanguage.T("Platine", "Platinum");
                default: return "";
            }
        }

        private static FrameworkElement CreateMedal(int tier)
        {
            var grid = new Grid { Width = 64, Height = 64 };

            if (tier <= 0)
            {
                grid.Children.Add(new Ellipse
                {
                    Fill = CreateFrozenBrush(Color.FromArgb(40, 255, 255, 255)),
                    Stroke = CreateFrozenBrush(Color.FromArgb(90, 255, 255, 255)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                });
                grid.Children.Add(new TextBlock
                {
                    Text = "?",
                    FontSize = 26,
                    FontWeight = FontWeights.Bold,
                    Foreground = CreateFrozenBrush(Color.FromArgb(120, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                return grid;
            }

            Color light, baseColor, dark;
            switch (tier)
            {
                case 1: light = Color.FromRgb(0xF3, 0xB8, 0x80); baseColor = Color.FromRgb(0xCD, 0x7F, 0x32); dark = Color.FromRgb(0x84, 0x48, 0x14); break;
                case 2: light = Color.FromRgb(0xF7, 0xF9, 0xFB); baseColor = Color.FromRgb(0xBC, 0xC6, 0xD2); dark = Color.FromRgb(0x74, 0x7F, 0x8D); break;
                case 3: light = Color.FromRgb(0xFF, 0xEC, 0xA8); baseColor = Color.FromRgb(0xF5, 0xB8, 0x3D); dark = Color.FromRgb(0xA0, 0x68, 0x0A); break;
                default: light = Color.FromRgb(0xEC, 0xFD, 0xFF); baseColor = Color.FromRgb(0x8F, 0xDD, 0xE8); dark = Color.FromRgb(0x3A, 0x92, 0xA0); break;
            }

            var fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.3),
                Center = new Point(0.45, 0.4),
                RadiusX = 0.75,
                RadiusY = 0.75
            };
            fill.GradientStops.Add(new GradientStop(light, 0));
            fill.GradientStops.Add(new GradientStop(baseColor, 0.55));
            fill.GradientStops.Add(new GradientStop(dark, 1));
            fill.Freeze();

            grid.Children.Add(new Ellipse { Fill = fill, Stroke = CreateFrozenBrush(dark), StrokeThickness = 2 });
            grid.Children.Add(new Ellipse
            {
                Width = 46,
                Height = 46,
                Stroke = CreateFrozenBrush(Color.FromArgb(150, light.R, light.G, light.B)),
                StrokeThickness = 1.5
            });
            grid.Children.Add(new TextBlock
            {
                Text = "★",
                FontSize = 28,
                Foreground = CreateFrozenBrush(Color.FromArgb(210, dark.R, dark.G, dark.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            });
            return grid;
        }

        // ===================== DÉCOR DÉFILANT =====================

        private void BuildScenery()
        {
            SceneryCanvas.Children.Clear();
            GroundCanvas.Children.Clear();

            _cloudLayer = AddTiledLayer(SceneryCanvas, CreateCloudTile, 0);

            _farHillLayer = AddTiledLayer(SceneryCanvas, () => CreateHillTile(
                "M0,70 C40,100 80,20 140,40 S240,90 290,60 S380,40 420,70 L420,150 L0,150 Z",
                Color.FromArgb(170, 150, 214, 132), 150), PlayHeight - 140);

            _nearHillLayer = AddTiledLayer(SceneryCanvas, () => CreateHillTile(
                "M0,60 C30,83 100,20 160,35 S260,80 320,50 S390,37 420,60 L420,130 L0,130 Z",
                Color.FromArgb(225, 104, 186, 96), 130), PlayHeight - 115);

            // Sol : fond fixe + bandes qui défilent à la vitesse des tuyaux
            var groundFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            groundFill.GradientStops.Add(new GradientStop(Color.FromRgb(0xAF, 0xE7, 0x7A), 0));
            groundFill.GradientStops.Add(new GradientStop(Color.FromRgb(0x77, 0xC8, 0x5C), 0.4));
            groundFill.GradientStops.Add(new GradientStop(Color.FromRgb(0x4E, 0x98, 0x48), 1));
            groundFill.Freeze();
            GroundCanvas.Children.Add(new Rectangle { Width = FieldWidth, Height = GroundHeight, Fill = groundFill });

            var stripes = new Canvas
            {
                Width = FieldWidth + GroundStripeSpacing * 2,
                Height = GroundHeight,
                CacheMode = new BitmapCache()
            };
            var stripeBrush = CreateFrozenBrush(Color.FromArgb(46, 255, 255, 255));
            var bladeBrush = CreateFrozenBrush(Color.FromArgb(70, 255, 255, 255));
            for (double x = 0; x < stripes.Width; x += GroundStripeSpacing)
            {
                stripes.Children.Add(new Polygon
                {
                    Points = new PointCollection { new Point(x, 12), new Point(x + 13, 12), new Point(x + 3, 30), new Point(x - 10, 30) },
                    Fill = stripeBrush
                });
                var blade = new Rectangle { Width = 3, Height = 12, RadiusX = 1.5, RadiusY = 1.5, Fill = bladeBrush };
                Canvas.SetLeft(blade, x + 18);
                Canvas.SetTop(blade, 40 + (x / GroundStripeSpacing % 3) * 4);
                stripes.Children.Add(blade);
            }
            _groundStripes = new TranslateTransform();
            stripes.RenderTransform = _groundStripes;
            GroundCanvas.Children.Add(stripes);

            GroundCanvas.Children.Add(new Rectangle { Width = FieldWidth, Height = 4, Fill = CreateFrozenBrush(Color.FromRgb(0x4F, 0x9E, 0x45)) });
            var highlight = new Rectangle { Width = FieldWidth, Height = 6, Fill = CreateFrozenBrush(Color.FromArgb(80, 255, 255, 255)) };
            Canvas.SetTop(highlight, 4);
            GroundCanvas.Children.Add(highlight);

            UpdateScenery();
        }

        private static TranslateTransform AddTiledLayer(Canvas host, Func<UIElement> createTile, double top)
        {
            // Deux tuiles identiques côte à côte : on décale le calque modulo la largeur pour un défilement sans fin
            var transform = new TranslateTransform();
            var layer = new Canvas
            {
                Width = FieldWidth * 2,
                RenderTransform = transform,
                CacheMode = new BitmapCache()
            };
            for (int i = 0; i < 2; i++)
            {
                var tile = createTile();
                Canvas.SetLeft(tile, i * FieldWidth);
                layer.Children.Add(tile);
            }
            Canvas.SetTop(layer, top);
            host.Children.Add(layer);
            return transform;
        }

        private void UpdateScenery()
        {
            if (_cloudLayer == null)
                return;

            _cloudLayer.X = -(_scroll * CloudParallax % FieldWidth);
            _farHillLayer.X = -(_scroll * FarHillParallax % FieldWidth);
            _nearHillLayer.X = -(_scroll * NearHillParallax % FieldWidth);
            _groundStripes.X = -(_scroll % GroundStripeSpacing);
        }

        private static UIElement CreateHillTile(string data, Color color, double height)
        {
            var tile = new Canvas { Width = FieldWidth, Height = height };
            var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Fill = CreateFrozenBrush(color) };
            tile.Children.Add(path);
            return tile;
        }

        private static UIElement CreateCloudTile()
        {
            var tile = new Canvas { Width = FieldWidth, Height = 300 };
            tile.Children.Add(CreateCloud(28, 52, 1.0, 0.94));
            tile.Children.Add(CreateCloud(226, 136, 0.85, 0.8));
            tile.Children.Add(CreateCloud(84, 226, 1.15, 0.55));
            return tile;
        }

        private static UIElement CreateCloud(double left, double top, double scale, double opacity)
        {
            var brush = CreateFrozenBrush(Colors.White);
            var cloud = new Canvas
            {
                Width = 124,
                Height = 56,
                Opacity = opacity,
                RenderTransform = new ScaleTransform(scale, scale)
            };
            AddEllipse(cloud, brush, 0, 18, 62, 34);
            AddEllipse(cloud, brush, 24, 4, 48, 42);
            AddEllipse(cloud, brush, 54, 14, 70, 38);
            var bar = new Rectangle { Width = 104, Height = 26, RadiusX = 13, RadiusY = 13, Fill = brush };
            Canvas.SetLeft(bar, 12);
            Canvas.SetTop(bar, 27);
            cloud.Children.Add(bar);
            Canvas.SetLeft(cloud, left);
            Canvas.SetTop(cloud, top);
            return cloud;
        }

        private static void AddEllipse(Canvas canvas, Brush fill, double left, double top, double width, double height)
        {
            var e = new Ellipse { Width = width, Height = height, Fill = fill };
            Canvas.SetLeft(e, left);
            Canvas.SetTop(e, top);
            canvas.Children.Add(e);
        }

        // ===================== CLASSEMENT =====================

        private async void LeaderboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
                return;

            LeaderboardButton.IsEnabled = false;
            try
            {
                var jwt = LicenseSession.CurrentJwt ?? global::BIMaestroApp.LicenseJwt;
                if (string.IsNullOrWhiteSpace(jwt))
                    throw new InvalidOperationException("JWT manquant.");

                var data = await SnakeLeaderboardClient.FetchLeaderboardAsync(SupabaseFunctionsBaseUrl, jwt)
                    .ConfigureAwait(true);

                var window = new SnakeLeaderboardWindow(data, "flappy_bird")
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                window.ShowDialog();
            }
            catch
            {
                LeaderboardButtonText.Text = UiLanguage.T("Classement indisponible", "Leaderboard unavailable");
                await Task.Delay(1800).ConfigureAwait(true);
                LeaderboardButtonText.Text = UiLanguage.T("Classement global", "Global leaderboard");
            }
            finally
            {
                LeaderboardButton.IsEnabled = !_running;
                Focus();
            }
        }

        private void SubmitRecordIfNeeded()
        {
            if (!_recordThisRun || _recordScoreThisRun <= 0)
                return;

            var jwt = LicenseSession.CurrentJwt ?? global::BIMaestroApp.LicenseJwt;
            if (string.IsNullOrWhiteSpace(jwt))
                return;

            _recordThisRun = false;

            var playerName = Environment.UserName;
            var installId = global::BIMaestroApp.InstallId ?? global::BIMaestroApp.MachineId ?? Environment.MachineName;

            _ = Task.Run(() => SnakeLeaderboardClient.SubmitRecordAsync(
                SupabaseFunctionsBaseUrl,
                jwt,
                "flappy_bird",
                _recordScoreThisRun,
                playerName,
                installId));
        }

        private void TriggerBestScoreSync()
        {
            _ = TrySyncBestScoreAsync();
        }

        private async Task TrySyncBestScoreAsync()
        {
            if (_isSyncingBestScore || _bestScore <= 0 || _bestScore <= _lastSyncedBestScore)
                return;

            var jwt = LicenseSession.CurrentJwt ?? global::BIMaestroApp.LicenseJwt;
            if (string.IsNullOrWhiteSpace(jwt))
                return;

            _isSyncingBestScore = true;

            try
            {
                var playerName = Environment.UserName;
                var installId = global::BIMaestroApp.InstallId ?? global::BIMaestroApp.MachineId ?? Environment.MachineName;

                await SnakeLeaderboardClient.SubmitRecordAsync(
                    SupabaseFunctionsBaseUrl,
                    jwt,
                    "flappy_bird",
                    _bestScore,
                    playerName,
                    installId).ConfigureAwait(true);

                _lastSyncedBestScore = _bestScore;
                SaveStateSafe();
            }
            catch
            {
            }
            finally
            {
                _isSyncingBestScore = false;
            }
        }

        // ===================== SAUVEGARDE =====================

        private string GetStateFilePath()
        {
            string logsFolder = global::BIMaestro.Storage.PreferenceStorage.Folder("Flappy Bird");
            Directory.CreateDirectory(logsFolder);
            return IOPath.Combine(logsFolder, "flappybird.json");
        }

        private void LoadState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_stateFile) || !File.Exists(_stateFile))
                    return;

                var json = File.ReadAllText(_stateFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                _bestScore = Math.Max(0, ReadInt(json, "BestScore", 0));
                _lastSyncedBestScore = Math.Max(0, ReadInt(json, "LastSyncedBestScore", 0));
            }
            catch
            {
                _bestScore = 0;
                _lastSyncedBestScore = 0;
            }
        }

        private void SaveStateSafe()
        {
            try
            {
                SaveState();
                _stateDirty = false;
            }
            catch
            {
            }
        }

        private void SaveStateIfDirty()
        {
            if (_stateDirty)
            {
                SaveStateSafe();
            }
        }

        private void SaveState()
        {
            string json = "{"
                + "\"BestScore\":" + Math.Max(0, _bestScore) + ","
                + "\"LastSyncedBestScore\":" + Math.Max(0, _lastSyncedBestScore)
                + "}";

            string tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Copy(tmp, _stateFile, true);
            File.Delete(tmp);
        }

        private static int ReadInt(string json, string key, int fallback)
        {
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value))
                return value;

            return fallback;
        }

        // ===================== DESSINS =====================

        private class Pipe
        {
            public double X { get; set; }
            public double TopHeight { get; set; }
            public double BottomY { get; set; }
            public double BottomHeight { get; set; }
            public double TopCapY { get; set; }
            public Canvas Root { get; set; }
            public TranslateTransform Transform { get; set; }
            public bool Passed { get; set; }
        }

        private Canvas CreateBirdSprite(Transform transform)
        {
            var bird = new Canvas
            {
                Width = BirdSize,
                Height = BirdSize,
                RenderTransform = transform,
                CacheMode = new BitmapCache(),
                IsHitTestVisible = false
            };

            var tail = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(2, 10),
                    new Point(0, 6),
                    new Point(6, 9),
                    new Point(0, 16),
                    new Point(2, 12)
                },
                Fill = CreateFrozenBrush(Color.FromRgb(246, 178, 45)),
                Stroke = _birdStroke,
                StrokeThickness = 0.8
            };

            var body = new Ellipse
            {
                Width = 20,
                Height = 17,
                Fill = _birdFill,
                Stroke = _birdStroke,
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(body, 3);
            Canvas.SetTop(body, 4);

            var belly = new Ellipse
            {
                Width = 9,
                Height = 7,
                Fill = CreateFrozenBrush(Color.FromArgb(150, 255, 248, 199))
            };
            Canvas.SetLeft(belly, 10);
            Canvas.SetTop(belly, 11);

            var wing = new Ellipse
            {
                Width = 10,
                Height = 8,
                Fill = CreateFrozenBrush(Color.FromRgb(247, 168, 38)),
                Stroke = CreateFrozenBrush(Color.FromRgb(190, 113, 20)),
                StrokeThickness = 0.8
            };
            Canvas.SetLeft(wing, 5);
            Canvas.SetTop(wing, 10);

            var beak = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(20, 9),
                    new Point(24, 12),
                    new Point(20, 15)
                },
                Fill = CreateFrozenBrush(Color.FromRgb(255, 129, 36)),
                Stroke = CreateFrozenBrush(Color.FromRgb(196, 80, 25)),
                StrokeThickness = 0.8
            };

            var eye = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Brushes.White,
                Stroke = CreateFrozenBrush(Color.FromRgb(80, 80, 80)),
                StrokeThickness = 0.5
            };
            Canvas.SetLeft(eye, 15);
            Canvas.SetTop(eye, 7);

            var pupil = new Ellipse
            {
                Width = 2,
                Height = 2,
                Fill = Brushes.Black
            };
            Canvas.SetLeft(pupil, 18);
            Canvas.SetTop(pupil, 9);

            bird.Children.Add(tail);
            bird.Children.Add(body);
            bird.Children.Add(belly);
            bird.Children.Add(wing);
            bird.Children.Add(beak);
            bird.Children.Add(eye);
            bird.Children.Add(pupil);

            return bird;
        }

        private static Brush CreateBirdFill()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 236, 151), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 200, 64), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateBirdStroke()
        {
            var brush = new SolidColorBrush(Color.FromRgb(198, 133, 18));
            brush.Freeze();
            return brush;
        }

        private static Brush CreatePipeFill()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(116, 208, 96), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(59, 156, 71), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush CreatePipeStroke()
        {
            var brush = new SolidColorBrush(Color.FromRgb(44, 122, 53));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
