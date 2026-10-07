using Analyse;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BIMaestro.Localization { internal static class UiLanguage { public static string T(string text) => text; } }

internal static class VisualProbe
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string folder = args.Length > 0 ? args[0] : "tmp/clash-visual-probe"; Directory.CreateDirectory(folder);
            var source = Box(-1, -.25, -.25, 1, .25, .25);
            var obstacle = Box(-.2, -.5, -.5, .2, .5, .5);
            var scene = new SmartVisualScene(source, obstacle, new SmartVisualPoint(0, 0, 0), 50, false, null);
            var image = Task.Run(() => scene.Thumbnail).GetAwaiter().GetResult();
            Check(image.IsFrozen && ReferenceEquals(image, scene.Thumbnail), "Frozen thumbnail cache failed.");
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, 160, 110));
            var bitmap = new RenderTargetBitmap(160, 110, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var pixels = new byte[160 * 110 * 4]; bitmap.CopyPixels(pixels, 160 * 4, 0);
            int orange = 0, blue = 0; int left = 160, right = 0, top = 110, bottom = 0;
            for (int y = 0; y < 110; y++) for (int x = 0; x < 160; x++)
            {
                int i = (y * 160 + x) * 4; int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                bool a = r > 150 && g < 140 && b < 90, c = b > 110 && g >= 70 && g < 160 && r < 90;
                if (a) orange++; if (c) blue++;
                if (a || c) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            }
            Check(orange > 300 && blue > 300 && orange + blue > 2000, "Objects too small or colours missing.");
            Check(right - left > 100 && bottom - top > 65, "Adaptive thumbnail frame does not fill the image.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, "fitted-thumbnail.png"))) encoder.Save(stream);
            string svg = Task.Run(() => SmartVisualProjection.Svg(scene)).GetAwaiter().GetResult();
            var document = XDocument.Parse(svg); XNamespace ns = "http://www.w3.org/2000/svg";
            var coordinates = document.Descendants(ns + "polygon").SelectMany(p => p.Attribute("points").Value.Split(' '))
                .Select(p => p.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            Check(coordinates.Length > 0 && coordinates.All(p => p[0] >= 9.9 && p[0] <= 150.1 && p[1] >= 9.9 && p[1] <= 100.1), "SVG frame overflows.");
            File.WriteAllText(Path.Combine(folder, "fitted-thumbnail.svg"), svg);
            var viewer = new SmartClashViewer(new SmartVisualScene(source, obstacle, scene.Center, 1.5, false, null));
            var before = viewer.Camera.Position; viewer.Orbit(25, 10); viewer.Zoom(120);
            Check(viewer.VertexCount > 0 && before != viewer.Camera.Position, "Independent viewer controls failed.");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith("Revit", StringComparison.OrdinalIgnoreCase)), "Renderer loaded a Revit assembly.");
            Console.WriteLine("PASS: frozen background cache, adaptive pixel coverage, valid SVG frame, independent 3D interaction, no Revit assembly.");
            Console.WriteLine("Coloured pixels: " + (orange + blue) + "; bounds: " + (right - left) + " x " + (bottom - top)); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static SmartVisualMesh Box(double x, double y, double z, double a, double b, double c)
    {
        var p = new[] { new SmartVisualPoint(x,y,z), new SmartVisualPoint(a,y,z), new SmartVisualPoint(a,b,z), new SmartVisualPoint(x,b,z),
            new SmartVisualPoint(x,y,c), new SmartVisualPoint(a,y,c), new SmartVisualPoint(a,b,c), new SmartVisualPoint(x,b,c) };
        var indices = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        var triangles = new List<SmartVisualTriangle>(); for (int i=0;i<indices.Length;i+=3) triangles.Add(new SmartVisualTriangle(p[indices[i]],p[indices[i+1]],p[indices[i+2]]));
        return new SmartVisualMesh(triangles);
    }
}
