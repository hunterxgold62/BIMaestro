using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Couleur
{
    public static class BrowserAtmosphere
    {
        public static bool IsSupported(string mode) => mode == "Verre dépoli" || mode == "Plan d'architecte" || mode == "Encre dans l'eau";

        public static string Css(ProjectBrowserColorSettings settings)
        {
            string alpha = (settings.BackgroundIntensity / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
            return @"
@keyframes bimaestroAtmosphereDrift {
  0%,100% { background-position:0% 0%,100% 100%,0 0; }
  50% { background-position:15% 12%,85% 80%,0 0; }
}
@keyframes bimaestroBlueprintDrift {
  from { background-position:0 0,0 0,0 0,0 0; }
  to { background-position:80px 80px,80px 80px,80px 80px,80px 80px; }
}
[data-bimaestro-bubble-surface='frosted'] {
  background-image:radial-gradient(ellipse at 0% 5%,rgba(108,190,236,calc(__ALPHA__ * .46)),transparent 64%),
    radial-gradient(ellipse at 100% 95%,rgba(192,142,233,calc(__ALPHA__ * .38)),transparent 65%),
    linear-gradient(125deg,rgba(255,255,255,calc(__ALPHA__ * .35)),transparent 70%) !important;
  background-size:120% 120%,120% 120%,100% 100%;
  background-repeat:no-repeat;
  animation:bimaestroAtmosphereDrift 36s ease-in-out infinite;
  animation-play-state:__PLAY__ !important;
}
[data-bimaestro-bubble-surface='blueprint'] {
  background-image:linear-gradient(rgba(128,195,242,calc(__ALPHA__ * .27)) 1px,transparent 1px),
    linear-gradient(90deg,rgba(128,195,242,calc(__ALPHA__ * .27)) 1px,transparent 1px),
    linear-gradient(rgba(128,195,242,calc(__ALPHA__ * .10)) 1px,transparent 1px),
    linear-gradient(90deg,rgba(128,195,242,calc(__ALPHA__ * .10)) 1px,transparent 1px) !important;
  background-size:80px 80px,80px 80px,16px 16px,16px 16px;
  animation:bimaestroBlueprintDrift 80s linear infinite;
  animation-play-state:__PLAY__ !important;
}
[data-bimaestro-bubble-surface='ink'] {
  background-image:radial-gradient(ellipse at -8% 20%,rgba(107,77,200,calc(__ALPHA__ * .52)) 0%,rgba(148,113,221,calc(__ALPHA__ * .25)) 20%,transparent 48%),
    radial-gradient(ellipse at 108% 78%,rgba(17,162,173,calc(__ALPHA__ * .48)) 0%,rgba(78,193,193,calc(__ALPHA__ * .22)) 22%,transparent 52%),
    radial-gradient(ellipse at 0% 100%,rgba(221,122,177,calc(__ALPHA__ * .25)),transparent 48%) !important;
  background-size:125% 130%,125% 130%,100% 100%;
  background-repeat:no-repeat;
  animation:bimaestroAtmosphereDrift 28s ease-in-out infinite;
  animation-play-state:__PLAY__ !important;
}
@media (prefers-reduced-motion:reduce) {
  [data-bimaestro-bubble-surface='frosted'],[data-bimaestro-bubble-surface='blueprint'],[data-bimaestro-bubble-surface='ink'] { animation:none !important; }
}
".Replace("__ALPHA__", alpha).Replace("__PLAY__", settings.BackgroundAnimated ? "running" : "paused");
        }

        // Static WPF approximation of the browser gradients; the browser owns the animation.
        public static Brush Preview(ProjectBrowserColorSettings settings)
        {
            if (settings == null || !IsSupported(settings.BackgroundMode)) return Brushes.Transparent;
            var group = new DrawingGroup();
            double intensity = settings.BackgroundIntensity / 100;
            using (var dc = group.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 320, 220));
                if (settings.BackgroundMode == "Plan d'architecte")
                {
                    for (int x = 0; x <= 320; x += 16)
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * intensity * (x % 80 == 0 ? .27 : .10)), 128, 195, 242)), 1), new Point(x, 0), new Point(x, 220));
                    for (int y = 0; y <= 220; y += 16)
                        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * intensity * (y % 80 == 0 ? .27 : .10)), 128, 195, 242)), 1), new Point(0, y), new Point(320, y));
                }
                else
                {
                    bool ink = settings.BackgroundMode == "Encre dans l'eau";
                    var colors = ink ? new[] { Color.FromRgb(107,77,200), Color.FromRgb(17,162,173) }
                        : new[] { Color.FromRgb(108,190,236), Color.FromRgb(192,142,233) };
                    for (int i = 0; i < 2; i++)
                    {
                        var color = colors[i];
                        color.A = (byte)(255 * intensity * (ink ? .5 : .42));
                        var gradient = new RadialGradientBrush(color, Colors.Transparent)
                        {
                            Center = new Point(i == 0 ? 0 : 1, i == 0 ? .15 : .85),
                            GradientOrigin = new Point(i == 0 ? 0 : 1, i == 0 ? .15 : .85),
                            RadiusX = ink ? .55 : .85, RadiusY = .95
                        };
                        dc.DrawRectangle(gradient, null, new Rect(0, 0, 320, 220));
                    }
                }
            }
            var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
    }
}
