using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    // Right panel: an "Appearance" tab (pick a look, pick a color, more options folded away)
    // and a "Rules" tab (looks shown only when a parameter value matches).
    internal sealed partial class PropertiesAppearanceEditor
    {
        private sealed class Look
        {
            internal string Key; internal StyleSet Set; internal bool HeaderOnly;
        }
        // Ready-made combinations of the layers; "More options" keeps the fine control.
        private static readonly Look[] Looks =
        {
            new Look { Key = "LookPastel", Set = new StyleSet { Background = StyleSet.BgFill, Text = true } },
            new Look { Key = "LookTextOnly", Set = new StyleSet { Background = StyleSet.BgNone, Text = true } },
            new Look { Key = "LookStripe", Set = new StyleSet { Background = StyleSet.BgNone, Text = false, Marker = StyleSet.MarkStripe } },
            new Look { Key = "LookGradientDot", Set = new StyleSet { Background = StyleSet.BgGradient, Text = true, Marker = StyleSet.MarkDot } },
            new Look { Key = "LookOutline", Set = new StyleSet { Background = StyleSet.BgNone, Text = false, Marker = StyleSet.MarkOutline } },
            new Look { Key = "LookTitleOnly", Set = new StyleSet { Background = StyleSet.BgFill, Text = true, HeaderOnly = true }, HeaderOnly = true },
        };
        private static readonly string[] Swatches = { "#155C42", "#234F78", "#77521A", "#604483", "#8A2C4F", "#0F766E", "#475569" };
        private const string DefaultColor = "#155C42";

        private readonly Button _appearanceTab, _rulesTab;
        private readonly StackPanel _appearancePage = new StackPanel(), _rulesPage = new StackPanel();
        private readonly List<Tuple<Button, Look>> _lookButtons = new List<Tuple<Button, Look>>();
        private readonly List<Tuple<Button, string>> _swatchButtons = new List<Tuple<Button, string>>();
        private readonly Expander _moreOptions = new Expander { Margin = new Thickness(0, 14, 0, 6) };
        private bool _rulesShown;

        private StackPanel BuildRightPanel()
        {
            var right = new StackPanel();
            // Tabs and the "…" menu for the rarely used actions.
            var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabs = new StackPanel { Orientation = Orientation.Horizontal }; tabs.Children.Add(_appearanceTab); tabs.Children.Add(_rulesTab); top.Children.Add(tabs);
            var menuButton = MakeButton("⋯", () => { }); menuButton.MinWidth = 0; menuButton.Padding = new Thickness(4, 2, 4, 2); menuButton.Margin = new Thickness(0);
            menuButton.ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.MoreActions");
            var menu = new ContextMenu();
            menu.Items.Add(MenuEntry("RestoreRevitOrder", ResetOrder));
            menu.Items.Add(MenuEntry("ResetAll", () => _resetConfirmation.Visibility = Visibility.Visible));
            menuButton.Click += (_, __) => { menu.PlacementTarget = menuButton; menu.IsOpen = true; };
            Grid.SetColumn(menuButton, 1); top.Children.Add(menuButton); right.Children.Add(top);

            // The scope applies to both tabs.
            var scope = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var forLabel = Label(UiLanguage.Get("PropertiesAppearanceWindow.For"), 12, true); forLabel.Margin = new Thickness(0, 0, 8, 0); forLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(forLabel, Dock.Left); scope.Children.Add(forLabel);
            _scope.SelectionChanged += (_, __) => ScopeChanged(); _scope.MinHeight = 30; _scope.Padding = new Thickness(8, 4, 8, 4); scope.Children.Add(_scope);
            right.Children.Add(scope);

            // Appearance tab: ready-made themes until a row is clicked, then the editor for that row.
            BuildThemesPage(); _appearancePage.Children.Add(_themesPage);
            _selectionPage.Children.Add(BackToThemes());
            BindText(_selection, 15, true); _selection.Margin = new Thickness(0, 0, 0, 2); _selectionPage.Children.Add(_selection);
            BindText(_selectionHint, 12, false, true); _selectionHint.Margin = new Thickness(0, 0, 0, 10); _selectionPage.Children.Add(_selectionHint);
            _selectionPage.Children.Add(_groupTools);
            BuildAppearanceEditor(); _selectionPage.Children.Add(_editor);
            _savedRules.Content = _rules; _selectionPage.Children.Add(_savedRules);
            _appearancePage.Children.Add(_selectionPage);
            right.Children.Add(_appearancePage);

            // Rules tab.
            BuildRulesPage(); _rulesPage.Visibility = Visibility.Collapsed; right.Children.Add(_rulesPage);
            ShowTab(false);
            return right;
        }
        private MenuItem MenuEntry(string key, Action action)
        {
            var item = new MenuItem { Header = UiLanguage.Get("PropertiesAppearanceWindow." + key) };
            item.Click += (_, __) => { try { action(); } catch (Exception ex) { Notify(ex.Message); } };
            return item;
        }
        private void ShowTab(bool rules)
        {
            _rulesShown = rules;
            _appearancePage.Visibility = rules ? Visibility.Collapsed : Visibility.Visible;
            _rulesPage.Visibility = rules ? Visibility.Visible : Visibility.Collapsed;
            foreach (var tab in new[] { Tuple.Create(_appearanceTab, !rules), Tuple.Create(_rulesTab, rules) })
            {
                tab.Item1.Background = tab.Item2 ? Brush("#DCEEE3") : (Brush)Resources["Surface"];
                tab.Item1.Foreground = tab.Item2 ? Brush("#155C42") : (Brush)Resources["Ink"];
                tab.Item1.BorderBrush = tab.Item2 ? Brush("#20845D") : (Brush)Resources["Line"];
            }
            if (rules) RefreshRuleList();
        }
        private Button Tab(string key, bool rules)
        {
            var b = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow." + key), () => ShowTab(rules));
            b.MinWidth = 0; b.MinHeight = 32; b.Padding = new Thickness(6, 4, 6, 4); b.Margin = new Thickness(0, 0, 6, 0); return b;
        }

        private void BuildAppearanceEditor()
        {
            _editor.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.PickALook"), 12, true));
            _editor.Children.Add(LookGallery(_lookButtons, look => ApplyLook(look)));
            _editor.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.PickAColor"), 12, true));
            _editor.Children.Add(SwatchRow(_swatchButtons, color => ApplySwatch(color), () =>
            {
                // The exact picker lives in "More options": open it and scroll to it.
                _moreOptions.IsExpanded = true;
                Dispatcher.BeginInvoke(new Action(() => _text.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
            }));

            // Everything else, folded away.
            var more = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var tones = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) }; tones.Children.Add(_lightButton); tones.Children.Add(_darkButton); more.Children.Add(tones);
            BuildStyleEditor(more);
            more.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.TextColor"), 12)); _text.Height = 34; more.Children.Add(_text);
            more.Children.Add(new Border { Height = 10 });
            more.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.BackgroundColor"), 12)); _background.Height = 34; more.Children.Add(_background);
            _text.SelectedColorChanged += (_, __) => ColorsChanged(); _background.SelectedColorChanged += (_, __) => ColorsChanged();
            BindText(_colorHint, 12, false, true); _colorHint.Margin = new Thickness(0, 10, 0, 6); more.Children.Add(_colorHint);
            _live.Margin = new Thickness(0, 6, 0, 4); more.Children.Add(_live);
            _moreOptions.Header = UiLanguage.Get("PropertiesAppearanceWindow.MoreOptions"); _moreOptions.Content = more; _editor.Children.Add(_moreOptions);

            var remove = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.RemoveThisLook"), ResetSelection);
            remove.HorizontalAlignment = HorizontalAlignment.Left; remove.Margin = new Thickness(0, 8, 0, 0); _editor.Children.Add(remove);
        }

        // Six clickable previews showing the real rendering of each look.
        // Two large tiles per row; the theme's fixed 38 px button height is lifted so names never get cut.
        private System.Windows.Controls.Primitives.UniformGrid LookGallery(List<Tuple<Button, Look>> buttons, Action<Look> pick)
        {
            var panel = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var look in Looks)
            {
                var l = look;
                var b = MakeButton("", () => pick(l));
                b.MinWidth = 0; b.Height = double.NaN; b.MinHeight = 64; b.Padding = new Thickness(8, 7, 8, 7); b.Margin = new Thickness(0, 0, 6, 6);
                b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Top;
                buttons.Add(Tuple.Create(b, look)); panel.Children.Add(b);
            }
            return panel;
        }
        // A short name for any combination: a ready-made look, else its layers ("Pastel + pastille").
        private static string Describe(StyleSet set)
        {
            var look = Looks.FirstOrDefault(l => SameLook(set, l.Set));
            if (look != null) return UiLanguage.Get("PropertiesAppearanceWindow." + look.Key);
            var parts = new List<string>();
            if (set.Background == StyleSet.BgFill) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.LookPastel"));
            else if (set.Background == StyleSet.BgGradient) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.BackgroundGradient"));
            else if (set.Text) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.LookTextOnly"));
            if (set.Marker == StyleSet.MarkStripe) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.MarkerStripe"));
            if (set.Marker == StyleSet.MarkDot) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.MarkerDot"));
            if (set.Marker == StyleSet.MarkOutline) parts.Add(UiLanguage.Get("PropertiesAppearanceWindow.MarkerOutline"));
            return parts.Count == 0 ? UiLanguage.Get("PropertiesAppearanceWindow.None") : string.Join(" + ", parts.Select((p, i) => i == 0 ? p : p.ToLowerInvariant()));
        }
        private UIElement LookSample(Look look, Color color)
        {
            var pale = AppearanceStyles.Mix(Colors.White, color, .12);
            var set = look.Set;
            var row = new Border { Height = 24, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 0, 4) };
            if (set.Background == StyleSet.BgFill) row.Background = new SolidColorBrush(pale);
            if (set.Background == StyleSet.BgGradient) row.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(pale, 0), new GradientStop(Colors.White, 1) }, new Point(0, .5), new Point(1, .5));
            if (set.Marker == StyleSet.MarkOutline) { row.BorderBrush = new SolidColorBrush(color); row.BorderThickness = new Thickness(1.5); }
            if (set.Marker == StyleSet.MarkStripe) { row.BorderBrush = new SolidColorBrush(color); row.BorderThickness = new Thickness(3, 0, 0, 0); row.CornerRadius = new CornerRadius(0); }
            var text = new TextBlock { Text = look.HeaderOnly ? UiLanguage.Get("PropertiesAppearanceWindow.LookTitleSample") : "Aa", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(set.Text ? color : Color.FromRgb(34, 47, 42)) };
            if (set.Marker == StyleSet.MarkDot) text.Inlines.Add(new System.Windows.Documents.Run("  ●") { Foreground = new SolidColorBrush(color) });
            row.Child = text;
            var stack = new StackPanel(); stack.Children.Add(row);
            var name = new TextBlock { Text = UiLanguage.Get("PropertiesAppearanceWindow." + look.Key), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); stack.Children.Add(name);
            return stack;
        }
        // Extra shades revealed by the "+" swatch, before the exact color picker.
        private static readonly string[] MoreSwatches = { "#0B3D2E", "#2E7D32", "#65A30D", "#0E7490", "#0369A1", "#1D4ED8", "#4338CA", "#7C3AED",
            "#A21CAF", "#BE185D", "#B91C1C", "#C2410C", "#B45309", "#A16207", "#57534E", "#111827" };
        private StackPanel SwatchRow(List<Tuple<Button, string>> buttons, Action<string> pick, Action exact)
        {
            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            Button Swatch(string hex)
            {
                var b = MakeButton("", () => pick(hex)); b.MinWidth = 0; b.MinHeight = 0; b.Width = b.Height = 28; b.Padding = new Thickness(0); b.Margin = new Thickness(0, 0, 6, 6);
                b.Background = Brush(hex); b.ToolTip = hex; buttons.Add(Tuple.Create(b, hex)); return b;
            }
            var main = new WrapPanel(); foreach (var hex in Swatches) main.Children.Add(Swatch(hex));
            var extra = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var hex in MoreSwatches) extra.Children.Add(Swatch(hex));
            if (exact != null)
            {
                var picker = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.ExactColor"), exact);
                picker.MinWidth = 0; picker.Height = double.NaN; picker.MinHeight = 28; picker.Padding = new Thickness(4, 3, 4, 3); picker.Margin = new Thickness(0, 0, 6, 6); extra.Children.Add(picker);
            }
            var more = MakeButton("+", () => extra.Visibility = extra.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
            more.MinWidth = 0; more.MinHeight = 0; more.Width = more.Height = 28; more.Padding = new Thickness(0); more.Margin = new Thickness(0, 0, 6, 6);
            more.ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.MoreColors"); main.Children.Add(more);
            root.Children.Add(main); root.Children.Add(extra);
            return root;
        }

        // The color the looks are previewed in: the selection's own color, else BIMaestro sage.
        private Color CurrentColor()
        {
            var rule = CurrentRule;
            return AppearancePreferences.Parse(rule?.Light?.Text) ?? AppearancePreferences.Parse(rule?.Light?.Background)
                ?? AppearancePreferences.Parse(rule?.Dark?.Text) ?? (Color)ColorConverter.ConvertFromString(DefaultColor);
        }
        private void RefreshLooks()
        {
            var color = CurrentColor(); var current = AppearanceStyles.Parse(CurrentRule?.Style);
            bool hasRule = CurrentRule != null;
            foreach (var b in _lookButtons)
            {
                b.Item1.Visibility = b.Item2.HeaderOnly && _selected?.Header != true ? Visibility.Collapsed : Visibility.Visible;
                b.Item1.Content = LookSample(b.Item2, color);
                bool on = hasRule && SameLook(current, b.Item2.Set);
                b.Item1.BorderBrush = on ? Brush("#20845D") : (Brush)Resources["Line"]; b.Item1.BorderThickness = new Thickness(on ? 2 : 1);
            }
            foreach (var s in _swatchButtons)
            {
                bool on = hasRule && AppearancePreferences.Parse(s.Item2) == color;
                s.Item1.BorderBrush = on ? (Brush)Resources["Ink"] : Brushes.Transparent; s.Item1.BorderThickness = new Thickness(on ? 3 : 1);
            }
        }
        private static bool SameLook(StyleSet a, StyleSet b) => a.Background == b.Background && a.Text == b.Text && a.Marker == b.Marker && a.HeaderOnly == b.HeaderOnly;
        private AppearanceRule EnsureRule()
        {
            var rule = CurrentRule;
            if (rule != null) return rule;
            rule = new AppearanceRule { Kind = _selected.Header ? "group" : "parameter", TargetKey = _selected.Header ? _selected.GroupKey : _selected.ParameterKey, Label = _selected.Label, AutoTint = true };
            _profile.Rules.Add(rule); return rule;
        }
        private void ApplyLook(Look look)
        {
            if (_selected == null || _selected.ReadOnly || _profile == null) return;
            var rule = EnsureRule();
            var set = AppearanceStyles.Parse(rule.Style);
            set.Background = look.Set.Background; set.Text = look.Set.Text; set.Marker = look.Set.Marker; set.HeaderOnly = _selected.Header && look.Set.HeaderOnly;
            rule.Style = AppearanceStyles.Store(set);
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) { rule.Light.Text = DefaultColor; rule.AutoTint = true; }
            _loading = true; _enabled.IsChecked = true; _loading = false; Changed(); LoadColors();
        }
        // One color: the pale background and the dark variant are derived automatically.
        private void ApplySwatch(string hex)
        {
            if (_selected == null || _selected.ReadOnly || _profile == null) return;
            var rule = EnsureRule();
            var c = (Color)ColorConverter.ConvertFromString(hex);
            rule.Light = new PaletteColors { Text = c.ToString(), Background = AppearanceStyles.Mix(Colors.White, c, .12).ToString() };
            rule.Dark = new PaletteColors(); rule.AutoTint = true;
            _loading = true; _enabled.IsChecked = true; _loading = false; Changed(); LoadColors();
        }
    }
}
