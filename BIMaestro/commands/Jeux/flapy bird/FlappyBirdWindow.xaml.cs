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
using System.Windows.Shapes;
using System.Threading.Tasks;
using System.Diagnostics;
using BIMaestro.Localization;

using IOPath = System.IO.Path;

namespace BIMaestro.Bonus
{
    public partial class FlappyBirdWindow : Window
    {
        private const double BirdSize = 24;
        private const double PipeWidth = 56;
        private const double GapSize = 138;
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
        private const string SupabaseFunctionsBaseUrl = "https://xqovxfgghbqxwsadzhzl.functions.supabase.co";

        private readonly Random _random = new Random();
        private readonly List<Pipe> _pipes = new List<Pipe>();
        private readonly Brush _birdFill = CreateBirdFill();
        private readonly Brush _birdStroke = CreateBirdStroke();
        private readonly Brush _pipeFill = CreatePipeFill();
        private readonly Brush _pipeStroke = CreatePipeStroke();
        private Canvas _bird;
        private RotateTransform _birdRotation;
        private TranslateTransform _birdTranslate;
        private double _birdY;
        private double _birdVelocity;
        private double _pipeSpawnAccumulator;
        private bool _running;
        private bool _gameOver;
        private int _score;
        private int _bestScore;
        private int _lastSyncedBestScore;
        private bool _recordThisRun;
        private int _recordScoreThisRun;
        private bool _isSyncingBestScore;
        private bool _isRendering;
        private bool _stateDirty;
        private readonly string _stateFile;
        private readonly Stopwatch _frameStopwatch = new Stopwatch();

        public FlappyBirdWindow()
        {
            InitializeComponent();
            _stateFile = GetStateFilePath();
            LoadState();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Focus();
            ResetGame("Appuie sur Espace pour commencer");
            TriggerBestScoreSync();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            StopRenderLoop();
            _frameStopwatch.Reset();
            SaveStateIfDirty();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            TriggerFlap();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
            {
                return;
            }

            TriggerFlap();
        }

        private void TriggerFlap()
        {

            if (!_running)
            {
                StartGame();
                return;
            }

            if (!_gameOver)
            {
                _birdVelocity = FlapStrength;
            }
        }

        private void StartGame()
        {
            if (_gameOver)
            {
                ResetGame("Appuie sur Espace ou clique pour rejouer");
            }

            _running = true;
            Overlay.Visibility = Visibility.Collapsed;
            _birdVelocity = FlapStrength;
            _frameStopwatch.Restart();
            StartRenderLoop();
        }

        private void ResetGame(string overlayMessage)
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
            GameCanvas.Children.Add(_bird);

            _birdY = (GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 560) / 2 - BirdSize / 2;
            _birdVelocity = 0;
            _pipeSpawnAccumulator = 0;
            _score = 0;
            _recordThisRun = false;
            _recordScoreThisRun = 0;
            _running = false;
            _gameOver = false;
            _frameStopwatch.Reset();

            UpdateScore();
            UpdateBestScoreUi();
            PositionBird(BirdX);

            OverlayTitle.Text = "Flappy Bird";
            OverlayMessage.Text = overlayMessage;
            Overlay.Visibility = Visibility.Visible;
            UpdateOverlayBestScore();
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
                    UpdateScore();

                    if (_score > _bestScore)
                    {
                        _bestScore = _score;
                        _recordThisRun = true;
                        _recordScoreThisRun = _score;
                        UpdateBestScoreUi();
                        UpdateOverlayBestScore();
                        _stateDirty = true;
                    }
                }

