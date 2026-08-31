using BIMaestro.ViewHover;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    public sealed class HeaderModel
    {
        public string Title { get; set; }
        public bool CanClose { get; set; } = true;
        public int CloseCount { get; private set; }
        public void Close() { CloseCount++; }
    }
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            Test("Native tab count, Header, Content and selection unchanged", NativeItems);
            Test("Image is below the single title inside the header", TemplateLayout);
            Test("Original local sizes and header template restored", RestoreLocals);
            Test("Original bindings restored", RestoreBindings);
            Test("30 ON/OFF cycles without new tabs or layout rows", Repeated);
            Test("Style values restored without local overrides", RestoreStyle);
            Test("External overrides retained on OFF", PreserveExternalChanges);
            Test("Unchanged preview does not recreate the header", StableUpdate);
            Test("Duplicate view names resolved by document, never guessed", DocumentIdentity);
            Test("Sheet number/title resolves to the correct sheet", SheetIdentity);
            Test("OFF/ON restores learned view identity and image without recapture", PreviewSurvivesOffOn);
            Test("Last image retained when replacement is missing or corrupt", RetainValidImage);
            Test("Locked replacement keeps previous image then retries", LockedReplacement);
            Test("Only a valid replacement changes the image", ReplaceValidImage);
            Test("Image reloads from disk with a fresh memory cache", ReloadImageFromDisk);
            Test("Two projects with same view names keep distinct previews", SeparatePreviewIdentities);
            Test("Atomic publication preserves old PNG on invalid replacement", AtomicPublication);
            Test("Large hover preview preserves Revit's native tooltip", HoverKeepsNativeTooltip);
            Test("Hover reuses the cached image and updates the existing popup", HoverUpdatesImage);
            Test("OFF keeps hover while restoring compact native tabs", HoverSurvivesOff);
            Test("Hover works from the initial OFF state without any expansion", InitialCompactHover);
            Test("Closing the native tab disposes hover completely", HoverDisposedOnClose);
            Test("Cache retains native PNG resolution for hover", NativeImageResolution);
            Test("Inline cross closes only its native model and respects CanClose", NativeClose);
            Test("Close button is independently anchored at the top-right corner", CloseCornerLayout);
            Test("Disposed inline cross no longer closes anything", DisposedClose);
            Test("Impact uses old OR new membership, not another view's changes", ChangeMembership);
            Test("Repeated modifications count once, add/delete cancels", ChangeAggregation);
            Test("Deleted category survives and each view resets independently", ChangeIsolation);
            Test("Partial tracking never claims zero changes", PartialChanges);
            Test("Change history has a bounded size", BoundedChanges);
            Test("Hover information updates while compact and survives ON/OFF", HoverChangeInformation);
            Test("Compact badges never shrink the image or grow the heading", CompactBadgeLayout);
            Test("Badge counts include movement and retain uncertainty without status prose", CompactBadgeCounts);
            Test("Deferred scans ignore edits already seen in the active view", SeenBeforeDeferredScan);
            Test("Visiting a view acknowledges queued changes, not future changes", VisitAcknowledgesQueue);
            Test("A scan is not replayed and an initialization gap is partial", ChangeScanBoundaries);
            Test("Location translation is distinct from pipe resizing and tiny jitter", MovementClassification);
            RenderExample();
            RenderHoverExample();
            Console.WriteLine(_passed + " ViewDeck tests passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Test(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static TabItem NewTab(string title = "Plan RDC") => new TabItem
    {
        Header = new HeaderModel { Title = title }, Content = new Border { Background = Brushes.White }
    };

    private static void Update(ViewDeckTabDecorator decorator, string signature = "1") =>
        decorator.Update("Plan RDC", PreviewImage(), "En attente", signature);

    private static ImageSource PreviewImage()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 120, 60))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.Black, 2), new RectangleGeometry(new Rect(10, 6, 100, 48))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brushes.Black, 1), Geometry.Parse("M60,6 L60,54 M10,30 L110,30")));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void NativeItems()
    {
        var control = new TabControl();
        TabItem first = NewTab(), second = NewTab("Coupe A");
        object header = first.Header, content = first.Content;
        control.Items.Add(first);
        control.Items.Add(second);
        using (var decorator = ViewDeckTabDecorator.Attach(first))
        {
            Update(decorator);
            control.SelectedItem = first;
            Check(control.Items.Count == 2 && control.Items[0] == first, "A tab was added/replaced");
            Check(ReferenceEquals(first.Header, header) && ReferenceEquals(first.Content, content), "Native model/content was replaced");
            Check(first.IsSelected, "Native selection broken");
            control.SelectedItem = second;
            Check(second.IsSelected && !first.IsSelected, "Native selection change broken");
        }
        Check(control.Items.Count == 2, "OFF changed the native collection");
    }

    private static void TemplateLayout()
    {
        TabItem tab = NewTab();
        using (var decorator = ViewDeckTabDecorator.Attach(tab))
        {
            Update(decorator);
            var root = (Grid)tab.HeaderTemplate.LoadContent();
            root.DataContext = tab.Header;
            root.Measure(new Size(166, 100));
            root.Arrange(new Rect(0, 0, 166, 100));
            root.UpdateLayout();
            var header = (StackPanel)root.Children[0];
            Check(header.Children.Count == 2, "Header must contain one title and one preview");
            var heading = header.Children[0] as Grid;
            var title = heading?.Children[0] as TextBlock;
            var preview = header.Children[1] as Border;
            Check(title?.Text == "Plan RDC" && preview?.Child is Image, "Missing title/image");
            Check(preview.TranslatePoint(new Point(), header).Y >= title.ActualHeight,
                "Image is not positioned below the title");
        }
    }

    private static void RestoreLocals()
    {
        TabItem tab = NewTab();
        var original = new DataTemplate();
        tab.HeaderTemplate = original;
        tab.Height = 24; tab.MinHeight = 20; tab.MaxHeight = 26;
        tab.Width = 120; tab.MinWidth = 60; tab.MaxWidth = 140;
        var decorator = ViewDeckTabDecorator.Attach(tab);
        Update(decorator);
        Check(tab.Height == 100 && tab.HeaderTemplate != original, "Decoration not applied");
        decorator.Dispose(); decorator.Dispose();
        Check(tab.HeaderTemplate == original && tab.Height == 24 && tab.MinHeight == 20 && tab.MaxHeight == 26,
            "Original header/height not restored");
        Check(tab.Width == 120 && tab.MinWidth == 60 && tab.MaxWidth == 140, "Original width not restored");
    }

    private static void RestoreBindings()
    {
        TabItem tab = NewTab();
        var original = new DataTemplate();
        var height = new Binding { Source = 27d };
        var header = new Binding { Source = original };
        BindingOperations.SetBinding(tab, FrameworkElement.HeightProperty, height);
        BindingOperations.SetBinding(tab, HeaderedContentControl.HeaderTemplateProperty, header);
        using (var decorator = ViewDeckTabDecorator.Attach(tab)) Update(decorator);
        Check(BindingOperations.GetBindingBase(tab, FrameworkElement.HeightProperty) == height && tab.Height == 27,
            "Height binding lost");
        Check(BindingOperations.GetBindingBase(tab, HeaderedContentControl.HeaderTemplateProperty) == header && tab.HeaderTemplate == original,
            "Header binding lost");
    }

    private static void Repeated()
    {
        var grid = new Grid();
        var control = new TabControl();
        TabItem tab = NewTab();
        control.Items.Add(tab);
        grid.Children.Add(control);
        for (int index = 0; index < 30; index++)
        {
            using (var decorator = ViewDeckTabDecorator.Attach(tab)) Update(decorator);
            Check(control.Items.Count == 1 && grid.Children.Count == 1 && grid.RowDefinitions.Count == 0, "Extra UI created");
            Check(tab.ReadLocalValue(FrameworkElement.HeightProperty) == DependencyProperty.UnsetValue &&
                tab.ReadLocalValue(HeaderedContentControl.HeaderTemplateProperty) == DependencyProperty.UnsetValue,
                "Local overrides left behind");
        }
    }

    private static void RestoreStyle()
    {
        TabItem tab = NewTab();
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 25d));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Orange));
        tab.Style = style;
        using (var decorator = ViewDeckTabDecorator.Attach(tab)) Update(decorator);
        Check(tab.Style == style && tab.Background == Brushes.Orange && tab.Height == 25, "Style/color altered");
        Check(tab.ReadLocalValue(FrameworkElement.HeightProperty) == DependencyProperty.UnsetValue, "Style is overridden");
    }

    private static void PreserveExternalChanges()
    {
        TabItem tab = NewTab();
        var external = new DataTemplate();
        using (var decorator = ViewDeckTabDecorator.Attach(tab))
        {
            Update(decorator);
            tab.Width = 200;
            tab.HeaderTemplate = external;
        }
        Check(tab.Width == 200 && tab.HeaderTemplate == external, "External values overwritten");
    }

    private static void StableUpdate()
    {
        TabItem tab = NewTab();
        using (var decorator = ViewDeckTabDecorator.Attach(tab))
        {
            Update(decorator);
            DataTemplate original = tab.HeaderTemplate;
            Update(decorator);
            Check(tab.HeaderTemplate == original, "Unchanged header rebuilt");
            Update(decorator, "2");
            Check(tab.HeaderTemplate != original, "New preview not applied");
        }
    }

    private static void DocumentIdentity()
    {
        var first = new ViewDeckTabIdentity { DocumentTitle = "Projet A", Titles = new[] { "RDC" } };
        var second = new ViewDeckTabIdentity { DocumentTitle = "Projet B", Titles = new[] { "RDC" } };
        var candidates = new[] { first, second };
        Check(ViewDeckTabIdentity.Resolve("RDC", "Projet B.rvt - RDC", candidates, item => item) == second,
            "Document qualifier not respected");
        Check(ViewDeckTabIdentity.Resolve("RDC", null, candidates, item => item) == null, "Ambiguous view guessed");
        Check(ViewDeckTabIdentity.Resolve("RDC", "Projet C - RDC", new[] { first }, item => item) == null,
            "Preview from another project accepted");
    }

    private static void SheetIdentity()
    {
        var sheet = new ViewDeckTabIdentity { DocumentTitle = "Projet", Titles = new[] { "Plans", "A101 - Plans" } };
        Check(ViewDeckTabIdentity.Resolve("A101 - Plans", "Projet - A101 - Plans", new[] { sheet }, item => item) == sheet,
            "Sheet title not recognized");
    }

    private static string NewPreviewPath()
    {
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "preview.png");
    }

    private static void WritePreview(string path, byte red)
    {
        byte[] pixels = { 0, 0, red, 255, 0, 0, red, 255, 0, 0, red, 255, 0, 0, red, 255 };
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static void PreviewSurvivesOffOn()
    {
        string path = NewPreviewPath();
        WritePreview(path, 100);
        var memory = new ViewDeckPreviewMemory<string>();
        TabItem tab = NewTab();
        object model = tab.Header;
        var before = memory.ForModel(model);
        before.Remember("Project A", "view-id", path);
        before.Preview.Refresh(path);
        ImageSource image = before.Preview.Image;
        Check(image != null, "Initial image not loaded");
        for (int index = 0; index < 10; index++)
        {
            using (var decorator = ViewDeckTabDecorator.Attach(tab))
            {
                var after = memory.ForModel(tab.Header);
                after.Preview.Refresh(after.PreviewPath);
                decorator.Update("Plan RDC", after.Preview.Image, "", "on");
                Check(after.Document == "Project A" && after.ViewUniqueId == "view-id", "OFF lost learned identity");
                Check(ReferenceEquals(after.Preview.Image, image) && after.Preview.Revision == 1, "OFF discarded/reloaded known image");
                var header = (StackPanel)((Grid)tab.HeaderTemplate.LoadContent()).Children[0];
                Check(((header.Children[1] as Border)?.Child as Image)?.Source == image, "ON shows an empty header");
            }
        }
        var rebuiltTab = NewTab();
        rebuiltTab.Header = model;
        Check(memory.ForModel(rebuiltTab.Header) == before, "Rebuilt tab lost the native model association");
        Check(File.Exists(path), "OFF deleted the disk cache");
    }

    private static void RetainValidImage()
    {
        string path = NewPreviewPath();
        WritePreview(path, 90);
        var preview = new ViewDeckCachedImage();
        preview.Refresh(path);
        ImageSource original = preview.Image;
        File.Delete(path); // This test's own single fixture, not the application cache.
        preview.Refresh(path);
        preview.Refresh(null);
        Check(preview.Image == original, "Missing file erased last image");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        preview.Refresh(path);
        Check(preview.Image == original && preview.Revision == 1, "Corrupt file replaced last image");
    }

    private static void LockedReplacement()
    {
        string path = NewPreviewPath();
        WritePreview(path, 10);
        var preview = new ViewDeckCachedImage();
        preview.Refresh(path);
        ImageSource original = preview.Image;
        DateTime nextStamp = File.GetLastWriteTimeUtc(path).AddSeconds(2);
        WritePreview(path, 220);
        File.SetLastWriteTimeUtc(path, nextStamp);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            preview.Refresh(path);
            Check(preview.Image == original && preview.Revision == 1, "Locked file erased image");
        }
        preview.Refresh(path); // Same timestamp, but now readable: must retry.
        Check(preview.Image != original && preview.Revision == 2, "Locked replacement was never retried");
    }

    private static void ReplaceValidImage()
    {
        string path = NewPreviewPath();
        WritePreview(path, 20);
        var preview = new ViewDeckCachedImage();
        preview.Refresh(path);
        ImageSource original = preview.Image;
        DateTime nextStamp = File.GetLastWriteTimeUtc(path).AddSeconds(2);
        WritePreview(path, 230);
        File.SetLastWriteTimeUtc(path, nextStamp);
        preview.Refresh(path);
        Check(preview.Image != original && preview.Image != null && preview.Revision == 2, "Valid image not replaced");
        ImageSource replacement = preview.Image;
        preview.Refresh(path);
        Check(preview.Image == replacement && preview.Revision == 2, "Unchanged image decoded again");
    }

    private static void ReloadImageFromDisk()
    {
        string path = NewPreviewPath();
        WritePreview(path, 70);
        var freshCache = new ViewDeckCachedImage();
        freshCache.Refresh(path);
        Check(freshCache.Image != null && freshCache.Revision == 1, "Existing PNG not reused");
        // OnLoad must release the handle so another export can replace this PNG.
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusive.Length > 0, "No fixture data");
    }

    private static void SeparatePreviewIdentities()
    {
        var memory = new ViewDeckPreviewMemory<string>();
        var first = memory.ForModel(new HeaderModel { Title = "RDC" });
        var second = memory.ForModel(new HeaderModel { Title = "RDC" });
        string path = NewPreviewPath();
        WritePreview(path, 80);
        first.Remember("Project A", "same-id", path);
        first.Preview.Refresh(path);
        second.Remember("Project B", "same-id", path + ".missing");
        second.Preview.Refresh(second.PreviewPath);
        Check(first.Preview.Image != null && second.Preview.Image == null, "Another project's image leaked");
        first.Remember("Project A", "different-view", path + ".missing");
        Check(first.Preview.Image == null, "Reassigned tab kept the wrong view's image");
    }

    private static void AtomicPublication()
    {
        string target = NewPreviewPath();
        WritePreview(target, 90);
        byte[] original = File.ReadAllBytes(target);
        string generated = Path.Combine(Path.GetDirectoryName(target), "new.png");
        File.WriteAllBytes(generated, new byte[] { 1, 2, 3 });
        bool refused = false;
        try { ViewDeckCachedImage.PublishGeneratedImage(generated, target); }
        catch { refused = true; }
        Check(refused && Convert.ToBase64String(File.ReadAllBytes(target)) == Convert.ToBase64String(original),
            "Invalid generation destroyed the cached PNG");
        WritePreview(generated, 220);
        byte[] replacement = File.ReadAllBytes(generated);
        ViewDeckCachedImage.PublishGeneratedImage(generated, target);
        Check(!File.Exists(generated) && Convert.ToBase64String(File.ReadAllBytes(target)) == Convert.ToBase64String(replacement),
            "Valid generation was not published");
        string newTarget = Path.Combine(Path.GetDirectoryName(target), "first.png");
        WritePreview(generated, 120);
        ViewDeckCachedImage.PublishGeneratedImage(generated, newTarget);
        Check(File.Exists(newTarget) && !File.Exists(generated), "First generation was not published");
    }

    private static void HoverKeepsNativeTooltip()
    {
        TabItem tab = NewTab();
        const string nativeTip = "Projet A - Plan RDC";
        tab.ToolTip = nativeTip;
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            presentation.Update(true, "Plan RDC", PreviewImage(), "", "1");
            var hover = presentation.Hover.ToolTip;
            Check(hover != null && hover.Width > tab.Width, "Missing large hover preview");
            Check(hover.PlacementTarget == tab && !hover.Focusable && !hover.IsHitTestVisible,
                "Hover should not steal focus or clicks");
            Check(ViewDeckHoverPreview.DelayMilliseconds == 500, "Hover delay incorrect");
            Check(Equals(tab.ToolTip, nativeTip), "Native tooltip/identity was overwritten");
        }
        Check(Equals(tab.ToolTip, nativeTip), "OFF lost original tooltip");
    }

    private static void HoverUpdatesImage()
    {
        TabItem tab = NewTab();
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            ImageSource first = PreviewImage();
            presentation.Update(false, "Plan RDC", first, "", "first");
            ToolTip hover = presentation.Hover.ToolTip;
            var body = (StackPanel)hover.Content;
            var frame = (Border)body.Children[1];
            var grid = (Grid)frame.Child;
            var image = (Image)grid.Children[1];
            Check(image.Source == first, "Hover should reuse existing pixels, not export/read them");
            ImageSource replacement = PreviewImage();
            presentation.Update(false, "Plan renommé", replacement, "", "second");
            Check(presentation.Hover.ToolTip == hover && image.Source == replacement, "Popup was recreated or not refreshed");
            Check(((TextBlock)((Grid)body.Children[0]).Children[0]).Text == "Plan renommé", "Hover title stale");
        }
    }

    private static void HoverSurvivesOff()
    {
        TabItem tab = NewTab();
        tab.Height = 24;
        ImageSource image = PreviewImage();
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            ToolTip hover = presentation.Hover.ToolTip;
            for (int index = 0; index < 30; index++)
            {
                presentation.Update(true, "RDC", image, "", "1");
                Check(presentation.IsExpanded && tab.Height == 100, "ON did not expand tab");
                presentation.SetExpanded(false);
                Check(!presentation.IsExpanded && tab.Height == 24 && tab.HeaderTemplate == null,
                    "OFF did not restore compact layout");
                Check(presentation.Hover.ToolTip == hover && hover.Content != null && hover.PlacementTarget == tab,
                    "OFF disposed/replaced the hover preview");
                var body = (StackPanel)hover.Content;
                var preview = (Grid)((Border)body.Children[1]).Child;
                Check(((Image)preview.Children[1]).Source == image, "OFF lost hover pixels");
            }
        }
    }

    private static void InitialCompactHover()
    {
        TabItem tab = NewTab();
        var nativeHeader = new DataTemplate();
        tab.HeaderTemplate = nativeHeader;
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            presentation.Update(false, "RDC", PreviewImage(), "", "1");
            Check(!presentation.IsExpanded && tab.HeaderTemplate == nativeHeader && double.IsNaN(tab.Height),
                "Initial OFF modified native layout");
            Check(presentation.Hover.ToolTip.Content != null && presentation.Hover.ToolTip.PlacementTarget == tab,
                "Initial OFF has no hover preview");
        }
    }

    private static void HoverDisposedOnClose()
    {
        TabItem tab = NewTab();
        var presentation = new ViewDeckTabPresentation(tab);
        presentation.Update(true, "RDC", PreviewImage(), "", "1");
        ToolTip hover = presentation.Hover.ToolTip;
        presentation.Dispose();
        Check(!hover.IsOpen && hover.Content == null && hover.PlacementTarget == null,
            "Close left a popup or native-tab reference alive");
        Check(tab.HeaderTemplate == null, "Close retained custom header");
        tab.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        presentation.Dispose();
    }

    private static void NativeImageResolution()
    {
        string path = NewPreviewPath();
        WritePreview(path, 120); // Test PNG is deliberately 2 x 2 pixels.
        var cache = new ViewDeckCachedImage();
        cache.Refresh(path);
        var source = (BitmapSource)cache.Image;
        Check(source.PixelWidth == 2 && source.PixelHeight == 2, "Cache resized pixels instead of retaining native resolution");
    }

    private static void RenderHoverExample()
    {
        TabItem tab = NewTab();
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            presentation.Update(false, "Plan RDC", PreviewImage(), "", "1",
                new ViewDeckChangeCounts { Added = 1, Modified = 48, Deleted = 321 });
            var body = (FrameworkElement)presentation.Hover.ToolTip.Content;
            body.Measure(new Size(464, 400));
            body.Arrange(new Rect(new Point(), body.DesiredSize));
            body.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(body.ActualWidth),
                (int)Math.Ceiling(body.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(body);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hover-preview.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine("WPF hover sample rendered: " + path);
        }
    }

    private static void RenderExample()
    {
        var control = new TabControl { Background = Brushes.White };
        foreach (string title in new[] { "Plan RDC", "Coupe A", "Vue 3D", "A101 - Plans" })
        {
            TabItem tab = NewTab(title);
            control.Items.Add(tab);
            var decorator = ViewDeckTabDecorator.Attach(tab);
            decorator.Update(title, PreviewImage(), "", title);
        }
        control.SelectedIndex = 0;
        control.Measure(new Size(730, 160));
        control.Arrange(new Rect(0, 0, 730, 160));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(730, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-tabs-preview.png");
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine("WPF sample rendered: " + path);
    }

    private static Button CloseButton(TabItem tab)
    {
        // LoadContent alone doesn't instantiate a template's routed event table.
        var presenter = new ContentPresenter { Content = tab.Header, ContentTemplate = tab.HeaderTemplate };
        presenter.Measure(new Size(166, 100));
        presenter.Arrange(new Rect(0, 0, 166, 100));
        presenter.UpdateLayout();
        var header = (Grid)VisualTreeHelper.GetChild(presenter, 0);
        return (Button)header.Children[1];
    }

    private static void CloseCornerLayout()
    {
        foreach (string title in new[] { "RDC", "FLU_Vue en Plan RDC avec un titre très long" })
        {
            var tab = NewTab(title);
            using (var decorator = ViewDeckTabDecorator.Attach(tab))
            {
                decorator.Update(title, PreviewImage(), "", title);
                Button close = CloseButton(tab);
                var root = (Grid)VisualTreeHelper.GetParent(close);
                Point corner = close.TranslatePoint(new Point(), root);
                Check(Math.Abs(corner.Y) < 0.1 && Math.Abs(corner.X + close.ActualWidth - root.ActualWidth) < 0.1,
                    "Close button is not anchored at the top-right corner");
                var stack = (StackPanel)root.Children[0];
                var frame = (Border)stack.Children[1];
                Point previewOrigin = frame.TranslatePoint(new Point(), root);
                Check(frame.ActualWidth == 130 && frame.ActualHeight == 64 && previewOrigin.Y >= close.ActualHeight,
                    "Close button overlaps/resizes the preview");
                // Geometric hit test: this headless presenter has no HwndSource.
                var hit = VisualTreeHelper.HitTest(root,
                    new Point(corner.X + close.ActualWidth / 2, corner.Y + close.ActualHeight / 2))?.VisualHit;
                while (hit != null && hit != close) hit = VisualTreeHelper.GetParent(hit);
                Check(hit == close, "Corner close target is not clickable");
            }
        }
    }

    private static void NativeClose()
    {
        TabItem first = NewTab("RDC"), second = NewTab("RDC");
        var firstModel = (HeaderModel)first.Header;
        var secondModel = (HeaderModel)second.Header;
        using (var decorator = ViewDeckTabDecorator.Attach(second))
        {
            Update(decorator);
            Button close = CloseButton(second);
            var args = new RoutedEventArgs(Button.ClickEvent);
            close.RaiseEvent(args);
            Check(firstModel.CloseCount == 0 && secondModel.CloseCount == 1 && args.Handled,
                "Cross targeted the wrong same-name tab or click bubbled: first=" + firstModel.CloseCount +
                ", second=" + secondModel.CloseCount + ", handled=" + args.Handled + ", enabled=" + close.IsEnabled);
            secondModel.CanClose = false;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(secondModel.CloseCount == 1, "Native CanClose ignored");
            Check(Equals(close.Content, "×") && close.Width == 19 && !close.Focusable, "Cross layout/focus incorrect");
        }
    }

    private static void DisposedClose()
    {
        TabItem tab = NewTab();
        var decorator = ViewDeckTabDecorator.Attach(tab);
        Update(decorator);
        Button close = CloseButton(tab);
        decorator.Dispose();
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((HeaderModel)tab.Header).CloseCount == 0, "Old OFF template can still close a tab");
    }

    private static ViewDeckChange Change(long id, ViewDeckChangeKind kind, string category = "Murs") =>
        new ViewDeckChange { Id = id, Kind = kind, Category = category, Sequence = 1 };

    private static void ChangeMembership()
    {
        var journal = new ViewDeckChangeJournal();
        var before = new HashSet<long> { 1, 2 };
        var after = new HashSet<long> { 3 };
        journal.Apply(Change(1, ViewDeckChangeKind.Moved, "Portes"), before, after);
        journal.Apply(Change(2, ViewDeckChangeKind.Deleted), before, after);
        journal.Apply(Change(3, ViewDeckChangeKind.Added, "Canalisations"), before, after);
        journal.Apply(Change(4, ViewDeckChangeKind.Modified), before, after);
        Check(journal.Count == 3 && journal.Summary().Contains("+ 1") && journal.Summary().Contains("− 1"),
            "Old/new membership not respected");
        Check(journal.Details().Contains("Portes : 1 déplacement") && journal.Details().Contains("Murs : 1 suppression"),
            "Missing category/action detail");
    }

    private static void ChangeAggregation()
    {
        var journal = new ViewDeckChangeJournal();
        var members = new HashSet<long> { 1, 2 };
        journal.Apply(Change(1, ViewDeckChangeKind.Added), members, members);
        journal.Apply(Change(1, ViewDeckChangeKind.Modified), members, members);
        Check(journal.Count == 1 && journal.Summary().Contains("+ 1"), "Creation counted twice");
        journal.Apply(Change(1, ViewDeckChangeKind.Deleted), members, members);
        Check(journal.Count == 0, "Created then deleted element still counted");
        journal.Apply(Change(2, ViewDeckChangeKind.Moved), members, members);
        journal.Apply(Change(2, ViewDeckChangeKind.Modified), members, members);
        Check(journal.Count == 1 && journal.Details().Contains("déplacement"), "Movement was lost/double counted");
        journal.Apply(Change(2, ViewDeckChangeKind.Deleted), members, members);
        journal.Apply(Change(2, ViewDeckChangeKind.Added), members, members);
        Check(journal.Count == 1 && journal.Details().Contains("modification"), "Restoration incorrectly counted as new");
    }

    private static void ChangeIsolation()
    {
        var first = new ViewDeckChangeJournal();
        var second = new ViewDeckChangeJournal();
        var members = new HashSet<long> { 1 };
        var deleted = Change(1, ViewDeckChangeKind.Deleted, "Murs");
        first.Apply(deleted, members, new HashSet<long>());
        second.Apply(deleted, members, new HashSet<long>());
        first.Clear();
        Check(first.Count == 0 && second.Count == 1 && second.Details().Contains("Murs : 1 suppression"),
            "Visiting one view reset another view or lost the deletion category");
    }

    private static void PartialChanges()
    {
        var journal = new ViewDeckChangeJournal { Partial = true };
        Check(!journal.Summary().Contains("Aucun") && journal.Summary().Contains("partiel"), "False zero on partial tracking");
        journal.Clear();
        Check(!journal.Partial && journal.Count == 0, "Visit did not reset tracking state");
    }

    private static void BoundedChanges()
    {
        var journal = new ViewDeckChangeJournal();
        for (long id = 1; id <= 1005; id++)
        {
            var members = new HashSet<long> { id };
            journal.Apply(Change(id, ViewDeckChangeKind.Modified), members, members);
        }
        Check(journal.Count == 1000 && journal.Partial, "History is unbounded or silently truncated");
    }

    private static void HoverChangeInformation()
    {
        TabItem tab = NewTab();
        using (var presentation = new ViewDeckTabPresentation(tab))
        {
            presentation.Update(false, "RDC", PreviewImage(), "", "1", new ViewDeckChangeCounts { Added = 2 });
            var heading = (Grid)((StackPanel)presentation.Hover.ToolTip.Content).Children[0];
            var info = (StackPanel)heading.Children[1];
            Check(Grid.GetColumn(info) == 1 && ((TextBlock)((Border)info.Children[0]).Child).Text == "+ 2",
                "Information not placed beside title");
            presentation.SetExpanded(true);
            presentation.SetExpanded(false);
            Check(((TextBlock)((Border)info.Children[0]).Child).Text == "+ 2", "OFF reset hover summary");
            presentation.Update(false, "RDC", PreviewImage(), "", "1");
            foreach (Border badge in info.Children)
                Check(badge.Visibility == Visibility.Collapsed && ((TextBlock)badge.Child).Text == "",
                    "Empty/active view retained a badge or status prose");
        }
    }

    private static void SeenBeforeDeferredScan()
    {
        var first = new ViewDeckChangeWindow();
        var second = new ViewDeckChangeWindow();
        var members = new HashSet<long> { 1 };
        first.Scan(members, new ViewDeckChange[0], 0, false, false);
        second.Scan(members, new ViewDeckChange[0], 0, false, false);
        first.Visit(0);
        var edit = Change(1, ViewDeckChangeKind.Modified);
        first.Acknowledge(1); // DocumentChanged while active, before Idling.
        first.Scan(members, new[] { edit }, 1, false, false); // User already switched away.
        second.Scan(members, new[] { edit }, 1, false, false);
        Check(first.Journal.Count == 0 && second.Journal.Count == 1,
            "Deferred scan reported already-seen edits or erased the other view's edits");
    }

    private static void CompactBadgeLayout()
    {
        using (var presentation = new ViewDeckTabPresentation(NewTab()))
        {
            ImageSource pixels = PreviewImage();
            presentation.Update(false, "Un nom de vue très long pour vérifier la place disponible", pixels, "", "1",
                new ViewDeckChangeCounts { Added = 1, Modified = 48, Deleted = 321 });
            var body = (StackPanel)presentation.Hover.ToolTip.Content;
            body.Measure(new Size(464, 500));
            body.Arrange(new Rect(0, 0, 464, body.DesiredSize.Height));
            body.UpdateLayout();
            var heading = (Grid)body.Children[0];
            var preview = (Grid)((Border)body.Children[1]).Child;
            double originalHeight = body.ActualHeight;
            Check(heading.ActualHeight == 24 && preview.ActualHeight == 340 && heading.Children.Count == 2,
                "Verbose header returned or image area shrank");
            Check(((Image)preview.Children[1]).Source == pixels, "Badge update altered preview pixels");
            var info = (StackPanel)heading.Children[1];
            Check(info.Orientation == Orientation.Horizontal && info.ActualWidth < 220,
                "Badges are stacked or consume too much space");
            presentation.Update(false, "RDC", pixels, "", "1", new ViewDeckChangeCounts());
            body.Measure(new Size(464, 500));
            body.Arrange(new Rect(0, 0, 464, body.DesiredSize.Height));
            body.UpdateLayout();
            Check(body.ActualHeight == originalHeight && preview.ActualHeight == 340, "Badge presence changes image layout");
            foreach (Border badge in info.Children) Check(badge.Visibility == Visibility.Collapsed, "Zero badge should be hidden");
        }
    }

    private static void CompactBadgeCounts()
    {
        var journal = new ViewDeckChangeJournal { Partial = true };
        var members = new HashSet<long> { 1, 2, 3, 4 };
        journal.Apply(Change(1, ViewDeckChangeKind.Added), members, members);
        journal.Apply(Change(2, ViewDeckChangeKind.Modified), members, members);
        journal.Apply(Change(3, ViewDeckChangeKind.Moved), members, members);
        journal.Apply(Change(4, ViewDeckChangeKind.Deleted), members, members);
        ViewDeckChangeCounts counts = journal.Counts();
        Check(counts.Added == 1 && counts.Modified == 2 && counts.Deleted == 1 && counts.Partial, "Incorrect compact counts");
        using (var presentation = new ViewDeckTabPresentation(NewTab()))
        {
            presentation.Update(false, "RDC", PreviewImage(), "", "1", counts);
            var info = (StackPanel)((Grid)((StackPanel)presentation.Hover.ToolTip.Content).Children[0]).Children[1];
            string[] expected = { "≈+ 1", "≈~ 2", "≈− 1" };
            Color[] expectedColors = { Color.FromRgb(27, 112, 62), Color.FromRgb(166, 79, 0), Color.FromRgb(169, 48, 48) };
            for (int i = 0; i < 3; i++)
            {
                Check(((TextBlock)((Border)info.Children[i]).Child).Text == expected[i], "Partial badge lost uncertainty or contains prose");
                Check(((SolidColorBrush)((TextBlock)((Border)info.Children[i]).Child).Foreground).Color == expectedColors[i],
                    "Badge colors must be green for additions, orange for modifications and red for deletions");
            }
        }
    }

    private static void VisitAcknowledgesQueue()
    {
        var window = new ViewDeckChangeWindow();
        var members = new HashSet<long> { 1, 2 };
        window.Scan(members, new ViewDeckChange[0], 0, false, false);
        window.Visit(1);
        var old = Change(1, ViewDeckChangeKind.Modified);
        var recent = Change(2, ViewDeckChangeKind.Modified);
        recent.Sequence = 2;
        window.Scan(members, new[] { old, recent }, 2, false, false);
        Check(window.Journal.Count == 1 && window.Visited && window.Baseline == 1,
            "Visit did not separate past/future changes");
    }

    private static void ChangeScanBoundaries()
    {
        var window = new ViewDeckChangeWindow();
        var members = new HashSet<long> { 1 };
        var edit = Change(1, ViewDeckChangeKind.Modified);
        window.Scan(members, new[] { edit }, 1, false, false);
        Check(window.Journal.Partial && window.Journal.Count == 1, "Unobserved initial period not marked partial");
        window.Journal.Clear();
        window.Scan(members, new[] { edit }, 1, false, false);
        Check(window.Journal.Count == 0, "Previously processed event replayed");
        window.Scan(members, new ViewDeckChange[0], 2, false, true);
        Check(window.Journal.Partial, "Truncated membership not marked partial");
    }

    private static void MovementClassification()
    {
        var line = new double[] { 0, 0, 0, 10, 0, 0 };
        Check(ViewDeckChange.IsTranslation(line, new double[] { 2, 1, 0, 12, 1, 0 }), "Translation missed");
        Check(!ViewDeckChange.IsTranslation(line, new double[] { 0, 0, 0, 12, 0, 0 }), "Pipe resize called a translation");
        Check(!ViewDeckChange.IsTranslation(line, new double[] { 0.00001, 0, 0, 10.00001, 0, 0 }), "Tiny jitter counted as move");
        Check(!ViewDeckChange.IsTranslation(null, line), "Move invented without before snapshot");
        Check(ViewDeckChange.IsTranslation(new double[6], new double[] { 1, 0, 0, 1, 0, 0 }), "Point/door move missed");
    }
}
