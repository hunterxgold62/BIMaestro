using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    // Shortcuts that make the tab usable without learning it first:
    // ready-made palette themes, one-click pinning, and a preview that follows the Revit selection.
    internal sealed partial class PropertiesAppearanceEditor
    {
        // ---------- Ready-made themes (shown while no row is selected) ----------
        private sealed class Theme { internal string Key; internal StyleSet Set; internal bool Icons; }
        private static readonly Theme[] Themes =
        {
            new Theme { Key = "ThemeStripe", Set = new StyleSet { Background = StyleSet.BgNone, Text = false, Marker = StyleSet.MarkStripe } },
            new Theme { Key = "ThemeText", Set = new StyleSet { Background = StyleSet.BgNone, Text = true } },
            new Theme { Key = "ThemeGradient", Set = new StyleSet { Background = StyleSet.BgGradient, Text = true } },
            new Theme { Key = "ThemeTitles", Set = new StyleSet { Background = StyleSet.BgFill, Text = true, HeaderOnly = true }, Icons = true },
            new Theme { Key = "ThemeAll", Set = new StyleSet { Background = StyleSet.BgGradient, Text = true, Marker = StyleSet.MarkStripe, Zebra = true }, Icons = true },
        };
        // One harmonious color (and icon) per Revit group; unknown groups cycle through the swatches.
        private static readonly Tuple<string, string, string>[] GroupThemes =
        {
            Tuple.Create("constraints", "#155C42", "levels"), Tuple.Create("structural", "#234F78", "structure"),
            Tuple.Create("dimensions", "#77521A", "zone"), Tuple.Create("identityData", "#604483", "data"),
            Tuple.Create("phasing", "#8A2C4F", "new-project"), Tuple.Create("group:text", "#0F766E", "study"),
            Tuple.Create("ifc", "#475569", "coordination"), Tuple.Create("wallCrossSection", "#0F766E", "details"),
            Tuple.Create("construction", "#234F78", "architecture"), Tuple.Create("graphics", "#155C42", "presentation"),
        };
        private readonly StackPanel _themesPage = new StackPanel(), _selectionPage = new StackPanel();
        private readonly TextBlock _themesHint = new TextBlock();
        private readonly UniformGrid _themeTiles = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 10) };

        private void BuildThemesPage()
        {
            // "(optional)" in grey: nothing has to be picked, the palette can stay as Revit draws it.
            var title = Label(UiLanguage.Get("PropertiesAppearanceWindow.ThemesTitle"), 15, true);
            var optional = new System.Windows.Documents.Run("  " + UiLanguage.Get("PropertiesAppearanceWindow.Optional")) { FontSize = 12, FontWeight = FontWeights.Normal };
            optional.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted"); title.Inlines.Add(optional);
            _themesPage.Children.Add(title);
            BindText(_themesHint, 12, false, true); _themesHint.Margin = new Thickness(0, 0, 0, 10); _themesPage.Children.Add(_themesHint);
            foreach (var theme in Themes)
            {
                var t = theme;
                var b = MakeButton("", () => ApplyTheme(t)); b.MinWidth = 0; b.Height = double.NaN; b.MinHeight = 78; b.Padding = new Thickness(8, 7, 8, 7); b.Margin = new Thickness(0, 0, 6, 6);
                b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Top; b.Content = ThemeSample(t); _themeTiles.Children.Add(b);
            }
            var revit = MakeButton("", () => ApplyTheme(null)); revit.MinWidth = 0; revit.Height = double.NaN; revit.MinHeight = 78; revit.Padding = new Thickness(8, 7, 8, 7); revit.Margin = new Thickness(0, 0, 6, 6);
            revit.HorizontalContentAlignment = HorizontalAlignment.Stretch; revit.VerticalContentAlignment = VerticalAlignment.Top; revit.Content = ThemeSample(null); _themeTiles.Children.Add(revit);
            _themesPage.Children.Add(_themeTiles);
            _themesPage.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.ThemesThenRefine"), 12, false, true));
        }
        // Three group titles drawn with the theme's layers, then its name.
        private UIElement ThemeSample(Theme theme)
        {
            var stack = new StackPanel();
            foreach (var hex in new[] { "#155C42", "#77521A", "#604483" })
            {
                var color = (Color)ColorConverter.ConvertFromString(hex); var pale = AppearanceStyles.Mix(Colors.White, color, .12);
                var set = theme?.Set;
                var row = new Border { Height = 15, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(5, 0, 5, 0), Background = new SolidColorBrush(Color.FromRgb(233, 237, 234)) };
                if (set?.Background == StyleSet.BgFill) row.Background = new SolidColorBrush(pale);
                if (set?.Background == StyleSet.BgGradient) row.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(pale, 0), new GradientStop(Colors.White, 1) }, new Point(0, .5), new Point(1, .5));
                if (set?.Marker == StyleSet.MarkStripe) { row.BorderBrush = new SolidColorBrush(color); row.BorderThickness = new Thickness(3, 0, 0, 0); row.CornerRadius = new CornerRadius(0); }
                var bar = new Border { Height = 4, Width = 46, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(set != null && set.Text ? color : Color.FromRgb(80, 92, 88)) };
                var line = new StackPanel { Orientation = Orientation.Horizontal }; line.Children.Add(bar);
                if (theme?.Icons == true) line.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Child = line; stack.Children.Add(row);
            }
            var name = new TextBlock { Text = UiLanguage.Get("PropertiesAppearanceWindow." + (theme?.Key ?? "ThemeRevit")), FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); stack.Children.Add(name);
            return stack;
        }
        private static Tuple<string, string> GroupLook(string groupKey, int index)
        {
            if ((groupKey ?? "").StartsWith("custom:", StringComparison.Ordinal)) return Tuple.Create("#0F5132", "validation");
            var known = GroupThemes.FirstOrDefault(g => (groupKey ?? "").IndexOf(g.Item1, StringComparison.OrdinalIgnoreCase) >= 0);
            return known != null ? Tuple.Create(known.Item2, known.Item3) : Tuple.Create(Swatches[index % Swatches.Length], (string)null);
        }
        // Colors every group of the current palette at once; parameter colors are kept. null = Revit look.
        private void ApplyTheme(Theme theme)
        {
            if (_context?.CategoryKey == null || _profile == null) { Notify(UiLanguage.Get("PropertiesAppearanceWindow.Groups.FirstSelectAnElementOrAViewIn")); return; }
            var headers = AppearanceOrdering.Arrange(_context.PreviewRows, DraftLayout).Where(r => r.Header && !r.ReadOnly && r.GroupKey != null).GroupBy(r => r.GroupKey).Select(g => g.First()).ToList();
            int index = 0;
            foreach (var header in headers)
            {
                _profile.Rules.RemoveAll(r => r.When == null && r.Kind == "group" && r.TargetKey == header.GroupKey);
                if (theme == null) continue;
                var look = GroupLook(header.GroupKey, index++); var color = (Color)ColorConverter.ConvertFromString(look.Item1);
                var set = new StyleSet { Background = theme.Set.Background, Text = theme.Set.Text, Marker = theme.Set.Marker, HeaderOnly = theme.Set.HeaderOnly, Zebra = theme.Set.Zebra };
                _profile.Rules.Add(new AppearanceRule { Kind = "group", TargetKey = header.GroupKey, Label = header.Label, AutoTint = true, Style = AppearanceStyles.Store(set),
                    Icon = theme.Icons ? look.Item2 : null, Light = new PaletteColors { Text = color.ToString(), Background = AppearanceStyles.Mix(Colors.White, color, .12).ToString() }, Dark = new PaletteColors() });
            }
            _loading = true; _enabled.IsChecked = true; _loading = false; Changed(); LoadColors();
            Notify(UiLanguage.Get(theme == null ? "PropertiesAppearanceWindow.ThemeRevitApplied" : "PropertiesAppearanceWindow.ThemeApplied"));
        }
        // Themes while nothing is selected, the editor once a row is clicked.
        private void UpdateHome()
        {
            bool home = _selected == null;
            _themesPage.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            _selectionPage.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
            bool ready = _context?.CategoryKey != null; _themeTiles.IsEnabled = ready;
            _themesHint.Text = UiLanguage.Get(ready ? "PropertiesAppearanceWindow.ThemesHint" : "PropertiesAppearanceWindow.Groups.FirstSelectAnElementOrAViewIn");
        }
        private Button BackToThemes()
        {
            var back = new Button { Content = UiLanguage.Get("PropertiesAppearanceWindow.BackToThemes"), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            back.Template = LinkTemplate(); back.SetResourceReference(Control.ForegroundProperty, "Muted");
            back.Click += (_, __) => { _selected = null; LoadColors(); RenderRows(); }; return back;
        }
        private static ControlTemplate LinkTemplate()
        {
            var template = new ControlTemplate(typeof(Button)); var text = new FrameworkElementFactory(typeof(ContentPresenter));
            template.VisualTree = text; return template;
        }

        // ---------- One-click pinning ----------
        // The 📌 on a parameter row sends it to « Mes paramètres » at the top (created on first use).
        private UIElement PinButton(PropertyPreviewRow row, Color foreground)
        {
            bool pinned = (DraftLayout.CustomGroups ?? new List<AppearanceCustomGroup>()).Any(g => g.Members.Contains(row.ParameterKey));
            var pin = new Button { Content = new TextBlock { Text = pinned ? "📌" : "📍", FontSize = 12 }, Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Opacity = pinned ? 1 : 0,
                ToolTip = UiLanguage.Get(pinned ? "PropertiesAppearanceWindow.Unpin" : "PropertiesAppearanceWindow.PinToTop") };
            pin.Template = LinkTemplate(); pin.Foreground = new SolidColorBrush(foreground);
            pin.Click += (_, e) => { e.Handled = true; try { TogglePin(row, pinned); } catch (Exception ex) { Notify(ex.Message); } };
            return pin;
        }
        private void TogglePin(PropertyPreviewRow row, bool pinned)
        {
            if (_context?.CategoryKey == null || _profile == null || row.ParameterKey == null) return;
            var previous = _selected;
            if (pinned) { AssignToGroup(row, null); Notify(UiLanguage.Get("PropertiesAppearanceWindow.Unpinned")); }
            else
            {
                var groups = DraftLayout.CustomGroups ?? new List<AppearanceCustomGroup>();
                string name = UiLanguage.Get("PropertiesAppearanceWindow.Groups.MyParameters");
                var target = groups.FirstOrDefault(g => AppearancePreferences.Normalize(g.Name) == AppearancePreferences.Normalize(name)) ?? groups.FirstOrDefault();
                string key = target?.Key ?? AddCustomGroup(name);
                AssignToGroup(row, key); Notify(UiLanguage.Get("PropertiesAppearanceWindow.Pinned").Replace("{0}", row.Label));
            }
            // Pinning is a layout shortcut: keep whatever the right panel was showing.
            _selected = previous == null ? null : _context.PreviewRows.FirstOrDefault(r => (r.ParameterKey ?? r.GroupKey) == (previous.ParameterKey ?? previous.GroupKey) && r.Header == previous.Header) ?? previous;
            LoadColors(); RenderRows();
        }
        private string AddCustomGroup(string name)
        {
            if ((DraftLayout.CustomGroups?.Count ?? 0) >= 20) throw new InvalidOperationException(UiLanguage.Get("PropertiesAppearanceWindow.Groups.Maximum20CustomGroups"));
            var definition = new AppearanceCustomGroup { Name = name }; EditableGroups().Add(definition);
            var keys = AppearanceOrdering.Arrange(_context.PreviewRows, DraftLayout).Where(r => r.Header).Select(r => r.GroupKey).Where(k => k != definition.Key).ToList(); keys.Insert(0, definition.Key); _profile.Layout.Groups = keys;
            TutorialAction?.Invoke("group-created"); return definition.Key;
        }

        // ---------- Follow the Revit selection ----------
        private readonly DispatcherTimer _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private void StartFollowingSelection()
        {
            _followTimer.Tick += (_, __) => { _followTimer.Stop(); if (!_closed) RefreshContext(true); };
            PropertiesAppearanceService.ContextChanged += OnRevitContextChanged;
        }
        private void StopFollowingSelection() { PropertiesAppearanceService.ContextChanged -= OnRevitContextChanged; _followTimer.Stop(); }
        // Debounced: Revit raises several changes while a selection is made, and the palette needs a moment to fill.
        private void OnRevitContextChanged() { if (_closed) return; _followTimer.Stop(); _followTimer.Start(); }
    }
}
