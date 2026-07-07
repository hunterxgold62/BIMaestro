using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Page
{
    internal enum SecretEffectKind
    {
        Celebration,
        Fireworks,
        Confetti,
        Character
    }

    internal sealed class SecretEffectWindow : Window
    {
        private readonly Canvas _canvas = new Canvas();
        private readonly Random _random = new Random();

        public SecretEffectWindow(SecretEffectKind kind)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            Focusable = false;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Content = _canvas;

            Loaded += (s, e) => Start(kind);
        }

        private void Start(SecretEffectKind kind)
        {
            switch (kind)
            {
                case SecretEffectKind.Celebration:
                    StartCelebration();
                    CloseAfter(TimeSpan.FromSeconds(11.0));
                    break;
                case SecretEffectKind.Fireworks:
                    StartFireworks();
                    CloseAfter(TimeSpan.FromSeconds(7.4));
                    break;
                case SecretEffectKind.Confetti:
                    StartConfetti();
                    CloseAfter(TimeSpan.FromSeconds(7.2));
                    break;
                case SecretEffectKind.Character:
                    StartCharacter();
                    CloseAfter(TimeSpan.FromSeconds(5.4));
                    break;
            }
        }

        private void StartCelebration()
        {
            int variant = _random.Next(3);
            if (variant == 0)
                StartClassicCelebration();
            else if (variant == 1)
                StartRocketCelebration();
            else
                StartBossFinalCelebration();
        }

        private void StartClassicCelebration()
        {
            StartConfetti();
            StartFireworks();
            StartCharactersAcrossScreens();
        }

        private void StartRocketCelebration()
        {
            StartConfetti();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                LaunchBimaestroRocket();
            };
            timer.Start();
        }

        private void StartBossFinalCelebration()
        {
            StartConfetti();
            StartFireworks();

            var bossTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            bossTimer.Tick += (s, e) =>
            {
                bossTimer.Stop();
                StartBossCharacter();
            };
            bossTimer.Start();
        }

        private void StartFireworks()
        {
            var screens = GetRelativeScreens();
            for (int i = 0; i < 10; i++)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(i * 620) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var screen = screens[_random.Next(screens.Count)];
                    CreateFireworkBurst(
                        screen.Left + _random.NextDouble() * screen.Width * 0.72 + screen.Width * 0.14,
                        screen.Top + _random.NextDouble() * screen.Height * 0.38 + screen.Height * 0.12);
                };
                timer.Start();
            }
        }

        private void CreateFireworkBurst(double x, double y)
        {
            var colors = new[]
            {
                Color.FromRgb(255, 214, 102),
                Color.FromRgb(255, 111, 145),
                Color.FromRgb(118, 214, 255),
                Color.FromRgb(178, 255, 139),
                Color.FromRgb(196, 159, 255)
            };

            int particleCount = 42 + _random.Next(14);
            for (int i = 0; i < particleCount; i++)
            {
                double size = 4 + _random.NextDouble() * 5;
                var dot = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(colors[_random.Next(colors.Length)]),
                    Opacity = 0.95
                };

                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, y);
                _canvas.Children.Add(dot);

                double angle = Math.PI * 2 * i / particleCount + (_random.NextDouble() - 0.5) * 0.18;
                double distance = 90 + _random.NextDouble() * 170;
                double targetX = x + Math.Cos(angle) * distance;
                double targetY = y + Math.Sin(angle) * distance + 50;
                TimeSpan duration = TimeSpan.FromMilliseconds(1300 + _random.Next(1100));

                AnimateCanvasValue(dot, Canvas.LeftProperty, x, targetX, duration, null);
                AnimateCanvasValue(dot, Canvas.TopProperty, y, targetY, duration, new QuadraticEase { EasingMode = EasingMode.EaseIn });
                AnimateOpacity(dot, 0.95, 0, duration, true);
            }
        }

        private void StartConfetti()
        {
            var screens = GetRelativeScreens();
            var colors = new[]
            {
                Color.FromRgb(255, 88, 88),
                Color.FromRgb(255, 207, 64),
                Color.FromRgb(56, 189, 248),
                Color.FromRgb(74, 222, 128),
                Color.FromRgb(216, 180, 254),
                Color.FromRgb(255, 255, 255)
            };

            for (int i = 0; i < 260; i++)
            {
                var screen = screens[_random.Next(screens.Count)];
                double width = 5 + _random.NextDouble() * 8;
                double height = 9 + _random.NextDouble() * 16;
                double startX = screen.Left + _random.NextDouble() * screen.Width;
                double startY = screen.Top - 40 - _random.NextDouble() * screen.Height * 0.45;
                double endX = startX + (_random.NextDouble() - 0.5) * 240;
                double endY = screen.Top + screen.Height + 80 + _random.NextDouble() * 180;
                TimeSpan duration = TimeSpan.FromMilliseconds(4200 + _random.Next(2600));

                var piece = new Rectangle
                {
                    Width = width,
                    Height = height,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(colors[_random.Next(colors.Length)]),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(_random.Next(360)),
                    Opacity = 0.96
                };

                Canvas.SetLeft(piece, startX);
                Canvas.SetTop(piece, startY);
                _canvas.Children.Add(piece);

                AnimateCanvasValue(piece, Canvas.LeftProperty, startX, endX, duration, new SineEase { EasingMode = EasingMode.EaseInOut });
                AnimateCanvasValue(piece, Canvas.TopProperty, startY, endY, duration, new QuadraticEase { EasingMode = EasingMode.EaseIn });
                AnimateRotation((RotateTransform)piece.RenderTransform, _random.Next(360), _random.Next(900, 1600), duration);
                AnimateOpacity(piece, 0.96, 0, TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.95), false);
            }
        }

        private void StartCharactersAcrossScreens()
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                int count = screen.Bounds.Width >= 1800 ? 3 : 2;
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260 + _random.Next(240) + i * 280) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    double ratio = count == 3 ? 0.25 + index * 0.25 : 0.34 + index * 0.28;
                    double yRatio = 0.52 + (_random.NextDouble() - 0.5) * 0.22;
                    StartCharacterOnScreen(screen.Bounds, ratio, yRatio, index % 2 == 0, index + _random.Next(4));
                };
                timer.Start();
            }
            }
        }

        private void StartCharacter()
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            StartCharacterOnScreen(screen, 0.5, 0.56, true, _random.Next(6));
        }

        private void LaunchBimaestroRocket()
        {
            var rocket = CreateRocketModel();
            double startX = -rocket.Width - 180;
            double startY = Height * 0.68;
            double endX = Width * 0.66;
            double endY = Height * 0.16;

            Canvas.SetLeft(rocket, startX);
            Canvas.SetTop(rocket, startY);
            _canvas.Children.Add(rocket);

            var duration = TimeSpan.FromMilliseconds(4300);
            RevealBimaestroLetters(startX, startY, endX, endY, duration);
            AnimateCanvasValue(rocket, Canvas.LeftProperty, startX, endX, duration, new QuadraticEase { EasingMode = EasingMode.EaseOut });

            var top = new DoubleAnimationUsingKeyFrames();
            top.KeyFrames.Add(new EasingDoubleKeyFrame(startY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            top.KeyFrames.Add(new EasingDoubleKeyFrame(endY, KeyTime.FromTimeSpan(duration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            top.Completed += (s, e) =>
            {
                _canvas.Children.Remove(rocket);
                CreateFireworkBurst(endX + 170, endY + 72);
                ShowBimFinale(endX - 125, endY + 74);
            };
            rocket.BeginAnimation(Canvas.TopProperty, top);
        }

        private Canvas CreateRocketModel()
        {
            var rocket = new Canvas
            {
                Width = 380,
                Height = 178,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-24)
            };

            var body = new Rectangle
            {
                Width = 238,
                Height = 72,
                RadiusX = 36,
                RadiusY = 36,
                Fill = new LinearGradientBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(122, 160, 255), 0),
                Stroke = new SolidColorBrush(Color.FromRgb(27, 39, 77)),
                StrokeThickness = 4
            };
            Canvas.SetLeft(body, 88);
            Canvas.SetTop(body, 54);
            rocket.Children.Add(body);

            var nose = new Polygon
            {
                Points = new PointCollection { new Point(318, 54), new Point(378, 90), new Point(318, 126) },
                Fill = new SolidColorBrush(Color.FromRgb(255, 91, 112)),
                Stroke = new SolidColorBrush(Color.FromRgb(102, 20, 36)),
                StrokeThickness = 4
            };
            rocket.Children.Add(nose);

            var finTop = new Polygon
            {
                Points = new PointCollection { new Point(128, 53), new Point(52, 8), new Point(86, 73) },
                Fill = new SolidColorBrush(Color.FromRgb(255, 205, 76))
            };
            rocket.Children.Add(finTop);

            var finBottom = new Polygon
            {
                Points = new PointCollection { new Point(128, 127), new Point(52, 170), new Point(86, 108) },
                Fill = new SolidColorBrush(Color.FromRgb(255, 205, 76))
            };
            rocket.Children.Add(finBottom);

            var flame = new Polygon
            {
                Points = new PointCollection { new Point(88, 68), new Point(0, 90), new Point(88, 116) },
                Fill = new LinearGradientBrush(Color.FromRgb(255, 246, 120), Color.FromRgb(255, 75, 43), 0)
            };
            rocket.Children.Add(flame);

            AddEllipse(rocket, 224, 67, 46, 46, Color.FromRgb(71, 208, 255), Color.FromRgb(22, 72, 112), 4);
            AddText(rocket, "BIM", 132, 66, 38, Brushes.Black);

            var flameScale = new ScaleTransform(1, 1);
            flame.RenderTransformOrigin = new Point(1, 0.5);
            flame.RenderTransform = flameScale;
            flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1.22, TimeSpan.FromMilliseconds(160))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

            return rocket;
        }

        private void RevealBimaestroLetters(double startX, double startY, double endX, double endY, TimeSpan duration)
        {
            string text = "BIMaestro";
            double baseX = Math.Max(40, Width * 0.18);
            double baseY = Math.Max(55, Height * 0.34);
            double spacing = 54;

            for (int i = 0; i < text.Length; i++)
            {
                int index = i;
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(450 + index * (duration.TotalMilliseconds - 1100) / text.Length)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var letter = new TextBlock
                    {
                        Text = text[index].ToString(),
                        FontSize = 72,
                        FontWeight = FontWeights.Black,
                        Foreground = new SolidColorBrush(index < 3 ? Color.FromRgb(255, 210, 88) : Color.FromRgb(255, 255, 255)),
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            BlurRadius = 12,
                            ShadowDepth = 0,
                            Color = Color.FromRgb(88, 118, 255),
                            Opacity = 0.92
                        },
                        Opacity = 0,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new ScaleTransform(0.58, 0.58)
                    };

                    double x = Math.Min(baseX + index * spacing, Width - 90);
                    double y = baseY + Math.Sin(index * 0.7) * 18;
                    Canvas.SetLeft(letter, x);
                    Canvas.SetTop(letter, y);
                    _canvas.Children.Add(letter);

                    var scale = (ScaleTransform)letter.RenderTransform;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.58, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut } });
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.58, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut } });
                    AnimateOpacity(letter, 0, 1, TimeSpan.FromMilliseconds(240), false);
                };
                timer.Start();
            }
        }

        private void ShowBimFinale(double x, double y)
        {
            var text = new TextBlock
            {
                Text = "BIMaestro",
                FontSize = 56,
                FontWeight = FontWeights.Black,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Color = Color.FromRgb(88, 118, 255),
                    Opacity = 0.95
                },
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.72, 0.72)
            };

            Canvas.SetLeft(text, Math.Max(20, Math.Min(x, Width - 360)));
            Canvas.SetTop(text, Math.Max(20, Math.Min(y, Height - 100)));
            _canvas.Children.Add(text);

            var scale = (ScaleTransform)text.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1.08, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, 1.08, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            AnimateOpacity(text, 0, 1, TimeSpan.FromMilliseconds(280), false);

            var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2300) };
            fade.Tick += (s, e) =>
            {
                fade.Stop();
                AnimateOpacity(text, 1, 0, TimeSpan.FromMilliseconds(750), true);
            };
            fade.Start();
        }

        private void StartBossCharacter()
        {
            var parade = new Canvas
            {
                Width = 1120,
                Height = 620,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            for (int i = 0; i < 5; i++)
            {
                var follower = CreateCharacterModel(i + _random.Next(8));
                follower.Opacity = 1;
                follower.RenderTransformOrigin = new Point(0.5, 0.82);
                follower.RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection
                    {
                        new ScaleTransform(0.62, 0.62),
                        new RotateTransform(i % 2 == 0 ? -5 : 5)
                    }
                };
                Canvas.SetLeft(follower, 16 + i * 126);
                Canvas.SetTop(follower, 255 + (i % 2) * 38);
                parade.Children.Add(follower);
                AnimateCharacterPersonality(follower, i + 2);
            }

            var boss = CreateCharacterModel(12);
            boss.Opacity = 1;
            boss.RenderTransformOrigin = new Point(0.5, 0.82);
            boss.RenderTransform = new ScaleTransform(2.05, 2.05);
            Canvas.SetLeft(boss, 610);
            Canvas.SetTop(boss, 20);
            parade.Children.Add(boss);

            AddBossTitle(parade, 645, 398);

            double startX = -parade.Width - 80;
            double endX = Width + 80;
            double y = Math.Max(20, (Height - parade.Height) * 0.5);

            Canvas.SetLeft(parade, startX);
            Canvas.SetTop(parade, y);
            _canvas.Children.Add(parade);

            parade.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(startX, endX, TimeSpan.FromMilliseconds(9000))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            });

            var bob = new DoubleAnimationUsingKeyFrames();
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(y - 28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(y + 8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3600))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(5600))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            parade.BeginAnimation(Canvas.TopProperty, bob);

            AnimateOpacity(parade, 0, 1, TimeSpan.FromMilliseconds(500), false);
        }

        private void StartCharacterOnScreen(System.Drawing.Rectangle screen, double xRatio, double yRatio, bool fromLeft, int personality)
        {
            var character = CreateCharacterModel(personality);
            double screenLeft = screen.Left - Left;
            double screenTop = screen.Top - Top;
            double screenRight = screenLeft + screen.Width;
            double startX = fromLeft ? screenLeft - character.Width - 28 : screenRight + character.Width + 28;
            double centerX = screenLeft + screen.Width * xRatio - character.Width * 0.5;
            double exitX = fromLeft ? screenRight + character.Width + 28 : screenLeft - character.Width - 28;
            double y = screenTop + screen.Height * yRatio - character.Height * 0.5;
            y = Math.Max(screenTop + 24, Math.Min(y, screenTop + screen.Height - character.Height - 24));

            Canvas.SetLeft(character, startX);
            Canvas.SetTop(character, y);
            _canvas.Children.Add(character);

            var bounce = new DoubleAnimationUsingKeyFrames();
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(startX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(centerX + 24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(780))) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(centerX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(centerX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2800))));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(exitX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3900))) { EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseIn } });
            character.BeginAnimation(Canvas.LeftProperty, bounce);

            var hop = new DoubleAnimationUsingKeyFrames();
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(y - 34, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1650))) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2.2, EasingMode = EasingMode.EaseOut } });
            character.BeginAnimation(Canvas.TopProperty, hop);

            AnimateCharacterPersonality(character, personality);
            AnimateOpacity(character, 0, 1, TimeSpan.FromMilliseconds(260), false);
        }

        private List<Rect> GetRelativeScreens()
        {
            var result = new List<Rect>();
            foreach (var screen in Forms.Screen.AllScreens)
            {
                result.Add(new Rect(
                    screen.Bounds.Left - Left,
                    screen.Bounds.Top - Top,
                    screen.Bounds.Width,
                    screen.Bounds.Height));
            }

            if (result.Count == 0)
                result.Add(new Rect(0, 0, Width, Height));

            return result;
        }

        private Canvas CreateCharacterModel(int personality)
        {
            var bodyColors = new[]
            {
                Color.FromRgb(58, 101, 242),
                Color.FromRgb(14, 165, 132),
                Color.FromRgb(225, 91, 91),
                Color.FromRgb(130, 92, 224),
                Color.FromRgb(245, 158, 11),
                Color.FromRgb(43, 128, 185)
            };
            var shirtColors = new[]
            {
                Color.FromRgb(88, 118, 255),
                Color.FromRgb(45, 212, 191),
                Color.FromRgb(248, 113, 113),
                Color.FromRgb(167, 139, 250),
                Color.FromRgb(251, 191, 36),
                Color.FromRgb(96, 165, 250)
            };
            var helmetColors = new[]
            {
                Color.FromRgb(255, 210, 88),
                Color.FromRgb(255, 255, 255),
                Color.FromRgb(86, 196, 255),
                Color.FromRgb(255, 170, 200),
                Color.FromRgb(250, 204, 21),
                Color.FromRgb(147, 197, 253)
            };
            var badges = new[] { "B", "M", "R", "3D", "IA", "!" };
            int variant = Math.Abs(personality) % bodyColors.Length;

            var root = new Canvas
            {
                Width = 230,
                Height = 285,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.82)
            };

            var scale = new ScaleTransform(1, 1);
            var rotate = new RotateTransform((variant % 2 == 0) ? -2 : 2);
            root.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection { scale, rotate }
            };

            AddEllipse(root, 34, 238, 160, 24, Color.FromArgb(70, 0, 0, 0), null, 0);
            AddRoundedRect(root, 68, 118, 94, 100, 22, bodyColors[variant], Color.FromRgb(31, 41, 86), 3);
            AddRoundedRect(root, 84, 130, 62, 66, 18, shirtColors[variant], null, 0);
            AddEllipse(root, 73, 50, 84, 82, Color.FromRgb(255, 220, 181), Color.FromRgb(71, 55, 48), 3);
            AddEllipse(root, 61, 42, 108, 38, Color.FromRgb(36, 43, 58), Color.FromRgb(12, 17, 28), 3);
            AddRoundedRect(root, 82, 31, 67, 26, 11, helmetColors[variant], Color.FromRgb(122, 88, 20), 2);
            AddRoundedRect(root, 90, 138, 50, 28, 10, Color.FromRgb(247, 250, 252), Color.FromRgb(31, 41, 86), 2);
            AddText(root, badges[variant], badges[variant].Length > 1 ? 97 : 105, 137, badges[variant].Length > 1 ? 19 : 24, Brushes.Black);
            AddEllipse(root, 93, 81, 10, 14, Colors.Black, null, 0);
            AddEllipse(root, 128, 81, 10, 14, Colors.Black, null, 0);
            AddEllipse(root, 97, 84, 3, 4, Colors.White, null, 0);
            AddEllipse(root, 132, 84, 3, 4, Colors.White, null, 0);
            AddSmile(root);
            AddLimb(root, 45, 134, -18, variant == 1 || variant == 4);
            AddLimb(root, 156, 134, 18, variant != 2);
            AddLeg(root, 82, 207, -10);
            AddLeg(root, 128, 207, 10);

            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1180))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } },
                    new EasingDoubleKeyFrame(1.04, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1380))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } },
                    new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1640))) { EasingFunction = new BounceEase { Bounces = 1, Bounciness = 2.5, EasingMode = EasingMode.EaseOut } }
                }
            });

            return root;
        }

        private static void AnimateCharacterPersonality(Canvas character, int personality)
        {
            var transforms = character.RenderTransform as TransformGroup;
            var rotate = transforms?.Children.Count > 1 ? transforms.Children[1] as RotateTransform : null;
            if (rotate == null) return;

            int action = Math.Abs(personality) % 3;
            if (action == 0)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = new RepeatBehavior(3),
                    AutoReverse = true,
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
                    }
                });
            }
            else if (action == 1)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = new RepeatBehavior(2),
                    AutoReverse = true,
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } }
                    }
                });
            }
            else
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = new RepeatBehavior(2),
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } },
                        new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
                    }
                });
            }
        }

        private static void AddEllipse(Canvas root, double x, double y, double width, double height, Color fill, Color? stroke, double strokeThickness)
        {
            var ellipse = new Ellipse
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(fill),
                Stroke = stroke.HasValue ? new SolidColorBrush(stroke.Value) : null,
                StrokeThickness = strokeThickness
            };
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            root.Children.Add(ellipse);
        }

        private static void AddRoundedRect(Canvas root, double x, double y, double width, double height, double radius, Color fill, Color? stroke, double strokeThickness)
        {
            var rect = new Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = radius,
                RadiusY = radius,
                Fill = new SolidColorBrush(fill),
                Stroke = stroke.HasValue ? new SolidColorBrush(stroke.Value) : null,
                StrokeThickness = strokeThickness
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            root.Children.Add(rect);
        }

        private static void AddText(Canvas root, string text, double x, double y, double size, Brush fill)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = fill
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            root.Children.Add(label);
        }

        private static void AddBossTitle(Canvas root, double x, double y)
        {
            AddText(root, "BIMaestro", x + 5, y + 6, 52, new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)));
            AddText(root, "BIMaestro", x - 2, y, 52, new SolidColorBrush(Color.FromRgb(20, 28, 44)));
            AddText(root, "BIMaestro", x + 2, y, 52, new SolidColorBrush(Color.FromRgb(20, 28, 44)));
            AddText(root, "BIMaestro", x, y - 2, 52, new SolidColorBrush(Color.FromRgb(20, 28, 44)));
            AddText(root, "BIMaestro", x, y + 2, 52, new SolidColorBrush(Color.FromRgb(20, 28, 44)));
            AddText(root, "BIMaestro", x, y, 52, new SolidColorBrush(Color.FromRgb(255, 210, 88)));
            AddText(root, "BIMaestro", x + 2, y + 2, 48, new SolidColorBrush(Color.FromRgb(88, 118, 255)));
        }

        private static void AddSmile(Canvas root)
        {
            var figure = new PathFigure { StartPoint = new Point(101, 105) };
            figure.Segments.Add(new QuadraticBezierSegment(new Point(115, 116), new Point(130, 105), true));
            var path = new Path
            {
                Data = new PathGeometry(new[] { figure }),
                Stroke = new SolidColorBrush(Color.FromRgb(101, 59, 45)),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            root.Children.Add(path);
        }

        private static void AddLimb(Canvas root, double x, double y, double angle, bool wave)
        {
            var arm = new Rectangle
            {
                Width = 18,
                Height = 70,
                RadiusX = 9,
                RadiusY = 9,
                Fill = new SolidColorBrush(Color.FromRgb(255, 220, 181)),
                RenderTransformOrigin = new Point(0.5, 0.12)
            };
            var rotate = new RotateTransform(angle);
            arm.RenderTransform = rotate;
            Canvas.SetLeft(arm, x);
            Canvas.SetTop(arm, y);
            root.Children.Add(arm);

            if (wave)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
                {
                    RepeatBehavior = new RepeatBehavior(2),
                    AutoReverse = true,
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(24, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(58, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
                    }
                });
            }
        }

        private static void AddLeg(Canvas root, double x, double y, double angle)
        {
            var leg = new Rectangle
            {
                Width = 20,
                Height = 54,
                RadiusX = 8,
                RadiusY = 8,
                Fill = new SolidColorBrush(Color.FromRgb(38, 50, 74)),
                RenderTransformOrigin = new Point(0.5, 0)
            };
            leg.RenderTransform = new RotateTransform(angle);
            Canvas.SetLeft(leg, x);
            Canvas.SetTop(leg, y);
            root.Children.Add(leg);
        }

        private static void AnimateCanvasValue(UIElement target, DependencyProperty property, double from, double to, TimeSpan duration, IEasingFunction easing)
        {
            var animation = new DoubleAnimation(from, to, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (s, e) =>
            {
                target.SetValue(property, to);
                var parent = VisualTreeHelper.GetParent(target) as Panel;
                if (property == Canvas.TopProperty && to > SystemParameters.VirtualScreenHeight + 60)
                    parent?.Children.Remove(target);
            };
            target.BeginAnimation(property, animation);
        }

        private static void AnimateOpacity(UIElement target, double from, double to, TimeSpan duration, bool removeWhenDone)
        {
            var animation = new DoubleAnimation(from, to, duration)
            {
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (s, e) =>
            {
                target.Opacity = to;
                if (removeWhenDone)
                {
                    var parent = VisualTreeHelper.GetParent(target) as Panel;
                    parent?.Children.Remove(target);
                }
            };
            target.BeginAnimation(OpacityProperty, animation);
        }

        private static void AnimateRotation(RotateTransform transform, double from, double to, TimeSpan duration)
        {
            transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, to, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        private void CloseAfter(TimeSpan delay)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }
    }
}
