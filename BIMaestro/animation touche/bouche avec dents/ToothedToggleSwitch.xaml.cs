using CloudinaryDotNet;
using MathNet.Numerics.Random;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BIMaestro.UI
{
    public partial class ToothedToggleSwitch : UserControl
    {
        public ToothedToggleSwitch()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => ApplyLayout();
        }

        // ========= API =========
        public static readonly DependencyProperty BaseSizeProperty =
            DependencyProperty.Register(nameof(BaseSize), typeof(double), typeof(ToothedToggleSwitch),
                new PropertyMetadata(26d, OnLayoutDp));
        public double BaseSize { get => (double)GetValue(BaseSizeProperty); set => SetValue(BaseSizeProperty, value); }

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(ToothedToggleSwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));
        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

        public static readonly DependencyProperty TransitionMsProperty =
            DependencyProperty.Register(nameof(TransitionMs), typeof(int), typeof(ToothedToggleSwitch),
                new PropertyMetadata(500));
        public int TransitionMs { get => (int)GetValue(TransitionMsProperty); set => SetValue(TransitionMsProperty, value); }

        public static readonly DependencyProperty TeethCountProperty =
            DependencyProperty.Register(nameof(TeethCount), typeof(int), typeof(ToothedToggleSwitch),
                new PropertyMetadata(10, OnLayoutDp));
        public int TeethCount { get => (int)GetValue(TeethCountProperty); set => SetValue(TeethCountProperty, value); }

        public static readonly DependencyProperty ShowLabelsProperty =
            DependencyProperty.Register(nameof(ShowLabels), typeof(bool), typeof(ToothedToggleSwitch),
                new PropertyMetadata(true, (d, e) => ((ToothedToggleSwitch)d).Lights.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));
        public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

        // couleurs (comme ta démo)
        public Color OnColor { get; set; } = (Color)ColorConverter.ConvertFromString("#4CAF50");
        public Color OffColor { get; set; } = (Color)ColorConverter.ConvertFromString("#f50000");
        public Color GreyStep { get; set; } = (Color)ColorConverter.ConvertFromString("#666666");

        // event "Toggled"
        public static readonly RoutedEvent ToggledEvent =
            EventManager.RegisterRoutedEvent("Toggled", RoutingStrategy.Bubble,
                typeof(RoutedPropertyChangedEventHandler<bool>), typeof(ToothedToggleSwitch));
        public event RoutedPropertyChangedEventHandler<bool> Toggled { add => AddHandler(ToggledEvent, value); remove => RemoveHandler(ToggledEvent, value); }

        // ========= internals =========
        double _sz, _w, _h, _bezel, _innerW, _innerH, _thumb, _slide, _startX;
        static void OnLayoutDp(DependencyObject d, DependencyPropertyChangedEventArgs e) { var c = (ToothedToggleSwitch)d; if (c.IsLoaded) c.ApplyLayout(); }
        static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (ToothedToggleSwitch)d;
            if (!c.IsLoaded) return;
            c.AnimateState((bool)e.NewValue);
            var args = new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue) { RoutedEvent = ToggledEvent };
            c.RaiseEvent(args);
        }

        void OnLoaded(object? s, RoutedEventArgs e)
        {
            Lights.Visibility = ShowLabels ? Visibility.Visible : Visibility.Collapsed;
            ApplyLayout();
            PlayIntro();
            AnimateState(IsOn, true);
        }

        void ApplyLayout()
        {
            // tailles générales (CSS: width = 6*sz, height = 2*sz)
            _sz = BaseSize;
            _w = 6 * _sz;
            _h = 2 * _sz;
            _bezel = Math.Max(6, Math.Round(_sz * 0.23)); // bord du “carving” (équiv. box-shadow/arrondi)

            Width = _w; Height = _h + (ShowLabels ? 24 : 0);

            // tailles switch
            SwitchHost.Height = _h;
            Bezel.Width = _w; Bezel.Height = _h; Bezel.CornerRadius = new CornerRadius(_h / 2.0);

            var m = 0.23 * _sz; // marge interne approx.
            Cavity.Margin = new Thickness(m);
            Cavity.Width = _w - 2 * m;
            Cavity.Height = _h - 2 * m;
            Cavity.CornerRadius = new CornerRadius(Cavity.Height / 2.0);

            TeethLayer.Margin = new Thickness(m);
            TeethLayer.Width = Cavity.Width; TeethLayer.Height = Cavity.Height;

            _innerW = Cavity.Width; _innerH = Cavity.Height;

            // thumb = hauteur interne - un petit delta
            _thumb = _innerH - (_sz * 0.2);
            _slide = _innerW - _thumb;
            _startX = m + (_sz * 0.1);

            Thumb.Width = _thumb; Thumb.Height = _thumb;
            ThumbDisk.Width = _thumb; ThumbDisk.Height = _thumb;
            ThumbGlow.Width = _thumb; ThumbGlow.Height = _thumb;

            // stries : 3 barres
            var g = Grip;
            g.Width = _thumb * 0.42; g.Height = _thumb * 0.55;
            g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Clear();
            for (int i = 0; i < 3; i++)
                g.Children.Add(new Rectangle
                {
                    Width = _thumb * 0.08,
                    Height = _thumb * 0.55,
                    Fill = (Brush)new BrushConverter().ConvertFromString("#2c3133"),
                    RadiusX = _thumb * 0.06,
                    RadiusY = _thumb * 0.06,
                    Margin = new Thickness(i == 0 ? 0 : _thumb * 0.07, 0, 0, 0),
                    Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Color = Color.FromArgb(48, 0, 0, 0) }
                });

            // dents (10 en haut + 10 en bas, largeur 120% comme CSS)
            BuildTeeth(TopTeeth, true);
            BuildTeeth(BottomTeeth, false);

            // conteneurs dents : hauteur ~sz/2.25 et rotation initiale
            TopTeethContainer.Height = _sz / 2.25; BottomTeethContainer.Height = _sz / 2.25;
            TopRotate.Angle = 5; BotRotate.Angle = -5;

            // positions initiales
            ThumbX.X = _startX;
            SetThumbHalo(OffColor);

            // voyants
            Lights.Margin = new Thickness(0, 0, 0, 0);
        }

        void BuildTeeth(UniformGrid grid, bool top)
        {
            grid.Columns = TeethCount; grid.Rows = 1;
            grid.Children.Clear();

            for (int i = 0; i < TeethCount; i++)
            {
                var r = new Rectangle();
                double w = _sz / 1.6;
                double h = _sz / 2.0;
                r.Width = w; r.Height = h;
                if (top)
                {
                    r.RadiusX = r.RadiusY = _sz / 8.0;  // bords inférieurs arrondis
                }
                else
                {
                    r.RadiusX = r.RadiusY = _sz / 8.0;
                }

                // remplissage: léger bevel comme CSS
                var lg = new LinearGradientBrush();
                if (top) { lg.StartPoint = new Point(0, 1); lg.EndPoint = new Point(0, 0); }
                else { lg.StartPoint = new Point(0, 0); lg.EndPoint = new Point(0, 1); }
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(255, 230, 230, 230), 0.00));
                lg.GradientStops.Add(new GradientStop(Color.FromArgb(255, 190, 190, 190), 1.00));
                r.Fill = lg;
                r.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 1.2, Color = Colors.White, Opacity = 0.35 };

                grid.Children.Add(r);
            }
        }

        void PlayIntro()
        {
            var dur = TimeSpan.FromMilliseconds(Math.Max(TransitionMs * 0.9, 260));
            ThumbX.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_startX + _slide, _startX, dur) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        }

        void AnimateState(bool on, bool instant = false)
        {
            var dur = TimeSpan.FromMilliseconds(instant ? 0 : TransitionMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            // molette
            var toX = on ? (_startX + _slide) : _startX;
            ThumbX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toX, dur) { EasingFunction = ease });
            // halo du thumb (rouge -> gris -> vert)
            var seq = new ColorAnimationUsingKeyFrames { Duration = dur };
            seq.KeyFrames.Add(new DiscreteColorKeyFrame(on ? GreyStep : GreyStep, KeyTime.FromPercent(0.50)));
            seq.KeyFrames.Add(new DiscreteColorKeyFrame(on ? OnColor : OffColor, KeyTime.FromPercent(1.00)));
            ThumbColorStop.BeginAnimation(GradientStop.ColorProperty, seq);

            // voyants
            GlowOn.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(on ? 10 : 0, dur));
            GlowOn.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(on ? 0.85 : 0.0, dur));
            GlowOff.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(on ? 0 : 10, dur));
            GlowOff.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(on ? 0.0 : 0.85, dur));

            // dents : rotation + petite translation pour simuler le pivot qui “glisse” (22% -> 78%)
            double shift = _innerW * 0.10; // approximation du changement d’origine
            TopRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(on ? -5 : 5, dur) { EasingFunction = ease });
            BotRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(on ? 5 : -5, dur) { EasingFunction = ease });
            TopShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? shift : -shift, dur) { EasingFunction = ease });
            BotShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? -shift : shift, dur) { EasingFunction = ease });
        }

        void SetThumbHalo(Color c) => ThumbColorStop.Color = c;
    }
}