                if (pipe.X + PipeWidth < 0)
                {
                    GameCanvas.Children.Remove(pipe.Root);
                    _pipes.RemoveAt(i);
                }
            }
        }

        private void SpawnPipe()
        {
            double canvasHeight = GameCanvas.ActualHeight;
            if (canvasHeight <= 0)
            {
                canvasHeight = 560;
            }

            double minGapCenter = 120;
            double maxGapCenter = canvasHeight - 120;
            if (maxGapCenter <= minGapCenter)
            {
                minGapCenter = canvasHeight * 0.35;
                maxGapCenter = canvasHeight * 0.65;
            }
            double gapCenter = minGapCenter + _random.NextDouble() * (maxGapCenter - minGapCenter);

            double topHeight = gapCenter - GapSize / 2;
            double bottomY = gapCenter + GapSize / 2;
            double bottomHeight = canvasHeight - bottomY;
            double startX = GameCanvas.ActualWidth > 0 ? GameCanvas.ActualWidth : 360;

            var pipeTransform = new TranslateTransform(startX - 4, 0);
            var pipeRoot = new Canvas
            {
                Width = PipeWidth + 8,
                Height = canvasHeight,
                IsHitTestVisible = false,
                RenderTransform = pipeTransform,
                CacheMode = new BitmapCache()
            };

            var topRect = new Rectangle
            {
                Width = PipeWidth,
                Height = topHeight,
                RadiusX = 8,
                RadiusY = 8,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            var bottomRect = new Rectangle
            {
                Width = PipeWidth,
                Height = bottomHeight,
                RadiusX = 8,
                RadiusY = 8,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            var topCap = new Rectangle
            {
                Width = PipeWidth + 8,
                Height = PipeCapHeight,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            var bottomCap = new Rectangle
            {
                Width = PipeWidth + 8,
                Height = PipeCapHeight,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _pipeFill,
                Stroke = _pipeStroke,
                StrokeThickness = 1.5
            };

            Canvas.SetLeft(topRect, 4);
            Canvas.SetTop(topRect, 0);

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

        private void CheckCollision()
        {
            double canvasHeight = GameCanvas.ActualHeight;
            if (canvasHeight <= 0)
            {
                canvasHeight = 560;
            }

            if (_birdY < 0 || _birdY + BirdSize > canvasHeight)
            {
                EndGame();
                return;
            }

            var birdRect = new Rect(BirdX, _birdY, BirdSize, BirdSize);

            foreach (var pipe in _pipes)
            {
                var topRect = new Rect(pipe.X, 0, PipeWidth, pipe.TopHeight);
                var bottomRect = new Rect(pipe.X, pipe.BottomY, PipeWidth, pipe.BottomHeight);
                var topCapRect = new Rect(pipe.X - 4, pipe.TopCapY, PipeWidth + 8, PipeCapHeight);
                var bottomCapRect = new Rect(pipe.X - 4, pipe.BottomY, PipeWidth + 8, PipeCapHeight);

                if (birdRect.IntersectsWith(topRect) || birdRect.IntersectsWith(bottomRect) || birdRect.IntersectsWith(topCapRect) || birdRect.IntersectsWith(bottomCapRect))
                {
                    EndGame();
                    return;
                }
            }
        }

        private void EndGame()
        {
            _gameOver = true;
            _running = false;
            StopRenderLoop();
            _frameStopwatch.Reset();

            OverlayTitle.Text = UiLanguage.T("Perdu !", "Game Over!");
            OverlayMessage.Text = UiLanguage.T("Score : ", "Score: ") + _score + UiLanguage.T(" — Appuie sur Espace ou clique pour rejouer", " — Press Space or Click to Play Again");
            UpdateBestScoreUi();
            UpdateOverlayBestScore();
            Overlay.Visibility = Visibility.Visible;
            SaveStateIfDirty();
            SubmitRecordIfNeeded();
            TriggerBestScoreSync();
        }

        private void UpdateScore()
        {
            ScoreText.Text = _score.ToString();
        }

        private void UpdateBestScoreUi()
        {
            if (BestScoreText != null)
            {
                BestScoreText.Text = _bestScore.ToString();
            }
        }

        private void UpdateOverlayBestScore()
        {
            if (OverlayBestScore != null)
            {
                OverlayBestScore.Text = UiLanguage.T("Record : ", "High Score: ") + _bestScore;
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

        private string GetStateFilePath()
        {
            string logsFolder = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", "SauvegardePréférence");
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
