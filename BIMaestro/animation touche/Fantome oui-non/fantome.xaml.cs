using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BIMaestro.UI
{
    public partial class GhostSwitch : UserControl
    {
        public GhostSwitch()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (_, __) => ApplyLayout();
            IsVisibleChanged += OnIsVisibleChanged; // <<< auto-pause en quittant l’onglet
        }

        // ========== Dependency Properties ==========
        public static readonly DependencyProperty TrackWidthProperty =
            DependencyProperty.Register(nameof(TrackWidth), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(100d, OnSizeDpChanged));
        public double TrackWidth { get => (double)GetValue(TrackWidthProperty); set => SetValue(TrackWidthProperty, value); }

        public static readonly DependencyProperty TrackHeightProperty =
            DependencyProperty.Register(nameof(TrackHeight), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(25d, OnSizeDpChanged));
        public double TrackHeight { get => (double)GetValue(TrackHeightProperty); set => SetValue(TrackHeightProperty, value); }

        public static readonly DependencyProperty GhostSizeProperty =
            DependencyProperty.Register(nameof(GhostSize), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(40d, OnSizeDpChanged));
        public double GhostSize { get => (double)GetValue(GhostSizeProperty); set => SetValue(GhostSizeProperty, value); }

        public static readonly DependencyProperty AutoSlideDistanceProperty =
            DependencyProperty.Register(nameof(AutoSlideDistance), typeof(bool), typeof(GhostSwitch),
                new PropertyMetadata(true, OnSizeDpChanged));
        public bool AutoSlideDistance { get => (bool)GetValue(AutoSlideDistanceProperty); set => SetValue(AutoSlideDistanceProperty, value); }

        public static readonly DependencyProperty SlideDistanceProperty =
            DependencyProperty.Register(nameof(SlideDistance), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(65d));
        public double SlideDistance { get => (double)GetValue(SlideDistanceProperty); set => SetValue(SlideDistanceProperty, value); }

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(GhostSwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));
        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

        // ---- Suivi des yeux ----
        public static readonly DependencyProperty EyeFollowGlobalProperty =
            DependencyProperty.Register(nameof(EyeFollowGlobal), typeof(bool), typeof(GhostSwitch),
                new PropertyMetadata(true, OnFollowModeChanged));
        public bool EyeFollowGlobal { get => (bool)GetValue(EyeFollowGlobalProperty); set => SetValue(EyeFollowGlobalProperty, value); }

        public static readonly DependencyProperty EyeFollowCursorProperty =
            DependencyProperty.Register(nameof(EyeFollowCursor), typeof(bool), typeof(GhostSwitch),
                new PropertyMetadata(false, OnFollowModeChanged));
        public bool EyeFollowCursor { get => (bool)GetValue(EyeFollowCursorProperty); set => SetValue(EyeFollowCursorProperty, value); }

        public static readonly DependencyProperty EyeLookAmountProperty =
            DependencyProperty.Register(nameof(EyeLookAmount), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(0.12));
        public double EyeLookAmount { get => (double)GetValue(EyeLookAmountProperty); set => SetValue(EyeLookAmountProperty, value); }

        public static readonly DependencyProperty EyeLookAmountYProperty =
            DependencyProperty.Register(nameof(EyeLookAmountY), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(0.12));
        public double EyeLookAmountY { get => (double)GetValue(EyeLookAmountYProperty); set => SetValue(EyeLookAmountYProperty, value); }

        public static readonly DependencyProperty ArmLagAmountProperty =
            DependencyProperty.Register(nameof(ArmLagAmount), typeof(double), typeof(GhostSwitch),
                new PropertyMetadata(0.0));
        public double ArmLagAmount { get => (double)GetValue(ArmLagAmountProperty); set => SetValue(ArmLagAmountProperty, value); }

        // Auto-pause quand l’onglet n’est plus visible
        public static readonly DependencyProperty AutoPauseWhenHiddenProperty =
            DependencyProperty.Register(nameof(AutoPauseWhenHidden), typeof(bool), typeof(GhostSwitch),
                new PropertyMetadata(true));
        public bool AutoPauseWhenHidden { get => (bool)GetValue(AutoPauseWhenHiddenProperty); set => SetValue(AutoPauseWhenHiddenProperty, value); }

        // RoutedEvent "Toggled"
        public static readonly RoutedEvent ToggledEvent =
            EventManager.RegisterRoutedEvent("Toggled", RoutingStrategy.Bubble,
                typeof(RoutedPropertyChangedEventHandler<bool>), typeof(GhostSwitch));
        public event RoutedPropertyChangedEventHandler<bool> Toggled
        {
            add { AddHandler(ToggledEvent, value); }
            remove { RemoveHandler(ToggledEvent, value); }
        }

        // ========== internals ==========
        private double _effectiveSlide;
        private double _eyeBaseX; // yeux centrés en hauteur
        private double _armBaseX;
        private Window? _hostWindow;

        private bool _appHooked;     // InputManager PreProcessInput (global)
        private bool _localHooked;   // sur ToggleHit (local)
        private DateTime _lastEyeUpdate = DateTime.MinValue;

        public CornerRadius TrackCornerRadius => new CornerRadius(TrackHeight / 2.0);

        // ----- lifecycle -----
        private static void OnSizeDpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (GhostSwitch)d;
            if (c.IsLoaded) c.ApplyLayout();
        }

        private static void OnFollowModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (GhostSwitch)d;
            if (!c.IsLoaded) return;
            c.ConfigureMouseHooks(reset: true);
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _hostWindow = Window.GetWindow(this);
            ConfigureMouseHooks(reset: true);

            ApplyLayout();
            AnimateToState(IsOn, instant: true);
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            ConfigureMouseHooks(reset: true, attach: false);
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!AutoPauseWhenHidden) return;

            if (IsVisible)
            {
                // on revient sur l’onglet → on ré-attache
                ConfigureMouseHooks(reset: true, attach: true);
            }
            else
            {
                // on quitte l’onglet → on détache + yeux au neutre
                ConfigureMouseHooks(reset: true, attach: false);
                ResetEyes();
            }
        }

        // ----- hooks souris (idempotent) -----
        private void ConfigureMouseHooks(bool reset, bool attach = true)
        {
            if (reset)
            {
                if (_appHooked)
                {
                    try { InputManager.Current.PreProcessInput -= OnPreProcessInput; } catch { }
                    _appHooked = false;
                }
                if (_localHooked)
                {
                    try
                    {
                        WeakEventManager<UIElement, MouseEventArgs>.RemoveHandler(ToggleHit, nameof(ToggleHit.MouseMove), OnLocalMouseMove);
                        WeakEventManager<UIElement, MouseEventArgs>.RemoveHandler(ToggleHit, nameof(ToggleHit.MouseLeave), OnLocalMouseLeave);
                    }
                    catch { }
                    _localHooked = false;
                }
            }

            if (!attach || !IsVisible) return; // <<< ne rien accrocher si pas visible

            if (EyeFollowGlobal)
            {
                InputManager.Current.PreProcessInput += OnPreProcessInput;
                _appHooked = true;
            }
            else if (EyeFollowCursor)
            {
                WeakEventManager<UIElement, MouseEventArgs>.AddHandler(ToggleHit, nameof(ToggleHit.MouseMove), OnLocalMouseMove);
                WeakEventManager<UIElement, MouseEventArgs>.AddHandler(ToggleHit, nameof(ToggleHit.MouseLeave), OnLocalMouseLeave);
                _localHooked = true;
            }
        }

        // ----- IsOn -----
        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (GhostSwitch)d;
            if (c.IsLoaded) c.AnimateToState((bool)e.NewValue);

            var args = new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue)
            { RoutedEvent = ToggledEvent };
            c.RaiseEvent(args);
        }

        // ----- Layout -----
        private void ApplyLayout()
        {
            Track.Width = TrackWidth;
            Track.Height = TrackHeight;
            Track.CornerRadius = new CornerRadius(TrackHeight / 2.0);

            var leftMargin = Math.Max(0, (GhostSize * 0.5) - (TrackHeight * 0.5));
            Track.Margin = new Thickness(leftMargin, 0, 0, 0);

            Ghost.Width = GhostSize;
            Ghost.Height = GhostSize;

            var ear = Math.Max(10, GhostSize * 0.28);
            EarLeft.Width = EarLeft.Height = ear;
            EarRight.Width = EarRight.Height = ear;

            _eyeBaseX = GhostSize * 0.18;
            EyeLBase.X = -_eyeBaseX; EyeLBase.Y = 0;
            EyeRBase.X = +_eyeBaseX; EyeRBase.Y = 0;
            EyeLLook.X = EyeRLook.X = 0;
            EyeLLook.Y = EyeRLook.Y = 0;

            var eyeW = Math.Max(2, GhostSize * 0.08);
            var eyeH = Math.Max(6, GhostSize * 0.24);
            EyeL.Width = EyeR.Width = eyeW;
            EyeL.Height = EyeR.Height = eyeH;

            var arm = Math.Max(9, GhostSize * 0.34);
            ArmLeft.Width = ArmLeft.Height = arm;
            ArmRight.Width = ArmRight.Height = arm;

            _armBaseX = GhostSize * 0.42;
            ArmLShift.X = -_armBaseX; ArmLShift.Y = 0;
            ArmRShift.X = +_armBaseX; ArmRShift.Y = 0;

            _effectiveSlide = AutoSlideDistance ? Math.Max(0, TrackWidth - TrackHeight) : SlideDistance;

            Width = leftMargin + TrackWidth + 16;
            Height = Math.Max(TrackHeight, GhostSize) + 16;

            GhostX.X = IsOn ? _effectiveSlide : 0;
            GhostY.Y = 0;
            GhostScale.ScaleX = GhostScale.ScaleY = 1;
        }

        // ----- Animations d’état -----
        private void AnimateToState(bool on, bool instant = false)
        {
            var moveDur = instant ? TimeSpan.Zero : TimeSpan.FromMilliseconds(380);
            var colorDur = instant ? TimeSpan.Zero : TimeSpan.FromMilliseconds(380);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            GhostX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                To = on ? _effectiveSlide : 0,
                Duration = moveDur,
                EasingFunction = ease
            });

            var from = (Track.Background as SolidColorBrush)?.Color ?? Colors.Gray;
            var to = on ? (Color)ColorConverter.ConvertFromString("#459DEF")!
                          : (Color)ColorConverter.ConvertFromString("#7F7F7F")!;
            var brush = new SolidColorBrush(from);
            Track.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation { To = to, Duration = colorDur });

            var squash = new DoubleAnimation
            {
                From = 1.00,
                To = 0.94,
                Duration = TimeSpan.FromMilliseconds(120),
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var stretch = new DoubleAnimation
            {
                From = 1.00,
                To = 1.06,
                Duration = TimeSpan.FromMilliseconds(120),
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            GhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
            GhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);

            var armLag = GhostSize * ArmLagAmount * (on ? -1 : +1);
            var ease2 = new CubicEase { EasingMode = EasingMode.EaseInOut };
            ArmLShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            { To = -_armBaseX + armLag, Duration = TimeSpan.FromMilliseconds(280), EasingFunction = ease2 });
            ArmRShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            { To = +_armBaseX + armLag, Duration = TimeSpan.FromMilliseconds(280), EasingFunction = ease2 });
        }

        // ====== Suivi global via InputManager (application) ======
        private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
        {
            if (!EyeFollowGlobal || (EyeLookAmount <= 0 && EyeLookAmountY <= 0)) return;

            var now = DateTime.UtcNow;
            if ((now - _lastEyeUpdate).TotalMilliseconds < 16) return;
            _lastEyeUpdate = now;

            if (!IsLoaded || !IsVisible) return;

            var win = _hostWindow ?? Window.GetWindow(this);
            if (win == null) return;

            Point p;
            try { p = Mouse.GetPosition(win); } catch { return; }

            if (p.X < 0 || p.Y < 0 || p.X > win.ActualWidth || p.Y > win.ActualHeight)
            {
                ResetEyes();
                return;
            }

            UpdateEyeTargetFromPointInWindow(p, win);
        }

        // ====== Fallback local (survol) ======
        private void OnLocalMouseMove(object? sender, MouseEventArgs e)
        {
            if (!EyeFollowCursor || (EyeLookAmount <= 0 && EyeLookAmountY <= 0)) return;

            var now = DateTime.UtcNow;
            if ((now - _lastEyeUpdate).TotalMilliseconds < 16) return;
            _lastEyeUpdate = now;

            Point p;
            try { p = e.GetPosition(Ghost); } catch { return; }
            UpdateEyeTargetFromLocalPoint(p);
        }
        private void OnLocalMouseLeave(object? s, MouseEventArgs e) => ResetEyes();

        // ====== Utilitaires regard ======
        private void UpdateEyeTargetFromPointInWindow(Point pWindow, Window win)
        {
            try
            {
                var gt = Ghost.TransformToAncestor(win);
                var rect = gt.TransformBounds(new Rect(0, 0, Ghost.ActualWidth, Ghost.ActualHeight));
                var center = new Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
                UpdateEyesFromVector(new Point(pWindow.X - center.X, pWindow.Y - center.Y), rect.Width, rect.Height);
            }
            catch { /* visuel pas dans l’arbre → ignorer */ }
        }

        private void UpdateEyeTargetFromLocalPoint(Point pLocal)
        {
            var center = new Point(Ghost.ActualWidth / 2.0, Ghost.ActualHeight / 2.0);
            UpdateEyesFromVector(new Point(pLocal.X - center.X, pLocal.Y - center.Y),
                                 Ghost.ActualWidth, Ghost.ActualHeight);
        }

        private void UpdateEyesFromVector(Point v, double w, double h)
        {
            double nx = v.X / (w / 2.0 * 0.85);
            double ny = v.Y / (h / 2.0 * 0.85);
            var mag = Math.Sqrt(nx * nx + ny * ny);
            if (mag > 1.0 && mag > 0) { nx /= mag; ny /= mag; }
            nx = Math.Max(-1, Math.Min(1, nx));
            ny = Math.Max(-1, Math.Min(1, ny));

            double targetX = nx * (GhostSize * EyeLookAmount);
            double targetY = ny * (GhostSize * EyeLookAmountY);

            var fast = TimeSpan.FromMilliseconds(70);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            EyeLLook.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = targetX, Duration = fast, EasingFunction = ease });
            EyeRLook.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = targetX, Duration = fast, EasingFunction = ease });
            EyeLLook.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = targetY, Duration = fast, EasingFunction = ease });
            EyeRLook.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = targetY, Duration = fast, EasingFunction = ease });
        }

        private void ResetEyes()
        {
            var back = TimeSpan.FromMilliseconds(140);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            EyeLLook.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = 0, Duration = back, EasingFunction = ease });
            EyeRLook.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = 0, Duration = back, EasingFunction = ease });
            EyeLLook.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = back, EasingFunction = ease });
            EyeRLook.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation { To = 0, Duration = back, EasingFunction = ease });
        }
    }
}
