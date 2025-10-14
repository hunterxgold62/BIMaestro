using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BIMaestro.UI
{
    public partial class StuntSpinner : UserControl
    {
        public StuntSpinner()
        {
            InitializeComponent();
            Loaded += (_, __) => Rebuild();
            SizeChanged += (_, __) => Rebuild();
        }

        // ====== API publique (DP) ======
        public static readonly DependencyProperty DiameterProperty =
            DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(StuntSpinner),
                new PropertyMetadata(128d, OnAnyChanged));
        public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

        public static readonly DependencyProperty ThicknessProperty =
            DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(StuntSpinner),
                new PropertyMetadata(16d, OnAnyChanged));
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

        public static readonly DependencyProperty RingBrushProperty =
            DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(StuntSpinner),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0))));
        public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

        public static readonly DependencyProperty WormBrushProperty =
            DependencyProperty.Register(nameof(WormBrush), typeof(Brush), typeof(StuntSpinner),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(95, 80, 255)))); // violet/bleu
        public Brush WormBrush { get => (Brush)GetValue(WormBrushProperty); set => SetValue(WormBrushProperty, value); }

        public static readonly DependencyProperty DurationSecondsProperty =
            DependencyProperty.Register(nameof(DurationSeconds), typeof(double), typeof(StuntSpinner),
                new PropertyMetadata(5.0, OnAnyChanged)); // comme l’original CSS
        public double DurationSeconds { get => (double)GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }

        public static readonly DependencyProperty SaltoOffsetProperty =
            DependencyProperty.Register(nameof(SaltoOffset), typeof(double), typeof(StuntSpinner),
                new PropertyMetadata(6.0)); // combien “sort” en haut (px)
        public double SaltoOffset { get => (double)GetValue(SaltoOffsetProperty); set => SetValue(SaltoOffsetProperty, value); }

        public static readonly DependencyProperty IsRunningProperty =
            DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(StuntSpinner),
                new PropertyMetadata(true, OnIsRunningChanged));
        public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

        // lecture seule (binding interne)
        public Geometry WormGeometry
        {
            get => (Geometry)GetValue(WormGeometryProperty);
            private set => SetValue(WormGeometryPropertyKey, value);
        }
        private static readonly DependencyPropertyKey WormGeometryPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(WormGeometry), typeof(Geometry), typeof(StuntSpinner),
                new PropertyMetadata(null));
        public static readonly DependencyProperty WormGeometryProperty = WormGeometryPropertyKey.DependencyProperty;

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (StuntSpinner)d;
            if (c.IsLoaded) c.Rebuild();
        }

        private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (StuntSpinner)d;
            if (!c.IsLoaded) return;
            if ((bool)e.NewValue) c.Start(); else c.Stop();
        }

        // ====== internes ======
        private Storyboard _sbDash, _sbSalto;
        private double _circumference;   // en pixels
        private const double CssR = 56.0;           // rayon dans l’exemple CSS
        private const double CssCirc = 2 * Math.PI * CssR;
        private const double CssSeg = 43.98;        // longueur “visible” dans l’exemple CSS

        private void Rebuild()
        {
            var D = Math.Max(4, Diameter);
            var T = Math.Max(1, Thickness);
            var R = (D / 2.0) - (T / 2.0);   // rayon au centre du trait

            // Anneau
            Ring.Width = D; Ring.Height = D;

            // Le ver suit le contour d’un cercle (EllipseGeometry)
            WormGeometry = new EllipseGeometry(new Point(D / 2, D / 2), R, R);

            // DashArray/Offset en unités WPF :
            // En WPF, les valeurs sont en *unités physiques* mais exprimées relativement à Thickness (1 = 1*Thickness).
            // On met donc des longueurs réelles / T.
            _circumference = 2 * Math.PI * R;
            var visibleSeg = _circumference * (CssSeg / CssCirc); // conserve le ratio de l’exemple
            var gapSeg = Math.Max(1.0, _circumference - visibleSeg);

            var da = new DoubleCollection { visibleSeg / T, gapSeg / T };
            Worm.StrokeDashArray = da;

            BuildAnimations();
            if (IsRunning) Start();
        }

        private void BuildAnimations()
        {
            _sbDash?.Stop(); _sbSalto?.Stop();
            _sbDash = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            _sbSalto = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            var T = Thickness;
            var dur = TimeSpan.FromSeconds(Math.Max(0.1, DurationSeconds));

            // ---- DASH OFFSET (même keyframes que le CSS .sp__worm1, mais mis à l’échelle) ----
            // offsets CSS pour R=56 (en px le long du chemin). On les met à l’échelle sur notre circonférence.
            double Scale(double css) => (css * (_circumference / CssCirc)) / T; // converti et /Thickness

            var dashKF = new DoubleAnimationUsingKeyFrames
            {
                Duration = dur
            };

            // from/to 0
            dashKF.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            dashKF.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(1)));

            // 12.5% → -175.91
            dashKF.KeyFrames.Add(new EasingDoubleKeyFrame(-Scale(175.91), KeyTime.FromPercent(0.125))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });

            // 25% → -307.88
            dashKF.KeyFrames.Add(new EasingDoubleKeyFrame(-Scale(307.88), KeyTime.FromPercent(0.25))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            // 50% → -483.8
            dashKF.KeyFrames.Add(new EasingDoubleKeyFrame(-Scale(483.80), KeyTime.FromPercent(0.5))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });

            // 62.5% → -307.88
            dashKF.KeyFrames.Add(new EasingDoubleKeyFrame(-Scale(307.88), KeyTime.FromPercent(0.625))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });

            // 75% → -175.91
            dashKF.KeyFrames.Add(new EasingDoubleKeyFrame(-Scale(175.91), KeyTime.FromPercent(0.75))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            Storyboard.SetTarget(dashKF, Worm);
            Storyboard.SetTargetProperty(dashKF, new PropertyPath(Shape.StrokeDashOffsetProperty));
            _sbDash.Children.Add(dashKF);

            // ---- SALTO (décalage radial en haut) ----
            // On lève légèrement le “ver” quand son offset passe par le haut (≈12.5% et 75%)
            double s = Math.Max(0, SaltoOffset);
            var saltoKF = new DoubleAnimationUsingKeyFrames { Duration = dur };

            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.00)));
            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(-s, KeyTime.FromPercent(0.125))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.25))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });

            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.625)));
            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(-s, KeyTime.FromPercent(0.75))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            saltoKF.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.00))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });

            Storyboard.SetTarget(saltoKF, this);
            Storyboard.SetTargetProperty(saltoKF, new PropertyPath("(UserControl).WormOffset.Y"));
            _sbSalto.Children.Add(saltoKF);
        }

        public void Start()
        {
            if (!IsLoaded) return;
            Stop();
            _sbDash?.Begin(this, true);
            _sbSalto?.Begin(this, true);
        }

        public void Stop()
        {
            _sbDash?.Stop(this);
            _sbSalto?.Stop(this);
        }
    }
}
