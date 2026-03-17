using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BIMaestro.Bonus
{
    public partial class FlappyBirdWindow : Window
    {
        private const double BirdSize = 24;
        private const double PipeWidth = 56;
        private const double GapSize = 150;
        private const double Gravity = 0.45;
        private const double FlapStrength = -7.5;
        private const double MaxFallSpeed = 10;
        private const double PipeSpeed = 3.2;
        private const double BirdX = 90;
        private const double PipeCapHeight = 18;
        private const int PipeSpawnTicks = 70;

        private readonly DispatcherTimer _timer;
        private readonly Random _random = new Random();
        private readonly List<Pipe> _pipes = new List<Pipe>();
        private readonly Brush _birdFill = CreateBirdFill();
        private readonly Brush _birdStroke = CreateBirdStroke();
        private readonly Brush _pipeFill = CreatePipeFill();
        private readonly Brush _pipeStroke = CreatePipeStroke();
        private Rectangle _bird;
        private double _birdY;
        private double _birdVelocity;
        private int _ticksSincePipe;
        private bool _running;
        private bool _gameOver;
        private int _score;

        public FlappyBirdWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Focus();
            ResetGame("Appuie sur Espace pour commencer");
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
            _timer.Start();
            _birdVelocity = FlapStrength;
        }

        private void ResetGame(string overlayMessage)
        {
            _timer.Stop();
            _pipes.Clear();
            GameCanvas.Children.Clear();

            _bird = new Rectangle
            {
                Width = BirdSize,
                Height = BirdSize,
                RadiusX = 6,
                RadiusY = 6,
                Fill = _birdFill,
                Stroke = _birdStroke,
                StrokeThickness = 1.2
            };

            GameCanvas.Children.Add(_bird);

            _birdY = (GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 560) / 2 - BirdSize / 2;
            _birdVelocity = 0;
            _ticksSincePipe = 0;
            _score = 0;
            _running = false;
            _gameOver = false;

            UpdateScore();
            PositionBird(BirdX);

            OverlayTitle.Text = "Flappy Bird";
            OverlayMessage.Text = overlayMessage;
            Overlay.Visibility = Visibility.Visible;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_gameOver)
            {
                return;
            }

            UpdateBird();
            UpdatePipes();
            CheckCollision();
        }

        private void UpdateBird()
        {
            _birdVelocity += Gravity;
            if (_birdVelocity > MaxFallSpeed)
            {
                _birdVelocity = MaxFallSpeed;
            }

            _birdY += _birdVelocity;

            PositionBird(BirdX);
        }

        private void PositionBird(double x)
        {
            Canvas.SetLeft(_bird, x);
            Canvas.SetTop(_bird, _birdY);

            double tilt = Math.Max(-30, Math.Min(40, _birdVelocity * 5.5));
            _bird.RenderTransform = new RotateTransform(tilt, BirdSize / 2, BirdSize / 2);
        }

        private void UpdatePipes()
        {
            _ticksSincePipe++;
            if (_ticksSincePipe >= PipeSpawnTicks)
            {
                _ticksSincePipe = 0;
                SpawnPipe();
            }

            for (int i = _pipes.Count - 1; i >= 0; i--)
            {
                var pipe = _pipes[i];
                pipe.X -= PipeSpeed;
                Canvas.SetLeft(pipe.Top, pipe.X);
                Canvas.SetLeft(pipe.Bottom, pipe.X);
                Canvas.SetLeft(pipe.TopCap, pipe.X - 4);
                Canvas.SetLeft(pipe.BottomCap, pipe.X - 4);

                if (!pipe.Passed && pipe.X + PipeWidth < BirdX)
                {
                    pipe.Passed = true;
                    _score++;
                    UpdateScore();
                }

                if (pipe.X + PipeWidth < 0)
                {
                    GameCanvas.Children.Remove(pipe.Top);
                    GameCanvas.Children.Remove(pipe.Bottom);
                    GameCanvas.Children.Remove(pipe.TopCap);
                    GameCanvas.Children.Remove(pipe.BottomCap);
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

            double startX = GameCanvas.ActualWidth > 0 ? GameCanvas.ActualWidth : 360;

            Canvas.SetLeft(topRect, startX);
            Canvas.SetTop(topRect, 0);

            Canvas.SetLeft(bottomRect, startX);
            Canvas.SetTop(bottomRect, bottomY);

            Canvas.SetLeft(topCap, startX - 4);
            Canvas.SetTop(topCap, topHeight - PipeCapHeight);

            Canvas.SetLeft(bottomCap, startX - 4);
            Canvas.SetTop(bottomCap, bottomY);

            GameCanvas.Children.Add(topRect);
            GameCanvas.Children.Add(bottomRect);
            GameCanvas.Children.Add(topCap);
            GameCanvas.Children.Add(bottomCap);

            _pipes.Add(new Pipe
            {
                X = startX,
                Top = topRect,
                Bottom = bottomRect,
                TopCap = topCap,
                BottomCap = bottomCap
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
                var topRect = new Rect(pipe.X, 0, PipeWidth, pipe.Top.Height);
                var bottomRect = new Rect(pipe.X, Canvas.GetTop(pipe.Bottom), PipeWidth, pipe.Bottom.Height);
                var topCapRect = new Rect(pipe.X - 4, Canvas.GetTop(pipe.TopCap), PipeWidth + 8, PipeCapHeight);
                var bottomCapRect = new Rect(pipe.X - 4, Canvas.GetTop(pipe.BottomCap), PipeWidth + 8, PipeCapHeight);

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
            _timer.Stop();

            OverlayTitle.Text = "Perdu !";
            OverlayMessage.Text = "Appuie sur Espace ou clique pour rejouer";
            Overlay.Visibility = Visibility.Visible;
        }

        private void UpdateScore()
        {
            ScoreText.Text = _score.ToString();
        }

        private class Pipe
        {
            public double X { get; set; }
            public Rectangle Top { get; set; }
            public Rectangle Bottom { get; set; }
            public Rectangle TopCap { get; set; }
            public Rectangle BottomCap { get; set; }
            public bool Passed { get; set; }
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
    }
}