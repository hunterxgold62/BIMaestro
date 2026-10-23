using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BIMaestro.PropertiesAppearance;

namespace BIMaestro.PropertiesAppearance
{
    // The grid boundary is simulated; drawing arithmetic and range validation
    // are the production renderer. No Revit instance or user preferences are used.
    internal sealed class NativePropertiesGrid
    {
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        internal IntPtr Window;
        internal uint RowCount = 2;
        internal bool Current = true, Header, IsEditing;
        internal int Reads;
        internal bool RefreshIfCurrent() => Current;
        internal string Value(uint row, uint col)
        {
            if (row < 1 || row > RowCount) throw new Exception("Out-of-range native cell access");
            Reads++; return "Offset";
        }
        internal bool IsGroupHeader(uint row) => Header;
        internal bool CanStyle(uint row) => true;
        internal Rect Bounds(uint row, uint col) => new Rect();
        internal uint TextColor(uint row, uint col) => 0;
    }
}

internal static class Verify
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static void Field(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static void Dispatch()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [STAThread]
    private static void Main()
    {
        var source = new Border { Background = Brushes.Red };
        var target = new TextBlock();
        BindingOperations.SetBinding(target, TextBlock.ForegroundProperty, new Binding("Background") { Source = source });
        BrowserVisuals.SetTrackedValue(target, TextBlock.ForegroundProperty, Brushes.Green);
        BrowserVisuals.SetTrackedValue(target, TextBlock.ForegroundProperty, Brushes.Yellow);
        Check(target.Foreground == Brushes.Yellow, "browser coloring applies over a binding");
        BrowserVisuals.RestoreTrackedValues();
        Check(BindingOperations.IsDataBound(target, TextBlock.ForegroundProperty) && target.Foreground == Brushes.Red,
            "reset restores the native binding after repeated recoloring");
        source.Background = Brushes.Blue;
        Check(target.Foreground == Brushes.Blue, "restored binding still follows Revit value changes");
        var dynamicTarget = new TextBlock();
        dynamicTarget.Resources["NativeInk"] = Brushes.Red;
        dynamicTarget.SetResourceReference(TextBlock.ForegroundProperty, "NativeInk");
        BrowserVisuals.SetTrackedValue(dynamicTarget, TextBlock.ForegroundProperty, Brushes.Green);
        BrowserVisuals.RestoreTrackedValues();
        dynamicTarget.Resources["NativeInk"] = Brushes.Blue;
        Check(dynamicTarget.Foreground == Brushes.Blue, "dynamic resources survive apply and reset");
        target = new TextBlock();
        BrowserVisuals.SetTrackedValue(target, TextBlock.ForegroundProperty, Brushes.Green);
        BrowserVisuals.RestoreTrackedValues();
        Check(target.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue, "reset removes temporary local values");

        var untouched = new Border { Background = Brushes.Blue, BorderBrush = Brushes.Red, BorderThickness = new Thickness(7) };
        var panel = new Border { Background = Brushes.Yellow, BorderThickness = new Thickness(3) };
        RibbonVisuals.PanelValues.Set(panel, Border.BackgroundProperty, Brushes.Green);
        RibbonVisuals.PanelValues.Set(panel, Border.BorderThicknessProperty, new Thickness(1));
        RibbonVisuals.ResetPapanoelColoring(IntPtr.Zero);
        Check(panel.Background == Brushes.Yellow && panel.BorderThickness == new Thickness(3), "full color reset restores original panel values even when the window has gone");
        Check(untouched.Background == Brushes.Blue && untouched.BorderBrush == Brushes.Red && untouched.BorderThickness == new Thickness(7),
            "full color reset leaves unrelated Revit borders intact");
        RibbonVisuals.Values.Set(panel, Border.BackgroundProperty, Brushes.Green);
        RibbonVisuals.ResetPartialColoring(IntPtr.Zero);
        Check(panel.Background == Brushes.Yellow, "partial color reset restores the original title bar appearance");
        var tab = new TabItem { Background = Brushes.Blue, Foreground = Brushes.Yellow };
        RibbonVisuals.TabValues.Set(tab, TabItem.BackgroundProperty, Brushes.Green);
        RibbonVisuals.ResetTabItemColoring(IntPtr.Zero);
        Check(tab.Background == Brushes.Blue && tab.Foreground == Brushes.Yellow, "project tab reset changes only properties previously colored");

        BrowserHover.PollViewHoverPreview();
        BrowserHover._viewHoverPollGeneration++; BrowserHover._viewHoverPollPending = false;
        BrowserHover.PollViewHoverPreview();
        BrowserHover.Callbacks[0]("old"); Dispatch();
        Check(BrowserHover._viewHoverPollPending && BrowserHover.Applied.Count == 0,
            "a delayed response after reset cannot reopen a popup or clear the replacement poll");
        BrowserHover.Callbacks[1]("current"); Dispatch();
        Check(!BrowserHover._viewHoverPollPending && (string)BrowserHover.Applied.Single() == "current",
            "the replacement hover request completes normally");
        BrowserHover.PollViewHoverPreview(); BrowserHover._activeViewGeneration++;
        BrowserHover.Callbacks[2]("previous view"); Dispatch();
        Check(!BrowserHover._viewHoverPollPending && BrowserHover.Applied.Count == 1,
            "a delayed response for the previous view is ignored");
        BrowserHover.PollViewHoverPreview(); BrowserHover._chromiumBrowser = new object();
        BrowserHover.Callbacks[3]("previous browser"); Dispatch();
        Check(!BrowserHover._viewHoverPollPending && BrowserHover.Applied.Count == 1,
            "a delayed response for a replaced browser is ignored");

        var renderer = (PropertiesPaletteRenderer)FormatterServices.GetUninitializedObject(typeof(PropertiesPaletteRenderer));
        int refreshes = 0;
        renderer.BeforePaint = () => { refreshes++; renderer.RefreshBeforePaint(); };
        renderer.RefreshBeforePaint();
        Check(refreshes == 1, "native before-paint refresh cannot recurse during a row redraw");
        renderer.BeforePaint = null;
        var grid = new NativePropertiesGrid();
        var cell = new PropertiesPaletteRenderer.CellPlan { Row = 2, Label = "Offset", Colors = new PaletteColors() };
        Field(renderer, "_grid", grid); Field(renderer, "_cells", new List<PropertiesPaletteRenderer.CellPlan> { cell });
        Check((bool)Call(renderer, "PlanIsCurrent"), "synchronized palette accepts its color plan");
        grid.RowCount = 1; grid.Reads = 0;
        Check(!(bool)Call(renderer, "PlanIsCurrent") && grid.Reads == 0, "a smaller rebuilt grid is rejected before any native cell access");
        grid.RowCount = 2; grid.Header = true;
        Check(!(bool)Call(renderer, "PlanIsCurrent"), "a group replacing a same-name parameter is rejected");
        grid.Current = false; grid.Reads = 0;
        Check(!(bool)Call(renderer, "PlanIsCurrent") && grid.Reads == 0, "a replaced native grid is rejected before reading cells");

        const int width = 12, height = 8, glyph = (3 * width + 6) * 4;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        pixels[glyph] = pixels[glyph + 1] = pixels[glyph + 2] = 0;
        Field(renderer, "_width", width); Field(renderer, "_height", height); Field(renderer, "_pixels", pixels);
        var rect = new NativePropertiesGrid.Rect { Right = width, Bottom = height };
        cell.Colors.Background = "#101010";
        Call(renderer, "Transform", rect, rect, (uint)0, cell);
        Check(pixels[glyph] == 0 && pixels[glyph + 1] == 0 && pixels[glyph + 2] == 0,
            "a background without explicit ink keeps black text");
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
        pixels[glyph] = pixels[glyph + 1] = pixels[glyph + 2] = 0;
        cell.Colors.Text = "#FF0000";
        Call(renderer, "Transform", rect, rect, (uint)0, cell);
        Check(pixels[glyph] == 0 && pixels[glyph + 1] == 0 && pixels[glyph + 2] == 255,
            "the renderer preserves an explicitly selected text color");
    }
}
