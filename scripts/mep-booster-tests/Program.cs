using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BIMaestro.MepBooster;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string name = new AssemblyName(request.Name).Name;
            if (name != "RevitAPI" && name != "RevitAPIUI") return null;
            return Assembly.LoadFrom(Path.Combine(@"C:\Program Files\Autodesk\Revit 2023", name + ".dll"));
        };
        try { Run(args.Length > 0 ? args[0] : "rosace.png", args.Contains("--window-lifecycle")); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string output, bool windowLifecycle)
    {
        TestKinematics();
        foreach (int iconSize in new[] { 16, 32 })
        {
            var greenIcon = MepBoosterService.StateIcon(true, iconSize);
            var redIcon = MepBoosterService.StateIcon(false, iconSize);
            Assert(greenIcon.PixelWidth == iconSize && redIcon.PixelWidth == iconSize, "Icônes ON/OFF disponibles à la taille du ruban");
            Assert(greenIcon.IsFrozen && redIcon.IsFrozen && ReferenceEquals(redIcon, MepBoosterService.StateIcon(false, iconSize)),
                "Icônes mises en cache sans rechargement à chaque bascule");
            Assert(!ReferenceEquals(greenIcon, redIcon), "ON et OFF utilisent des images distinctes");
        }
        string settingsTest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "angle-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Assert(BoosterPreferences.LoadAngle(settingsTest) == 22.5, "Angle par défaut sans préférence enregistrée");
            BoosterPreferences.SaveAngle(settingsTest, -37.25);
            Assert(BoosterPreferences.LoadAngle(settingsTest) == -37.25, "Angle personnalisé signé et décimal conservé sur disque");
            File.WriteAllText(settingsTest, "invalide");
            Assert(BoosterPreferences.LoadAngle(settingsTest) == 22.5, "Préférence invalide : repli sans bloquer la rosace");
        }
        finally { if (File.Exists(settingsTest)) File.Delete(settingsTest); }
        var axisX = new Vector3D(1, 0, 0);
        Assert(Math.Abs(BoosterOrientationMath.Roll(axisX, new Vector3D(0, 0, 1))) < 1e-8, "Copie : position haute = zéro autour d’un tuyau horizontal");
        Assert(Math.Abs(BoosterOrientationMath.Roll(axisX, new Vector3D(0, -1, 0)) - 90) < 1e-8, "Copie : rotation signée de 90 degrés");
        Assert(Math.Abs(BoosterOrientationMath.Roll(new Vector3D(0, 0, 1), new Vector3D(0, 1, 0))) < 1e-8, "Copie : axe Y de repli sur tuyau vertical");
        Assert(BoosterOrientationMath.Delta(-170, 170) == 20 && BoosterOrientationMath.Delta(170, -170) == -20,
            "Copie : trajet angulaire le plus court dans les deux sens");
        foreach (var pipeAxis in new[] { new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(1, 2, 3), new Vector3D(0, 0, 1) })
        {
            var unit = pipeAxis; unit.Normalize();
            var up = Math.Abs(unit.Z) > 0.99 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
            up -= unit * Vector3D.DotProduct(up, unit); up.Normalize();
            var rotation = Matrix3D.Identity; rotation.Rotate(new Quaternion(unit, 37));
            Assert(Math.Abs(BoosterOrientationMath.Roll(unit, rotation.Transform(up)) - 37) < 1e-8,
                "Copie : même orientation relative sur axes non parallèles et obliques");
        }
        var clock = DateTime.UtcNow;
        DateTime next = DateTime.MinValue;
        int polls = 0;
        for (int i = 0; i < 10000; i++)
        {
            var time = clock.AddMilliseconds(i);
            if (MepBoosterService.ShouldInspect(false, false, true, time, next)) { polls++; next = time.AddMilliseconds(150); }
        }
        Assert(polls == 67, "10 000 Idling : seulement 67 inspections quand la rosace est visible");
        Assert(!MepBoosterService.ShouldInspect(false, false, false, clock, DateTime.MinValue), "Aucune inspection quand la sélection est inchangée et la rosace masquée");
        Assert(MepBoosterService.ShouldInspect(true, false, false, clock, clock.AddHours(1)), "Une nouvelle sélection est traitée immédiatement");
        var originalDocument = new DocumentWrapper(1);
        var refreshedDocument = new DocumentWrapper(1);
        Assert(!ReferenceEquals(originalDocument, refreshedDocument), "Deux wrappers distincts pour le même document");
        for (int callback = 0; callback < 100; callback++)
            AssertStableDocument(originalDocument, new DocumentWrapper(1));
        Assert(!MepBoosterService.SelectionContextChanged(originalDocument, refreshedDocument, 10, 10, "1,2", "1,2", false),
            "100 callbacks pour le même document ne réinitialisent pas la stabilisation");
        Assert(MepBoosterService.SelectionContextChanged(originalDocument, new DocumentWrapper(2), 10, 10, "1,2", "1,2", false), "Un autre document réinitialise le contexte");
        Assert(MepBoosterService.SelectionContextChanged(originalDocument, refreshedDocument, 10, 11, "1,2", "1,2", false), "Un changement de vue réinitialise le contexte");
        Assert(MepBoosterService.SelectionContextChanged(originalDocument, refreshedDocument, 10, 10, "1", "1,2", false), "Une nouvelle sélection relance le délai");
        Assert(MepBoosterService.SelectionContextChanged(originalDocument, refreshedDocument, 10, 10, "1,2", "1,2", true), "Une invalidation explicite est respectée");
        var service = new MepBoosterService { DiagnosticSink = _ => { } };
        var serviceType = typeof(MepBoosterService);
        var suppressed = serviceType.GetField("_suppressed", Hidden);
        var suspend = serviceType.GetMethod("Suspend", Hidden);
        suspend.Invoke(service, null);
        Assert(!(bool)suppressed.GetValue(service), "Une indisponibilité temporaire ne bloque pas la sélection courante");
        serviceType.GetMethod("Dismiss", Hidden).Invoke(service, null);
        Assert((bool)suppressed.GetValue(service), "Une fermeture volontaire empêche la réapparition");
        suspend.Invoke(service, null);
        Assert((bool)suppressed.GetValue(service), "Le retour du focus respecte une fermeture volontaire");
        var palette = new BoosterPalette(IntPtr.Zero);
        var pillSamples = new StackPanel { Background = (Brush)palette.FindResource("App.Background"), Width = 204 };
        pillSamples.Resources.MergedDictionaries.Add(palette.Resources);
        foreach (int count in new[] { 1, 24, 150, 0 })
        {
            var sample = palette.CreatePill(count, count == 0 ? "Sélection incompatible" : null);
            sample.Margin = new Thickness(20, 8, 20, 8);
            pillSamples.Children.Add(sample);
            Assert(sample.Height >= 40 && !sample.Focusable, "Pastille : cible confortable sans prise de focus");
            Assert(((StackPanel)sample.Content).Children.Count == 3, "Pastille : icône, libellé et compteur distincts");
        }
        pillSamples.Measure(new Size(204, 240)); pillSamples.Arrange(new Rect(0, 0, 204, 240)); pillSamples.UpdateLayout();
        foreach (Button sample in pillSamples.Children)
            Assert(((StackPanel)sample.Content).ActualWidth <= sample.ActualWidth, "Contenu de pastille sans débordement");
        var pillBitmap = new RenderTargetBitmap(408, 480, 192, 192, PixelFormats.Pbgra32);
        pillBitmap.Render(pillSamples);
        var pillEncoder = new PngBitmapEncoder(); pillEncoder.Frames.Add(BitmapFrame.Create(pillBitmap));
        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "mep-booster-pastilles.png"))) pillEncoder.Save(stream);
        var type = typeof(BoosterPalette);
        var expand = type.GetMethod("Expand", Hidden);
        expand.Invoke(palette, new object[] { null, 3 });
        var canvas = (Canvas)type.GetField("_canvas", Hidden).GetValue(palette);
        var buttons = canvas.Children.OfType<Button>().Where(b => b.Tag is bool).ToList();
        Assert(buttons.Count == 11, "Dix rotations et une inversion");
        Assert(buttons.Count(b => b.IsEnabled) == 10, "Inversion désactivée pour sélection incompatible");
        Assert(BoosterPalette.ParseAngle("-22,5", out double customAngle) && customAngle == -22.5, "Angle personnalisé signé avec virgule");
        Assert(!BoosterPalette.ParseAngle("NaN", out _) && !BoosterPalette.ParseAngle("0", out _) && !BoosterPalette.ParseAngle("181", out _), "Angles personnalisés invalides refusés");
        var card = (Border)palette.Content;
        card.Measure(new Size(310, 406)); card.Arrange(new Rect(0, 0, 310, 406)); card.UpdateLayout();
        foreach (var button in buttons)
        {
            Assert(Canvas.GetLeft(button) + button.ActualWidth <= canvas.Width, "Bouton contenu horizontalement");
            Assert(Canvas.GetTop(button) + button.ActualHeight <= canvas.Height, "Bouton contenu verticalement");
        }
        var bitmap = new RenderTargetBitmap(310, 406, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);
        type.GetField("_expanded", Hidden).SetValue(palette, false);
        type.GetField("_canFlip", Hidden).SetValue(palette, true);
        expand.Invoke(palette, new object[] { null, 25 });
        Assert(buttons.All(b => b.IsEnabled), "Inversion disponible pour sélection compatible, libre ou raccordée");
        var status = (TextBlock)type.GetField("_status", Hidden).GetValue(palette);
        Assert(status.Text.Contains("axes et flèches"), "Aperçu simplifié annoncé pour grande sélection");
        int applications = 0;
        palette.Apply += (angle, flip) => { if (angle == 45 && !flip) applications++; };
        var plus45 = buttons.Single(b => (string)b.Content == "+45° ↷");
        plus45.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(applications == 1, "Clic +45 transmet une seule opération de rotation");
        double? preview = null;
        palette.Preview += (angle, flip) => preview = angle;
        plus45.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        Assert(preview == 45, "Survol transmet le même angle que le clic");
        plus45.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
        Assert(preview == null, "Sortie du survol efface l’aperçu");
        type.GetField("_expanded", Hidden).SetValue(palette, false);
        type.GetField("_supports", Hidden).SetValue(palette, new Func<double, bool, bool>((degrees, invert) => invert));
        expand.Invoke(palette, new object[] { null, 1 });
        Assert(buttons.Count(b => b.IsEnabled) == 1 && (bool)buttons.Single(b => b.IsEnabled).Tag,
            "Rosace d’un té entièrement raccordé : seule l’inversion compatible est active");
        Assert(((SolidColorBrush)card.Background).Color == ((SolidColorBrush)palette.FindResource("Surface")).Color,
            "La rosace reprend la surface du thème partagé BIMaestro");
        Assert(card.Background.Opacity > 0 && card.Background.Opacity <= 0.1,
            "Fond de rosace quasi transparent avec surface souris continue");
        var copyButton = (Button)type.GetField("_copy", Hidden).GetValue(palette);
        int copyRequests = 0;
        palette.CopyOrientation += () => copyRequests++;
        copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(copyRequests == 1, "Pipette : copie directe depuis la référence");
        Assert(!canvas.Children.OfType<CheckBox>().Any(), "Aucune case : le sens est systématiquement inclus");
        palette.SetCopyAvailability(false);
        Assert(!copyButton.IsEnabled && copyButton.Visibility == Visibility.Collapsed, "Pipette masquée sur sélection incompatible");
        palette.SetCopyAvailability(true, 2);
        Assert(copyButton.Visibility == Visibility.Collapsed, "Pipette masquée si plusieurs accessoires sont sélectionnés au départ");
        palette.SetCopyAvailability(true);
        Assert(copyButton.IsEnabled && copyButton.Visibility == Visibility.Visible, "Pipette disponible pour une référence unique");
        foreach (var control in canvas.Children.OfType<FrameworkElement>())
            Assert(Canvas.GetTop(control) + control.ActualHeight <= canvas.Height && Canvas.GetLeft(control) + control.ActualWidth <= canvas.Width,
                "Contrôle contenu dans la rosace agrandie");
        if (windowLifecycle)
        {
            var projection = new BoosterProjection { Left = -20000, Top = -20000, Right = -19000, Bottom = -19000 };
            void Set(string name, object value) => serviceType.GetField(name, Hidden).SetValue(service, value);
            var present = serviceType.GetMethod("PresentIfReady", Hidden);
            var now = DateTime.UtcNow;
            Set("_palette", palette);
            Set("_projection", projection);
            Set("_parts", new System.Collections.Generic.List<BoosterPart> { new BoosterPart { CanFlip = true } });
            Set("_enabled", true); Set("_ready", true); Set("_suppressed", false);
            Set("_selectedCount", 1); Set("_quietSince", now);
            Assert(serviceType.GetField("_event", Hidden).GetValue(service) == null, "Test sans événement externe ni callback Revit");
            present.Invoke(service, new object[] { now.AddMilliseconds(199) });
            Assert(!palette.IsVisible, "Aucune pastille avant les 200 ms");
            for (int callback = 1; callback <= 100; callback++)
            {
                if (MepBoosterService.SelectionContextChanged(originalDocument, new DocumentWrapper(1), 10, 10, "1,2", "1,2", false))
                    Set("_quietSince", now.AddMilliseconds(callback * 5));
            }
            present.Invoke(service, new object[] { now.AddMilliseconds(200) });
            Assert(palette.IsVisible, "Le service affiche la pastille malgré 100 wrappers successifs du même document");
            Assert(ReferenceEquals(((Button)card.Child).Style, palette.FindResource("PrimaryButton")), "La pastille utilise le bouton primaire vert du thème BIMaestro");
            Assert(palette.Topmost, "Même affichage au premier plan que les rosaces existantes");
            double anchoredLeft = palette.Left, anchoredTop = palette.Top;
            palette.RefreshAfterRotation(1, true, (degrees, invert) => !invert);
            Assert(palette.IsVisible && (bool)type.GetField("_expanded", Hidden).GetValue(palette), "Après rotation : rosace maintenue ouverte");
            Assert(palette.Left == anchoredLeft && palette.Top == anchoredTop, "Après rotation : position fixe");
            Assert(buttons.Where(b => !(bool)b.Tag).All(b => b.IsEnabled) && !buttons.Single(b => (bool)b.Tag).IsEnabled,
                "Après rotation : capacités recalculées sans fermer la rosace");
            serviceType.GetMethod("SelectionChanged", Hidden).Invoke(service, new object[] { null, null });
            Assert(palette.IsVisible && !(bool)suppressed.GetValue(service),
                "Une notification de sélection seule ne ferme pas la pastille");
            Set("_selectionCheckPending", false);
            using (var previewTest = new PreviewWindowScope())
            {
                var transform = BoosterNative.DeviceToDip(previewTest.Window);
                Assert(transform.M11 > 0 && transform.M22 > 0, "Conversion DPI disponible avant le premier affichage de l’aperçu");
                previewTest.Window.Show(); previewTest.Window.Hide();
                transform = BoosterNative.DeviceToDip(previewTest.Window);
                Assert(transform.M11 > 0, "Conversion DPI disponible après masquage de l’aperçu");
            }
            service.TryPreview(() => { throw new NullReferenceException("Erreur d’aperçu simulée"); });
            Assert(palette.IsVisible && !(bool)suppressed.GetValue(service), "Une erreur d’aperçu ne ferme ni ne bloque la rosace");
            suspend.Invoke(service, null);
            var resumed = (DateTime)serviceType.GetField("_quietSince", Hidden).GetValue(service);
            present.Invoke(service, new object[] { resumed.AddMilliseconds(200) });
            Assert(palette.IsVisible, "La pastille réapparaît après suspension sans nouvelle lecture Revit");
            serviceType.GetMethod("Dismiss", Hidden).Invoke(service, null);
            present.Invoke(service, new object[] { resumed.AddSeconds(10) });
            Assert(!palette.IsVisible, "La fermeture volontaire reste respectée après expiration du délai");
            serviceType.GetMethod("Rearm", Hidden).Invoke(service, null);
            Assert(!(bool)suppressed.GetValue(service)
                && (bool)serviceType.GetField("_selectionDirty", Hidden).GetValue(service),
                "Après fermeture utilisateur, la même sélection est réarmée et sera relue");
            // Simulate the refreshed snapshot returned by Revit without changing selection.
            Set("_selectionDirty", false); Set("_ready", true);
            var rearmed = (DateTime)serviceType.GetField("_quietSince", Hidden).GetValue(service);
            present.Invoke(service, new object[] { rearmed.AddMilliseconds(199) });
            Assert(!palette.IsVisible, "Réapparition : respecte les 200 ms de repos");
            present.Invoke(service, new object[] { rearmed.AddMilliseconds(200) });
            Assert(palette.IsVisible, "Réapparition sans désélectionner ni resélectionner");
            Assert(!(bool)type.GetField("_expanded", Hidden).GetValue(palette), "Réapparition sous forme de pastille, pas de rosace encombrante");
            serviceType.GetMethod("Dismiss", Hidden).Invoke(service, null);
        }
        palette.Close();
        Console.WriteLine("PASS — rosace, capacités, angles et rendu. Les transactions nécessitent Revit.");
    }
    private static void Assert(bool value, string description)
    {
        if (!value) throw new Exception(description);
        Console.WriteLine("PASS — " + description);
    }
    private sealed class PreviewWindowScope : IDisposable
    {
        internal readonly BoosterPreview Window = new BoosterPreview(IntPtr.Zero)
            { Left = -20000, Top = -20000, Width = 100, Height = 100 };
        public void Dispose() => Window.Close();
    }
    private static BoosterPortPose Port(string key, double x, double y, double dx, double dy, bool connected = true, double size = 0.1) =>
        new BoosterPortPose { Key = key, Position = new Point3D(x, y, 0), Direction = new Vector3D(dx, dy, 0),
            Connected = connected, Shape = 1, SizeA = size, SizeB = size };
    private static void TestKinematics()
    {
        var tee = BoosterKinematics.Create(new[] { Port("A", -1, 0, -1, 0), Port("B", 1, 0, 1, 0), Port("C", 0, 1, 0, 1) });
        var flip = tee.ConnectionMap(180, true);
        Assert(flip != null && flip["A"] == "B" && flip["B"] == "A" && flip["C"] == "C", "Té raccordé : inversion échange le passage et conserve le piquage");
        Assert(tee.ConnectionMap(45, false) == null, "Té raccordé : rotation qui déplacerait le piquage refusée");
        tee.Ports[2].Connected = false;
        Assert(tee.ConnectionMap(90, false) != null, "Té avec piquage libre : rotation à 90° autorisée");
        var reordered = BoosterKinematics.Create(new[] { tee.Ports[2], tee.Ports[1], tee.Ports[0] });
        Assert(reordered.ConnectionMap(180, true)["A"] == "B", "L’axe du té ne dépend pas de l’ordre des connecteurs");
        var elbow = BoosterKinematics.Create(new[] { Port("A", 0, 0, -1, 0), Port("B", 1, 1, 0, 1) });
        var elbowFlip = elbow.ConnectionMap(180, true);
        Assert(elbowFlip != null && elbowFlip["A"] == "B" && elbowFlip["B"] == "A", "Coude symétrique raccordé : inversion par la bissectrice");
        Assert(elbow.ConnectionMap(90, false) == null, "Coude raccordé aux deux bouts : pivot déplaçant le deuxième bout refusé");
        elbow.Ports[0].Connected = false;
        var oneEnd = BoosterKinematics.Create(elbow.Ports);
        Assert(oneEnd.Center == elbow.Ports[1].Position && oneEnd.ConnectionMap(45, false) != null, "Coude avec une extrémité libre : pivot autour de l’extrémité raccordée");
        var unequal = BoosterKinematics.Create(new[] { Port("A", 0, 0, -1, 0), Port("B", 2, 1, 0, 1) });
        Assert(unequal.ConnectionMap(180, true) == null, "Coude asymétrique raccordé : pas de déplacement silencieux du réseau");
        var reducer = BoosterKinematics.Create(new[] { Port("A", -1, 0, -1, 0), Port("B", 1, 0, 1, 0, true, 0.2) });
        Assert(reducer.ConnectionMap(180, true) == null, "Réduction raccordée : inversion incompatible refusée");
        foreach (var p in reducer.Ports) p.Connected = false;
        Assert(reducer.ConnectionMap(180, true) != null, "Réduction libre : retournement autorisé");
        var world = Matrix3D.Identity;
        world.Rotate(new Quaternion(new Vector3D(1, 2, 3), 37)); world.Translate(new Vector3D(100, -30, 17));
        foreach (var p in tee.Ports) { p.Position = world.Transform(p.Position); p.Direction = world.Transform(p.Direction); p.Connected = true; }
        Assert(BoosterKinematics.Create(tee.Ports).ConnectionMap(180, true) != null, "Té oblique dans le projet : inversion conserve les trois connexions");
    }
    private static void AssertStableDocument(object previous, object current)
    {
        if (MepBoosterService.SelectionContextChanged(previous, current, 10, 10, "1,2", "1,2", false))
            throw new Exception("Régression : le même document remet continuellement le délai à zéro.");
    }
    // Models Revit Document's equality contract without pretending to instantiate a live model.
    private sealed class DocumentWrapper
    {
        private readonly int _model;
        internal DocumentWrapper(int model) { _model = model; }
        public override bool Equals(object obj) => obj is DocumentWrapper other && other._model == _model;
        public override int GetHashCode() => _model;
    }
}
