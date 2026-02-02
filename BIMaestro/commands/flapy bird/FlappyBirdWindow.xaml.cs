using System;
using System.Collections.Generic;
using System.Linq;
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
        private const double PipeSpeed = 3.2;
        private const int PipeSpawnTicks = 70;

        private readonly DispatcherTimer _timer;
        private readonly Random _random = new Random();
        private readonly List<Pipe> _pipes = new List<Pipe>();
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

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
            {
                return;
            }

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
                ResetGame("Appuie sur Espace pour rejouer");
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
                Fill = new SolidColorBrush(Color.FromRgb(255, 218, 86))
            };

            GameCanvas.Children.Add(_bird);

            _birdY = (GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 560) / 2 - BirdSize / 2;
            _birdVelocity = 0;
            _ticksSincePipe = 0;
            _score = 0;
            _running = false;
            _gameOver = false;

            UpdateScore();
            PositionBird(90);

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
            _birdY += _birdVelocity;

            PositionBird(90);
        }

        private void PositionBird(double x)
        {
            Canvas.SetLeft(_bird, x);
            Canvas.SetTop(_bird, _birdY);
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

                if (!pipe.Passed && pipe.X + PipeWidth < 90)
                {
                    pipe.Passed = true;
                    _score++;
                    UpdateScore();
                }

                if (pipe.X + PipeWidth < 0)
                {
                    GameCanvas.Children.Remove(pipe.Top);
                    GameCanvas.Children.Remove(pipe.Bottom);
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
            double gapCenter = minGapCenter + _random.NextDouble() * (maxGapCenter - minGapCenter);

            double topHeight = gapCenter - GapSize / 2;
            double bottomY = gapCenter + GapSize / 2;
            double bottomHeight = canvasHeight - bottomY;

            var topRect = new Rectangle
            {
                Width = PipeWidth,
                Height = topHeight,
                Fill = new SolidColorBrush(Color.FromRgb(68, 173, 59))
            };

            var bottomRect = new Rectangle
            {
                Width = PipeWidth,
                Height = bottomHeight,
                Fill = new SolidColorBrush(Color.FromRgb(68, 173, 59))
            };

            double startX = GameCanvas.ActualWidth > 0 ? GameCanvas.ActualWidth : 360;

            Canvas.SetLeft(topRect, startX);
            Canvas.SetTop(topRect, 0);

            Canvas.SetLeft(bottomRect, startX);
            Canvas.SetTop(bottomRect, bottomY);

            GameCanvas.Children.Add(topRect);
            GameCanvas.Children.Add(bottomRect);

            _pipes.Add(new Pipe
            {
                X = startX,
                Top = topRect,
                Bottom = bottomRect
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

            var birdRect = new Rect(90, _birdY, BirdSize, BirdSize);

            foreach (var pipe in _pipes)
            {
                var topRect = new Rect(pipe.X, 0, PipeWidth, pipe.Top.Height);
                var bottomRect = new Rect(pipe.X, Canvas.GetTop(pipe.Bottom), PipeWidth, pipe.Bottom.Height);

                if (birdRect.IntersectsWith(topRect) || birdRect.IntersectsWith(bottomRect))
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
            OverlayMessage.Text = "Appuie sur Espace pour rejouer";
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
            public bool Passed { get; set; }
        }
    }
}
