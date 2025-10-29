using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BIMaestro.UI
{
    public partial class EyeToggleSwitch : UserControl
    {
        public EyeToggleSwitch()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => ApplyLayout();
        }

        // -------- API ----------
        public static readonly DependencyProperty BaseSizeProperty =
            DependencyProperty.Register(nameof(BaseSize), typeof(double), typeof(EyeToggleSwitch),
                new PropertyMetadata(28d, OnLayoutChanged));
        public double BaseSize { get => (double)GetValue(BaseSizeProperty); set => SetValue(BaseSizeProperty, value); }

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(EyeToggleSwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));
        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

        public static readonly DependencyProperty TransitionMsProperty =
            DependencyProperty.Register(nameof(TransitionMs), typeof(int), typeof(EyeToggleSwitch),
                new PropertyMetadata(420));
        public int TransitionMs { get => (int)GetValue(TransitionMsProperty); set => SetValue(TransitionMsProperty, value); }

        // réglages anneau et liseré (plus fins par défaut)
        public static readonly DependencyProperty RingThicknessRatioProperty =
            DependencyProperty.Register(nameof(RingThicknessRatio), typeof(double), typeof(EyeToggleSwitch),
                new PropertyMetadata(0.10, OnLayoutChanged));
        public double RingThicknessRatio { get => (double)GetValue(RingThicknessRatioProperty); set => SetValue(RingThicknessRatioProperty, value); }

        public static readonly DependencyProperty EyeRimRatioProperty =
            DependencyProperty.Register(nameof(EyeRimRatio), typeof(double), typeof(EyeToggleSwitch),
                new PropertyMetadata(0.06, OnLayoutChanged));
        public double EyeRimRatio { get => (double)GetValue(EyeRimRatioProperty); set => SetValue(EyeRimRatioProperty, value); }

        // couverture de paupière OFF : hauteur à gauche/droite (0..1 du diamètre)
        public static readonly DependencyProperty EyelidLeftCoverProperty =
            DependencyProperty.Register(nameof(EyelidLeftCover), typeof(double), typeof(EyeToggleSwitch),
                new PropertyMetadata(0.56, OnLayoutChanged));
        public double EyelidLeftCover { get => (double)GetValue(EyelidLeftCoverProperty); set => SetValue(EyelidLeftCoverProperty, value); }

        public static readonly DependencyProperty EyelidRightCoverProperty =
            DependencyProperty.Register(nameof(EyelidRightCover), typeof(double), typeof(EyeToggleSwitch),
                 new PropertyMetadata(0.46, OnLayoutChanged));
        public double EyelidRightCover { get => (double)GetValue(EyelidRightCoverProperty); set => SetValue(EyelidRightCoverProperty, value); }

        // couleurs
        public Color TrackOff { get; set; } = (Color)ColorConverter.ConvertFromString("#224056");
        public Color RingOff { get; set; } = (Color)ColorConverter.ConvertFromString("#172C3C");
        public Color TrackOn { get; set; } = (Color)ColorConverter.ConvertFromString("#FED501");
        public Color RingOn { get; set; } = (Color)ColorConverter.ConvertFromString("#E4BF00");

        // event "Toggled"
        public static readonly RoutedEvent ToggledEvent =
            EventManager.RegisterRoutedEvent("Toggled", RoutingStrategy.Bubble,
                typeof(RoutedPropertyChangedEventHandler<bool>), typeof(EyeToggleSwitch));
        public event RoutedPropertyChangedEventHandler<bool> Toggled
        {
            add => AddHandler(ToggledEvent, value);
            remove => RemoveHandler(ToggledEvent, value);
        }

        // -------- internals ----------
        double _sz, _trackW, _trackH, _ringThick, _innerW, _innerH, _thumb, _slide, _startX, _eyelidRest;
        readonly SolidColorBrush _trackBrush = new();
        readonly SolidColorBrush _ringBrush = new();

        static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (EyeToggleSwitch)d;
            if (c.IsLoaded) c.ApplyLayout();
        }

        static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (EyeToggleSwitch)d;
            if (!c.IsLoaded) return;
            c.AnimateState((bool)e.NewValue);
            var args = new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue)
            { RoutedEvent = ToggledEvent };
            c.RaiseEvent(args);
        }

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            ApplyLayout();
            PlayIntro();
            AnimateState(IsOn, instant: true);
        }

        void ApplyLayout()
        {
            _sz = BaseSize;
            _trackW = 4 * _sz;
            _trackH = 2 * _sz;
            _ringThick = Math.Max(2.0, Math.Round(_trackH * RingThicknessRatio));

            _innerW = _trackW - 2 * _ringThick;
            _innerH = _trackH - 2 * _ringThick;

            _thumb = _innerH;
            _slide = _innerW - _thumb;
            _startX = _ringThick;

            // Track
            TrackHost.Width = _trackW; TrackHost.Height = _trackH;
            TrackRing.Width = _trackW; TrackRing.Height = _trackH;
            TrackRing.CornerRadius = new CornerRadius(_trackH / 2.0);
            TrackRing.BorderThickness = new Thickness(_ringThick);

            TrackBody.Margin = new Thickness(_ringThick);
            Track.Width = _innerW; Track.Height = _innerH; Track.CornerRadius = new CornerRadius(_innerH / 2.0);
            TrackBevel.Width = _innerW; TrackBevel.Height = _innerH; TrackBevel.CornerRadius = new CornerRadius(_innerH / 2.0);

            // Couleurs
            _trackBrush.Color = TrackOff; Track.Background = _trackBrush;
            _ringBrush.Color = RingOff; TrackRing.BorderBrush = _ringBrush;
            RingGlow.BlurRadius = 0; RingGlow.Opacity = 0;

            // Thumb / Eye
            Thumb.Width = _thumb; Thumb.Height = _thumb;

            EyeWhite.Width = _thumb * 0.95;
            EyeWhite.Height = _thumb * 0.95;
            EyeWhite.StrokeThickness = Math.Max(1.0, Math.Round(_thumb * EyeRimRatio));

            // Pupille + reflet (échelle fiable petites tailles)
            var pupil = Math.Max(6.0, Math.Round(_thumb * 0.42));
            Pupil.Width = Pupil.Height = pupil;
            PupilShape.Width = PupilShape.Height = pupil;

            var spec = Math.Max(2.0, Math.Round(pupil * 0.26));
            Spec.Width = spec; Spec.Height = spec;
            Spec.Margin = new Thickness(Math.Round(pupil * 0.18), Math.Round(pupil * 0.18), 0, 0);

            Eyelid.Width = EyeWhite.Width; Eyelid.Height = EyeWhite.Height;
            var eyelidCenter = new Point(Eyelid.Width / 2.0, Eyelid.Height / 2.0);
            EyelidClip.Center = eyelidCenter;
            EyelidClip.RadiusX = Eyelid.Width / 2.0;
            EyelidClip.RadiusY = Eyelid.Height / 2.0;

            RebuildEyelidGeometry();

            _eyelidRest = _thumb * 0.10;
            EyelidShift.X = -_thumb * 0.02;
            EyelidShift.Y = _eyelidRest;

            var tilt = (EyelidLeftCover - EyelidRightCover) * 65.0;
            if (tilt < -16.0) tilt = -16.0;
            if (tilt > 16.0) tilt = 16.0;
            EyelidRot.Angle = -tilt;

            // Taille globale
            Width = _trackW; Height = _trackH;

            // OFF par défaut : pupille visible
            ThumbX.X = _startX;
            PupilOffset.X = -_thumb * 0.12;
            PupilOffset.Y = _thumb * 0.10;
            EyelidShift.Y = _eyelidRest;
        }

        void RebuildEyelidGeometry()
        {
            var w = Eyelid.Width; var h = Eyelid.Height;
            if (w <= 0 || h <= 0) return;

            var leftCover = Math.Max(0, Math.Min(1, EyelidLeftCover));
            var rightCover = Math.Max(0, Math.Min(1, EyelidRightCover));

            var leftY = leftCover * h;
            var rightY = rightCover * h;
            var margin = Math.Max(w, h);

            const double arcRatio = 0.28;
            var control = new Point(w * 0.5, Math.Min(leftY, rightY) - (h * arcRatio));

            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(-margin, -margin), true, true);
                gc.LineTo(new Point(w + margin, -margin), true, false);
                gc.LineTo(new Point(w + margin, rightY), true, false);
                gc.QuadraticBezierTo(control, new Point(-margin, leftY), true, false);
            }
            geo.Freeze();
            Eyelid.Data = geo;
        }

        void PlayIntro()
        {
            var dur = TimeSpan.FromMilliseconds(Math.Max(TransitionMs * 0.9, 220));
            ThumbX.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_startX + _slide, _startX, dur)
                { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            PupilOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_thumb * 0.06, -_thumb * 0.12, dur) { EasingFunction = ease });
            PupilOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-_thumb * 0.06, _thumb * 0.10, dur) { EasingFunction = ease });

            EyelidShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation
                {
                    From = _eyelidRest - _thumb * 0.35,
                    To = _eyelidRest,
                    Duration = dur,
                    EasingFunction = ease
                });

            RebuildEyelidGeometry();
        }

        void AnimateState(bool on, bool instant = false)
        {
            var dur = TimeSpan.FromMilliseconds(instant ? 0 : TransitionMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            // glisse de l’œil
            var x = on ? (_startX + _slide) : _startX;
            ThumbX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation { To = x, Duration = dur, EasingFunction = ease });

            // couleurs + halo
            _trackBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? TrackOn : TrackOff, dur));
            _ringBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? RingOn : RingOff, dur));
            RingGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(on ? 8.0 : 0.0, dur));
            RingGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(on ? 0.55 : 0.0, dur));

            // pupille
            if (on)
            {
                // œil grand ouvert, pupille haut-gauche
                PupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_thumb * 0.20, dur) { EasingFunction = ease });
                PupilOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-_thumb * 0.20, dur) { EasingFunction = ease });

                var target = -_thumb * 1.05;
                if (instant) EyelidShift.Y = target;
                EyelidShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation { To = target, Duration = dur, EasingFunction = ease });
            }
            else
            {
                // OFF : mi-clos, pupille visible
                PupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_thumb * 0.12, dur) { EasingFunction = ease });
                PupilOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_thumb * 0.10, dur) { EasingFunction = ease });

                if (instant) EyelidShift.Y = _eyelidRest;
                EyelidShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation { To = _eyelidRest, Duration = dur, EasingFunction = ease });
            }
        }
    }
}
