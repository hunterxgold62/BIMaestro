using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

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

        // ========== API réutilisable ==========

        // Échelle globale (équiv. --sz). Piste = 4*sz x 2*sz ; Pouce = 2*sz
        public static readonly DependencyProperty BaseSizeProperty =
            DependencyProperty.Register(nameof(BaseSize), typeof(double), typeof(EyeToggleSwitch),
                new PropertyMetadata(28d, OnLayoutDpChanged));
        public double BaseSize { get => (double)GetValue(BaseSizeProperty); set => SetValue(BaseSizeProperty, value); }

        // État ON/OFF (bindable)
        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(EyeToggleSwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));
        public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

        // Durée transitions (ms)
        public static readonly DependencyProperty TransitionMsProperty =
            DependencyProperty.Register(nameof(TransitionMs), typeof(int), typeof(EyeToggleSwitch),
                new PropertyMetadata(500));
        public int TransitionMs { get => (int)GetValue(TransitionMsProperty); set => SetValue(TransitionMsProperty, value); }

        // Couleurs thème (fidèles au CSS)
        public Color ColorOn1 { get; set; } = (Color)ColorConverter.ConvertFromString("#fed501");
        public Color ColorOn2 { get; set; } = (Color)ColorConverter.ConvertFromString("#e4bf00");
        public Color ColorOff1 { get; set; } = (Color)ColorConverter.ConvertFromString("#224056");
        public Color ColorOff2 { get; set; } = (Color)ColorConverter.ConvertFromString("#172c3c");
        public Color ColorW3 { get; set; } = (Color)ColorConverter.ConvertFromString("#ccd2d5");

        // Événement comme une CheckBox
        public static readonly RoutedEvent ToggledEvent =
            EventManager.RegisterRoutedEvent("Toggled", RoutingStrategy.Bubble,
                typeof(RoutedPropertyChangedEventHandler<bool>), typeof(EyeToggleSwitch));
        public event RoutedPropertyChangedEventHandler<bool> Toggled
        {
            add => AddHandler(ToggledEvent, value);
            remove => RemoveHandler(ToggledEvent, value);
        }

        // ========== internals ==========
        double _sz, _trackW, _trackH, _thumb, _slide, _ringThick;
        SolidColorBrush _trackBrush = new();
        SolidColorBrush _ringBrush = new();
        SolidColorBrush _offBrush = new();
        SolidColorBrush _onBrush = new();

        static void OnLayoutDpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (EyeToggleSwitch)d;
            if (c.IsLoaded) c.ApplyLayout();
        }

        static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (EyeToggleSwitch)d;
            if (!c.IsLoaded) return;
            c.AnimateState((bool)e.NewValue);
            var args = new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue) { RoutedEvent = ToggledEvent };
            c.RaiseEvent(args);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyLayout();
            PlayIntro();                 // “go-back” au chargement
            AnimateState(IsOn, true);    // appliquer l’état courant
        }

        void ApplyLayout()
        {
            _sz = BaseSize;
            _trackW = 4 * _sz;
            _trackH = 2 * _sz;
            _thumb = 2 * _sz;
            _slide = _trackW - _thumb;
            _ringThick = _sz / 7.0;

            // Taille hôte + positions labels (≈ CSS: left:-2*sz / +4.65*sz)
            Host.Width = _trackW + 2 * _sz * 2.2;
            Host.Height = Math.Max(_trackH, _thumb);

            OffText.FontSize = 0.7 * _sz;
            OffText.Margin = new Thickness(-2.0 * _sz, 0, 0, 0);
            OffText.HorizontalAlignment = HorizontalAlignment.Left;

            OnText.FontSize = 0.7 * _sz;
            OnText.Margin = new Thickness(_trackW + 0.65 * _sz, 0, 0, 0);
            OnText.HorizontalAlignment = HorizontalAlignment.Left;

            // Piste + anneau
            SwitchCanvas.Width = _trackW; SwitchCanvas.Height = _trackH;

            Track.Width = _trackW; Track.Height = _trackH;
            Track.CornerRadius = new CornerRadius(_trackH / 2.0);

            TrackRing.Width = _trackW; TrackRing.Height = _trackH;
            TrackRing.CornerRadius = new CornerRadius(_trackH / 2.0);
            TrackRing.BorderThickness = new Thickness(_ringThick);

            _trackBrush.Color = ColorOff1; Track.Background = _trackBrush;
            _ringBrush.Color = ColorOff2; TrackRing.BorderBrush = _ringBrush;

            _offBrush.Color = ColorOff2; OffText.Foreground = _offBrush;
            _onBrush.Color = ColorW3; OnText.Foreground = _onBrush;

            // Pouce / œil
            Thumb.Width = _thumb; Thumb.Height = _thumb;

            // œil bien gros + liseré gris
            EyeWhite.Width = _thumb * 0.92;
            EyeWhite.Height = _thumb * 0.92;
            EyeWhite.StrokeThickness = Math.Max(2.0, _sz * 0.18);

            // Pupil taille
            PupilBox.Width = PupilBox.Height = Math.Max(8.0, _sz * 0.65);

            // Clip de la paupière = même cercle que l’œil
            Eyelid.Width = EyeWhite.Width; Eyelid.Height = EyeWhite.Height;
            EyelidClip.Center = new Point(Eyelid.Width / 2.0, Eyelid.Height / 2.0);
            EyelidClip.RadiusX = Eyelid.Width / 2.0;
            EyelidClip.RadiusY = Eyelid.Height / 2.0;

            // Taille globale du contrôle
            Width = Host.Width;
            Height = Host.Height;
        }

        void PlayIntro()
        {
            var dur = TimeSpan.FromMilliseconds(Math.Max(TransitionMs * 0.9, 250));
            ThumbX.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_slide, 0, dur) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });

            // œil vers OFF, paupière qui monte → descend (comme le CSS)
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            PupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_sz * 0.10, _sz * 0.00, dur) { EasingFunction = ease });
            PupilOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-_sz * 0.10, _sz * 0.00, dur) { EasingFunction = ease });
            EyelidShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_sz * 0.45, _sz * 0.10, dur) { EasingFunction = ease });
        }

        void AnimateState(bool on, bool instant = false)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var dur = TimeSpan.FromMilliseconds(instant ? 0 : TransitionMs);

            // 1) déplacement du pouce
            ThumbX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            { To = on ? _slide : 0, Duration = dur, EasingFunction = ease });

            // 2) couleurs piste + anneau (OFF -> ON)
            _trackBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            { To = on ? ColorOn1 : ColorOff1, Duration = dur });
            _ringBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            { To = on ? ColorOn2 : ColorOff2, Duration = dur });

            // 3) libellés & halo ON
            _offBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            { To = on ? ColorW3 : ColorOff2, Duration = dur });
            _onBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            { To = on ? ColorOn2 : ColorW3, Duration = dur });

            // halo ON (glow)
            var glowTo = on ? 10.0 : 0.0;           // BlurRadius
            var glowOp = on ? 0.90 : 0.0;           // Opacité
            OnGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(glowTo, dur));
            OnGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(glowOp, dur));

            // 4) pupille (direction)
            // OFF : sceptique, en bas-gauche (yeux mi-clos)
            // ON  : joyeux, en haut-gauche (regarde le label ON)
            double px = on ? -_sz * 0.12 : -_sz * 0.08;
            double py = on ? -_sz * 0.12 : _sz * 0.18;
            PupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(px, dur) { EasingFunction = ease });
            PupilOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(py, dur) { EasingFunction = ease });

            // 5) paupière : inclinée -12°, glisse vers le haut quand ON (œil grand ouvert)
            double eyelidY = on ? -_sz * 0.90 : _sz * 0.10; // hors du cercle quand ON
            EyelidShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(eyelidY, dur) { EasingFunction = ease });
        }
    }
}
