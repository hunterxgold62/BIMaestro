using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BIMaestro.UI;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed partial class PropertiesAppearanceEditor : UserControl
    {
        private sealed class ScopeChoice
        {
            internal string Key, Label, Explanation;
            public override string ToString() => Label;
        }
        // Look layers: one background, the text color, one marker, plus an icon and options.
        private readonly List<Tuple<Button, string>> _bgButtons = new List<Tuple<Button, string>>(), _markButtons = new List<Tuple<Button, string>>();
        private string _bgValue = StyleSet.BgFill, _markValue = StyleSet.MarkNone;
        private readonly CheckBox _textLayer = new CheckBox { Margin = new Thickness(0, 2, 0, 8) };
        private readonly ComboBox _icon = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        private readonly CheckBox _headerOnly = new CheckBox { Margin = new Thickness(0, 2, 0, 4) }, _zebra = new CheckBox { Margin = new Thickness(0, 2, 0, 4) };
        private readonly StackPanel _groupOptions = new StackPanel();
        private readonly CheckBox _autoTint = new CheckBox { Margin = new Thickness(0, 2, 0, 6) };
        internal static Action<string> CustomGroupRequested;
        private AppearanceContext _context;
        private readonly Dictionary<string, AppearanceProfile> _drafts = new Dictionary<string, AppearanceProfile>();
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private AppearanceProfile _profile;
        private PropertyPreviewRow _selected;
        private bool _loading, _closed;
        private int _revision;
        private readonly ComboBox _scope = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly TextBlock _caption = new TextBlock(), _hint = new TextBlock(), _selection = new TextBlock(), _selectionHint = new TextBlock(), _scopeHint = new TextBlock(), _changeState = new TextBlock(), _status = new TextBlock(), _colorHint = new TextBlock();
        private readonly StackPanel _rows = new StackPanel(), _rules = new StackPanel();
        private readonly SimpleColorPicker _text = new SimpleColorPicker { TransparentLabel = UiLanguage.Get("PropertiesAppearanceWindow.RevitText") }, _background = new SimpleColorPicker { TransparentLabel = UiLanguage.Get("PropertiesAppearanceWindow.RevitBackground") };
        private readonly CheckBox _enabled = new CheckBox { Content = UiLanguage.Get("PropertiesAppearanceWindow.UseMyColors"), Margin = new Thickness(0, 12, 0, 6) };
        private readonly CheckBox _live = new CheckBox { Content = UiLanguage.Get("PropertiesAppearanceWindow.LiveTestInRevit"), Margin = new Thickness(0, 8, 0, 4), ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.TemporaryTestClosingWithoutSavingCancelsThisTest") };
        private readonly Expander _savedRules = new Expander { Header = UiLanguage.Get("PropertiesAppearanceWindow.MySavedColors"), Margin = new Thickness(0, 18, 0, 0) };
        private readonly StackPanel _editor = new StackPanel();
        private readonly Button _lightButton, _darkButton;
        private readonly DispatcherTimer _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private bool _darkPreview;
        private readonly List<Tuple<Button, string, string>> _presets = new List<Tuple<Button, string, string>>();

        internal PropertiesAppearanceEditor(AppearanceContext context)
        {
            _context = context; _darkPreview = PropertiesAppearanceService.IsDark;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            foreach (var p in PropertiesAppearanceService.Saved.Profiles) _drafts[p.Key] = p.Copy();
            if (!_drafts.ContainsKey("all")) _drafts["all"] = new AppearanceProfile();
            _enabled.IsChecked = PropertiesAppearanceService.Saved.Enabled;
            _lightButton = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Light"), () => SetTone(false));
            _darkButton = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Dark"), () => SetTone(true));
            _lightButton.MinWidth = _darkButton.MinWidth = 110;
            _appearanceTab = Tab("TabAppearance", false); _rulesTab = Tab("TabRules", true);
            _live.IsChecked = true;
            InstallStyles(); ApplyWindowTheme(); Build(); ReloadContext(context.CategoryKey ?? "all");
            _previewTimer.Tick += (_, __) => { _previewTimer.Stop(); if (!_closed && _live.IsChecked == true) Preview(); };
        }
        internal void Close() { _closed = true; _previewTimer.Stop(); }
        private static SolidColorBrush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        private TextBlock Label(string value, double size = 13, bool bold = false, bool muted = false)
        {
            var t = new TextBlock { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return t;
        }
        private static void BindText(TextBlock t, double size = 13, bool bold = false, bool muted = false)
        {
            t.FontSize = size; t.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal; t.TextWrapping = TextWrapping.Wrap;
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink");
        }
        private Button MakeButton(string label, Action action, bool primary = false)
        {
            var button = new Button { Content = new TextBlock { Text = label, Margin = new Thickness(12, 0, 12, 0), TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(13, 9, 13, 9), Margin = new Thickness(0, 0, 7, 0), MinHeight = 36, Cursor = System.Windows.Input.Cursors.Hand };
            button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
            button.Click += (_, __) => { try { action(); } catch (Exception ex) { Notify(ex.Message); } }; return button;
        }
        private void InstallStyles()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Frame";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(content); template.VisualTree = border; style.Setters.Add(new Setter(Control.TemplateProperty, template));
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(OpacityProperty, .84)); style.Triggers.Add(hover);
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .5)); style.Triggers.Add(disabled);
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#22A875"))); focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2))); style.Triggers.Add(focus);
            Resources[typeof(Button)] = style;
            var combo = new Style(typeof(ComboBox)); combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7))); combo.Setters.Add(new Setter(Control.MinHeightProperty, 36.0)); Resources[typeof(ComboBox)] = combo;
            var check = new Style(typeof(CheckBox)); check.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink"))); Resources[typeof(CheckBox)] = check;
        }
        private void ApplyWindowTheme()
        {
            // Use the same palette as the other Couleur tabs. Preview colors remain independent.
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/" + typeof(SimpleColorPicker).Assembly.GetName().Name + ";component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            Resources["Canvas"] = Resources["App.Background"]; Resources["Ink"] = Resources["Text.Primary"];
            Resources["Muted"] = Resources["Text.Secondary"]; Resources["Line"] = Resources["Border"]; Resources["Tint"] = Resources["Surface.Subtle"];
            SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        }
        private Border Card(UIElement child, int padding = 18)
        {
            var b = new Border { Child = child, Padding = new Thickness(padding) };
            b.SetResourceReference(StyleProperty, "Card"); return b;
        }
        // Framed area inside the page card (no shadow), like the panels of « Arborescence du projet ».
        private Border InnerPanel(UIElement child, bool subtle)
        {
            var b = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
            b.SetResourceReference(Border.BorderBrushProperty, "Line"); b.SetResourceReference(Border.BackgroundProperty, subtle ? "Tint" : "Surface"); return b;
        }
        private void Build()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
            // One large white card, like « Mes panneaux BIMaestro » and « Arborescence du projet »:
            // title and selection on the left, switches on the right, then the useful part.
            var root = new Grid { ClipToBounds = true };
            var page = new Border { Child = root, Margin = new Thickness(0, 0, 4, 4) }; page.SetResourceReference(StyleProperty, "Card"); Content = page;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var groupCreation=new WrapPanel {Margin=new Thickness(0,4,0,8)};
            _groupName.SetResourceReference(Control.ForegroundProperty,"Ink");_groupRenameName.SetResourceReference(Control.ForegroundProperty,"Ink");
            groupCreation.Children.Add(_groupName);groupCreation.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.CreateAGroup"),CreateCustomGroup));
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var heading = new TextBlock { Text = UiLanguage.Get("PropertiesAppearanceWindow.Properties") }; heading.SetResourceReference(StyleProperty, "H2"); title.Children.Add(heading);
            _caption.SetResourceReference(StyleProperty, "Hint"); _caption.Margin = new Thickness(0, 4, 14, 0); _caption.TextWrapping = TextWrapping.NoWrap; _caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _caption.ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.CustomizeThePropertiesPaletteGroupAndParameterColors"); title.Children.Add(_caption); header.Children.Add(title);
            _enabled.Margin = new Thickness(18, 0, 16, 0); _enabled.VerticalAlignment = VerticalAlignment.Center; _enabled.FontWeight = FontWeights.SemiBold; Grid.SetColumn(_enabled, 1); header.Children.Add(_enabled);
            var refresh = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.RefreshSelection"), RefreshContext); refresh.Margin = new Thickness(0); refresh.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(refresh, 2); header.Children.Add(refresh);
            root.Children.Add(header);

            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) }); Grid.SetRow(body, 1); root.Children.Add(body);
            var left = new Grid(); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition());
            var introduction = new StackPanel(); introduction.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.PropertiesPreview"), 16, true)); BindText(_hint, 12, false, true); _hint.Margin = new Thickness(0, 0, 0, 8); introduction.Children.Add(_hint);
            var groupExpander = GroupExpander = new Expander { Header = UiLanguage.Get("PropertiesAppearanceWindow.CreateACustomGroup"), Content = groupCreation, Margin = new Thickness(0, 0, 0, 10) }; introduction.Children.Add(groupExpander); left.Children.Add(introduction);
            var search = new Grid { Margin = new Thickness(0, 0, 0, 14) }; _search.Padding = new Thickness(10, 8, 10, 8); _search.FontSize = 13; _search.SetResourceReference(Control.ForegroundProperty, "Ink"); _search.SetResourceReference(Control.BackgroundProperty, "Surface"); _search.SetResourceReference(Control.BorderBrushProperty, "Line");
            _search.TextChanged += (_, __) => RenderRows(); search.Children.Add(_search);
            var placeholder = Label(UiLanguage.Get("PropertiesAppearanceWindow.SearchForAGroupOrParameter"), 13, false, true); placeholder.Margin = new Thickness(11, 9, 0, 0); placeholder.IsHitTestVisible = false; search.Children.Add(placeholder); _search.TextChanged += (_, __) => placeholder.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            // The preview list is short enough to scan: no search box.
            search.Visibility = Visibility.Collapsed; Grid.SetRow(search, 1); left.Children.Add(search);
            var headings = Pair(Label(UiLanguage.Get("PropertiesAppearanceWindow.ORIGINALREVIT"), 11, true, true), Label(UiLanguage.Get("PropertiesAppearanceWindow.YOURCUSTOMIZATION"), 11, true)); headings.Margin = new Thickness(8, 0, 8, 5); Grid.SetRow(headings, 2); left.Children.Add(headings);
            var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; _previewScroll = scroll; scroll.AllowDrop = true; scroll.PreviewDragOver += (_, e) => AutoScrollWhileDragging(e); headings.AllowDrop = true; headings.PreviewDragOver += (_, e) => { AutoScrollWhileDragging(e); e.Effects = DragDropEffects.None; e.Handled = true; }; Grid.SetRow(scroll, 3); left.Children.Add(scroll); body.Children.Add(InnerPanel(left, false));

            var right = BuildRightPanel();
            _enabled.Checked += (_, __) => EnabledChanged(); _enabled.Unchecked += (_, __) => EnabledChanged();
            _live.Checked += (_, __) => SchedulePreview(); _live.Unchecked += (_, __) => { if (_loading) return; _previewTimer.Stop(); RequestCancelPreview(); };
            var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; var rightCard = InnerPanel(rightScroll, true); Grid.SetColumn(rightCard, 2); body.Children.Add(rightCard);
            var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var messages = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; BindText(_changeState, 13, true); BindText(_status, 12, false, true); _status.Margin = new Thickness(0, 4, 0, 0); messages.Children.Add(_changeState); messages.Children.Add(_status); footer.Children.Add(messages);
            Grid.SetRow(footer, 2); root.Children.Add(footer);
            var confirmation = BuildResetConfirmation(); Grid.SetRowSpan(confirmation, 3); Panel.SetZIndex(confirmation, 10);
            _resetConfirmation.MaxWidth = 680; _resetConfirmation.HorizontalAlignment = HorizontalAlignment.Center; _resetConfirmation.VerticalAlignment = VerticalAlignment.Center; root.Children.Add(confirmation);
            Notify(UiLanguage.Get("PropertiesAppearanceWindow.UseSaveMySettingsToKeepYourChanges"));
        }
        private static Grid Pair(UIElement before, UIElement after)
        {
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.Children.Add(before); Grid.SetColumn(after, 2); g.Children.Add(after); return g;
        }
        private void AddPreset(Grid grid, string name, string text, string background, int row, int col)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) }; content.Children.Add(new TextBlock { Text = "Aa", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brush(text), Margin = new Thickness(0, 0, 10, 0) }); content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(text) });
            var button = MakeButton("", () => ApplyPreset(text, background)); button.MinWidth = 0; button.Content = content; button.Background = Brush(background); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, col == 0 ? 8 : 0, 8);
            Grid.SetRow(button, row); Grid.SetColumn(button, col); grid.Children.Add(button); _presets.Add(Tuple.Create(button, text, background));
        }
        private void ReloadContext(string scopeKey)
        {
            _loading = true; _caption.Text = string.IsNullOrWhiteSpace(_context.CategoryName) ? _context.Caption : UiLanguage.Get("PropertiesAppearanceWindow.CurrentSelection") + _context.Caption; _hint.Text = _context.PreviewHint ?? _context.Caption;
            var choices = new List<ScopeChoice>();
            if (_context.CategoryKey != null) { EnsureProfile(_context.CategoryKey, "Catégorie · " + _context.CategoryName); choices.Add(new ScopeChoice { Key = _context.CategoryKey, Label = UiLanguage.Get("PropertiesAppearanceWindow.EntireCategory") + _context.CategoryName, Explanation = UiLanguage.Get("PropertiesAppearanceWindow.SameAppearanceForAllElementsInThisCategory") }); }
            if (_context.TypeKey != null) { EnsureProfile(_context.TypeKey, "Type · " + _context.TypeName); choices.Add(new ScopeChoice { Key = _context.TypeKey, Label = UiLanguage.Get("PropertiesAppearanceWindow.ThisType") + _context.TypeName, Explanation = UiLanguage.Get("PropertiesAppearanceWindow.SameCategoryAndSameFamilyTypeAcrossYour") }); }
            choices.Add(new ScopeChoice { Key = "all", Label = UiLanguage.Get("PropertiesAppearanceWindow.AllCategories"), Explanation = UiLanguage.Get("PropertiesAppearanceWindow.ThisGroupOrParameterWillBeCustomizedWherever") });
            _scope.ItemsSource = choices; _scope.SelectedItem = choices.FirstOrDefault(c => c.Key == scopeKey) ?? choices[0]; _profile = _drafts[((ScopeChoice)_scope.SelectedItem).Key]; _scopeHint.Text = ((ScopeChoice)_scope.SelectedItem).Explanation;
            var oldKey = _selected?.ParameterKey ?? _selected?.GroupKey;
            _selected = _context.PreviewRows.FirstOrDefault(r => !r.ReadOnly && (r.ParameterKey ?? r.GroupKey) == oldKey) ?? _context.PreviewRows.FirstOrDefault(r => r.Header && !r.ReadOnly && AppearancePreferences.Normalize(r.Label) == "GRAPHISMES") ?? _context.PreviewRows.FirstOrDefault(r => !r.ReadOnly);
            _loading = false; LoadColors(); RenderRows(); RefreshRules(); UpdateState();
        }
        private void EnsureProfile(string key, string name) { if (!_drafts.ContainsKey(key)) _drafts[key] = new AppearanceProfile { Key = key, Name = name }; }
        private void RefreshContext()
        {
            PropertiesAppearanceCommand.Request(app => { if (_closed) return; _context = AppearanceContext.ForEditor(app); _search.Text = ""; ReloadContext(_context.CategoryKey ?? "all"); TutorialAction?.Invoke("context-refreshed"); Notify(UiLanguage.Get("PropertiesAppearanceWindow.SelectionRefreshedYourCurrentColorsAreKept")); });
        }
        private void ScopeChanged()
        {
            if (_loading || !(_scope.SelectedItem is ScopeChoice choice)) return;
            _profile = _drafts[choice.Key]; _scopeHint.Text = choice.Explanation; _scope.ToolTip = choice.Explanation; LoadColors(); RenderRows(); RefreshRules(); if (_rulesShown) { CloseRule(); }
        }
        private AppearanceRule CurrentRule => _selected == null || _profile == null ? null : _profile.Rules.FirstOrDefault(r => r.When == null && r.Kind == (_selected.Header ? "group" : "parameter") && r.TargetKey == (_selected.Header ? _selected.GroupKey : _selected.ParameterKey));
        private PaletteColors Effective(PropertyPreviewRow row)
        {
            var preferences = new AppearancePreferences { Profiles = _drafts.Values.ToList() };
            return row.ReadOnly ? null : preferences.Resolve(_context.CategoryKey, _context.TypeKey, row.GroupKey, row.ParameterKey, _darkPreview, _context.ValuesOf);
        }
        private void Select(PropertyPreviewRow row)
        {
            _selected = row; LoadColors(); RenderRows();
            if (row.ReadOnly) Notify(UiLanguage.Get("PropertiesAppearanceWindow.ThisRowKeepsItsRevitAppearanceItIs"));
        }
        private void SetTone(bool dark) { _darkPreview = dark; LoadColors(); RenderRows(); RefreshRules(); }
        private void LoadColors()
        {
            _loading = true; var c = (_darkPreview ? CurrentRule?.Dark : CurrentRule?.Light) ?? new PaletteColors(); _text.SelectedColor = AppearancePreferences.Parse(c.Text) ?? Colors.Transparent; _background.SelectedColor = AppearancePreferences.Parse(c.Background) ?? Colors.Transparent;
            LoadStyle(); _loading = false;
            _selection.Text = _selected == null ? UiLanguage.Get("PropertiesAppearanceWindow.OpenASelectionInRevit") : (_selected.Header ? UiLanguage.Get("PropertiesAppearanceWindow.Group") : UiLanguage.Get("PropertiesAppearanceWindow.Parameter")) + _selected.Label;
            _selectionHint.Text = _selected == null ? UiLanguage.Get("PropertiesAppearanceWindow.OpenThePropertiesPaletteThenChooseAView") : _selected.ReadOnly ? UiLanguage.Get("PropertiesAppearanceWindow.ThisRowKeepsItsNativeAppearance") : _selected.Header ? UiLanguage.Get("PropertiesAppearanceWindow.TheHeaderAndEditableRowsOfThisGroup") : UiLanguage.Get("PropertiesAppearanceWindow.OnlyThisRowChangesOtherParametersKeepTheir");
            _editor.IsEnabled = _selected != null && !_selected.ReadOnly;
            _lightButton.Background = !_darkPreview ? Brush("#DCEEE3") : (Brush)Resources["Surface"]; _lightButton.Foreground = !_darkPreview ? Brush("#155C42") : (Brush)Resources["Ink"];
            _darkButton.Background = _darkPreview ? Brush("#DCEEE3") : (Brush)Resources["Surface"]; _darkButton.Foreground = _darkPreview ? Brush("#155C42") : (Brush)Resources["Ink"];
            UpdateColorHint();
            RefreshGroupTools();
        }
        private static string Hex(Color? color) => color.HasValue && color.Value.A == 255 ? color.Value.ToString() : null;
        private void ApplyPreset(string foreground, string background)
        {
            if (_selected == null || _selected.ReadOnly) return;
            if (_darkPreview) { string swap = foreground; foreground = background; background = swap; }
            _loading = true; _text.SelectedColor = AppearancePreferences.Parse(foreground); _background.SelectedColor = AppearancePreferences.Parse(background); _loading = false; ColorsChanged();
        }
        private void ColorsChanged()
        {
            if (_loading || _selected == null || _selected.ReadOnly || _profile == null) return;
            var rule = CurrentRule;
            if (rule == null) { rule = new AppearanceRule { Kind = _selected.Header ? "group" : "parameter", TargetKey = _selected.Header ? _selected.GroupKey : _selected.ParameterKey, Label = _selected.Label }; _profile.Rules.Add(rule); }
            var colors = _darkPreview ? rule.Dark : rule.Light; colors.Text = Hex(_text.SelectedColor); colors.Background = Hex(_background.SelectedColor);
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) _profile.Rules.Remove(rule);
            _loading = true; _enabled.IsChecked = true; _loading = false; Changed();
        }
        private WrapPanel Segment(string[] keys, string[] labels, List<Tuple<Button, string>> buttons, Action<string> pick)
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                var b = MakeButton(UiLanguage.Get("PropertiesAppearanceWindow." + labels[i]), () => { pick(key); StyleChanged(); });
                b.MinWidth = 0; b.MinHeight = 30; b.Padding = new Thickness(4, 4, 4, 4); b.Margin = new Thickness(0, 0, 6, 6);
                buttons.Add(Tuple.Create(b, key)); panel.Children.Add(b);
            }
            return panel;
        }
        private CheckBox Option(CheckBox box, string key)
        {
            var text = new TextBlock { Text = UiLanguage.Get("PropertiesAppearanceWindow." + key), TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); box.Content = text;
            box.Checked += (_, __) => StyleChanged(); box.Unchecked += (_, __) => StyleChanged(); return box;
        }
        private void BuildStyleEditor(StackPanel panel)
        {
            panel.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.LayerBackground"), 12, true));
            panel.Children.Add(Segment(new[] { StyleSet.BgNone, StyleSet.BgFill, StyleSet.BgGradient }, new[] { "None", "BackgroundFill", "BackgroundGradient" }, _bgButtons, k => _bgValue = k));
            panel.Children.Add(Option(_textLayer, "LayerText"));
            panel.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.LayerMarker"), 12, true));
            panel.Children.Add(Segment(new[] { StyleSet.MarkNone, StyleSet.MarkStripe, StyleSet.MarkDot, StyleSet.MarkOutline }, new[] { "None", "MarkerStripe", "MarkerDot", "MarkerOutline" }, _markButtons, k => _markValue = k));
            panel.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.LayerIcon"), 12, true));
            var none = new ComboBoxItem { Content = UiLanguage.Get("PropertiesAppearanceWindow.NoIcon"), Tag = "" }; _icon.Items.Add(none);
            foreach (var asset in Couleur.ProjectBrowserIcons.BuiltInIcons)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Image { Source = PaletteIcons.Source(asset.Id), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
                row.Children.Add(new TextBlock { Text = asset.Name, VerticalAlignment = VerticalAlignment.Center });
                _icon.Items.Add(new ComboBoxItem { Content = row, Tag = asset.Id });
            }
            _icon.SelectedIndex = 0; _icon.SelectionChanged += (_, __) => StyleChanged(); panel.Children.Add(_icon);
            _groupOptions.Children.Add(Option(_headerOnly, "HeaderOnly")); _groupOptions.Children.Add(Option(_zebra, "Zebra")); panel.Children.Add(_groupOptions);
            panel.Children.Add(Option(_autoTint, "AutoTint"));
        }
        private void LoadStyle()
        {
            var rule = CurrentRule; var set = AppearanceStyles.Parse(rule?.Style);
            _bgValue = set.Background; _markValue = set.Marker; _textLayer.IsChecked = set.Text;
            _headerOnly.IsChecked = set.HeaderOnly; _zebra.IsChecked = set.Zebra; _autoTint.IsChecked = rule?.AutoTint == true;
            // Header-only and alternate rows only make sense on a group title.
            _groupOptions.Visibility = _selected?.Header == true ? Visibility.Visible : Visibility.Collapsed;
            _icon.SelectedItem = _icon.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (rule?.Icon ?? "")) ?? _icon.Items[0];
            HighlightSegments(); RefreshLooks();
        }
        private static string OperatorName(string op)
        {
            switch (op) { case "=": return "Equals"; case "!=": return "Differs"; case "contains": return "Contains"; case "empty": return "Empty"; case "notempty": return "NotEmpty"; case ">": return "Greater"; default: return "Less"; }
        }
        private void HighlightSegments()
        {
            foreach (var b in _bgButtons.Concat(_markButtons))
            {
                bool on = b.Item2 == (_bgButtons.Contains(b) ? _bgValue : _markValue);
                b.Item1.Background = on ? Brush("#DCEEE3") : (Brush)Resources["Surface"]; b.Item1.Foreground = on ? Brush("#155C42") : (Brush)Resources["Ink"];
                b.Item1.BorderBrush = on ? Brush("#20845D") : (Brush)Resources["Line"];
            }
        }
        private void StyleChanged()
        {
            if (_loading || _selected == null || _selected.ReadOnly || _profile == null) return;
            HighlightSegments();
            var rule = CurrentRule;
            if (rule == null) { rule = new AppearanceRule { Kind = _selected.Header ? "group" : "parameter", TargetKey = _selected.Header ? _selected.GroupKey : _selected.ParameterKey, Label = _selected.Label }; _profile.Rules.Add(rule); }
            var set = new StyleSet { Background = _bgValue, Marker = _markValue, Text = _textLayer.IsChecked == true,
                HeaderOnly = _selected.Header && _headerOnly.IsChecked == true, Zebra = _selected.Header && _zebra.IsChecked == true };
            rule.Style = AppearanceStyles.Store(set);
            rule.Icon = (_icon.SelectedItem as ComboBoxItem)?.Tag as string; if (rule.Icon == "") rule.Icon = null;
            rule.AutoTint = _autoTint.IsChecked == true;
            // A style without any color would be invisible: start from the BIMaestro sage.
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) rule.Light.Text = "#FF155C42";
            _loading = true; _enabled.IsChecked = true; _loading = false; Changed(); LoadColors();
        }
        private void Changed()
        {
            _dirty.Add(_profile.Key); _revision++; RenderRows(); RefreshRules(); UpdateColorHint(); UpdateState(); SchedulePreview();
        }
        private void EnabledChanged() { if (_loading) return; _revision++; UpdateState(); SchedulePreview(); }
        private void UpdateColorHint()
        {
            var effective = _selected == null ? null : Effective(_selected);
            var own = _darkPreview ? CurrentRule?.Dark : CurrentRule?.Light;
            string source = own == null || own.IsEmpty ? effective == null ? UiLanguage.Get("PropertiesAppearanceWindow.OriginalRevitColors") : UiLanguage.Get("PropertiesAppearanceWindow.ColorsInheritedFromAMoreGeneralSetting") : UiLanguage.Get("PropertiesAppearanceWindow.CustomColorsForThisSelection");
            var colors = effective ?? new PaletteColors(); var bg = AppearancePreferences.Parse(colors.Background) ?? (_darkPreview ? Color.FromRgb(59, 68, 83) : Colors.White); var fg = AppearancePreferences.Parse(colors.Text) ?? (_darkPreview ? Colors.White : Colors.Black);
            var mine = CurrentRule == null ? null : CurrentRule.AutoTint ? AppearanceStyles.Tint(CurrentRule, _darkPreview) : own;
            bool sameAsMine = mine != null && effective != null && mine.Text == effective.Text && mine.Background == effective.Background && AppearanceStyles.Parse(CurrentRule.Style).ToString() == AppearanceStyles.Parse(effective.Style).ToString();
            if (own != null && !own.IsEmpty && effective != null && !sameAsMine) source = UiLanguage.Get("PropertiesAppearanceWindow.AMoreSpecificSettingTakesPriorityForThis");
            _colorHint.Text = source + (AppearancePreferences.Contrast(fg, bg) < 4.5 ? UiLanguage.Get("PropertiesAppearanceWindow.TheTextIsAdjustedInThePreviewTo") : "");
            foreach (var preset in _presets)
            {
                string text = _darkPreview ? preset.Item3 : preset.Item2, background = _darkPreview ? preset.Item2 : preset.Item3;
                bool selected = AppearancePreferences.Parse(colors.Text) == AppearancePreferences.Parse(text) && AppearancePreferences.Parse(colors.Background) == AppearancePreferences.Parse(background);
                preset.Item1.BorderBrush = selected ? Brush("#20845D") : Brush("#DDE7E1"); preset.Item1.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }
        private void RenderRows()
        {
            long measured = PropertiesAppearanceMetrics.Begin();
            try { RenderComparison(); } finally { PropertiesAppearanceMetrics.End("EditorRender", measured); }
        }

        private Button PreviewCell(PropertyPreviewRow row, bool modified)
        {
            var nativeBg = _darkPreview ? Color.FromRgb(row.Header ? (byte)44 : (byte)59, row.Header ? (byte)53 : (byte)68, row.Header ? (byte)66 : (byte)83) : row.Header ? Color.FromRgb(233, 237, 234) : Colors.White;
            var colors = modified ? Effective(row) : null;
            var set = colors == null ? new StyleSet { Background = StyleSet.BgNone, Text = false } : AppearanceStyles.Parse(colors.Style);
            var accent = AppearanceStyles.Accent(colors);
            Color chosenBg = AppearancePreferences.Parse(colors?.Background) ?? nativeBg;
            Color bg = set.Background == StyleSet.BgFill ? chosenBg : nativeBg;
            Color nativeFg = _darkPreview ? Colors.White : Color.FromRgb(34, 47, 42);
            bool recolor = set.Text || (set.Background == StyleSet.BgFill && AppearancePreferences.Parse(colors?.Background).HasValue);
            Color fg = row.ReadOnly ? (_darkPreview ? Color.FromRgb(165, 175, 187) : Color.FromRgb(138, 146, 143)) : !recolor ? nativeFg : AppearancePreferences.ReadableText(new PaletteColors { Text = set.Text ? colors.Text : null, Background = bg.ToString() }, _darkPreview);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = (row.Header ? (IsCollapsed(row) ? "›  " : "⌄  ") : "") + row.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(fg), FontWeight = row.Header ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) }; grid.Children.Add(label);
            if (row.Header) Grid.SetColumnSpan(label, 2);
            else { var value = new TextBlock { Text = row.Value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(fg), FontSize = 12 }; Grid.SetColumn(value, 1); grid.Children.Add(value); }
            if (colors?.Icon != null) label.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Image { Source = PaletteIcons.Source(colors.Icon), Width = 14, Height = 14, Margin = new Thickness(7, 0, 0, -2) }));
            if (set.Marker == StyleSet.MarkDot && accent.HasValue) label.Inlines.Add(new System.Windows.Documents.Run("  ●") { Foreground = new SolidColorBrush(accent.Value) });
            UIElement cellContent = grid;
            if (set.Marker == StyleSet.MarkStripe && accent.HasValue)
            {
                var striped = new DockPanel { LastChildFill = true };
                var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(accent.Value), Margin = new Thickness(-4, -2, 6, -2) };
                DockPanel.SetDock(bar, Dock.Left); striped.Children.Add(bar); striped.Children.Add(grid); cellContent = striped;
            }
            if (modified)
            {
                var handleRow = new Grid(); handleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); handleRow.ColumnDefinitions.Add(new ColumnDefinition());
                handleRow.Children.Add(new TextBlock { Text = "⠿", Foreground = new SolidColorBrush(fg), VerticalAlignment = VerticalAlignment.Center, ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.DragToMove") });
                Grid.SetColumn(cellContent, 1); handleRow.Children.Add(cellContent); cellContent = handleRow;
            }
            var numbered = new DockPanel { LastChildFill=true };
            var reference = new TextBlock { Text=_originNumbers.TryGetValue(row,out var origin)?origin:"", MinWidth=38, Margin=new Thickness(8,0,0,0), TextAlignment=TextAlignment.Right, VerticalAlignment=VerticalAlignment.Center, FontSize=11, FontWeight=FontWeights.SemiBold, ToolTip="Position dans l’ordre Revit d’origine" };
            reference.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
            DockPanel.SetDock(reference,Dock.Right);numbered.Children.Add(reference);numbered.Children.Add(cellContent);cellContent=numbered;
            var button = MakeButton("", () => ClickRow(row)); button.Style = (Style)Resources[typeof(Button)]; button.MinWidth = 0; button.Content = cellContent; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(8, 7, 8, 7); button.Margin = new Thickness(0); button.MinHeight = row.Header ? 34 : 32;
            button.Background = set.Background == StyleSet.BgGradient
                ? new LinearGradientBrush(new GradientStopCollection { new GradientStop(chosenBg, 0), new GradientStop(nativeBg, .55) }, new Point(0, .5), new Point(1, .5))
                : (Brush)new SolidColorBrush(bg); button.Foreground = new SolidColorBrush(fg);
            button.ToolTip = row.Label + (row.Header ? UiLanguage.Get("PropertiesAppearanceWindow.EntireGroup") : " : " + row.Value) + (row.ReadOnly ? UiLanguage.Get("PropertiesAppearanceWindow.NativeAppearanceKept") : UiLanguage.Get("PropertiesAppearanceWindow.ClickToCustomize"));
            bool selected = ReferenceEquals(row, _selected) && modified;
            bool outline = set.Marker == StyleSet.MarkOutline && accent.HasValue;
            button.BorderBrush = selected ? Brush("#22A875") : outline ? new SolidColorBrush(accent.Value) : new SolidColorBrush(bg);
            button.BorderThickness = new Thickness(selected ? 2 : outline ? 1.5 : 1);
            if (colors?.Badge != null && accent.HasValue) button.ToolTip = colors.Badge + " · " + button.ToolTip; if (modified) EnableDragging(button, row); return button;
        }
        private void ResetSelection()
        {
            var rule = CurrentRule; if (rule == null) { Notify(UiLanguage.Get("PropertiesAppearanceWindow.ThisRowAlreadyUsesItsOriginalOrInherited")); return; }
            if (_darkPreview) rule.Dark = new PaletteColors(); else rule.Light = new PaletteColors();
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) _profile.Rules.Remove(rule); Changed(); LoadColors(); Notify(UiLanguage.Get("PropertiesAppearanceWindow.ColorsRemovedForThisVariantAMoreGeneral"));
        }
        private void RefreshRules()
        {
            _rules.Children.Clear(); if (_profile == null) return;
            var rules = _profile.Rules.Where(r => !(_darkPreview ? r.Dark : r.Light).IsEmpty).ToArray(); _savedRules.Header = UiLanguage.Get("PropertiesAppearanceWindow.CustomizationsHere") + rules.Length + ")";
            foreach (var rule in rules)
            {
                var b = MakeButton((rule.Kind == "group" ? "▤  " : "—  ") + rule.Label, () => { var row = _context.PreviewRows.FirstOrDefault(r => rule.Kind == "group" ? r.Header && r.GroupKey == rule.TargetKey : r.ParameterKey == rule.TargetKey); if (row != null) { _search.Text = ""; Select(row); } else Notify("Affichez ce groupe dans la palette Revit, puis actualisez la sélection."); });
                b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 5, 0, 0); _rules.Children.Add(b);
            }
            if (rules.Length == 0) _rules.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.NoCustomColorForThisScopeAndTheme"), 12, false, true));
        }
        private AppearanceProfile[] Changes() => _dirty.Select(k => _drafts[k].Copy()).ToArray();
        internal AppearancePreferences CapturePreferences() => new AppearancePreferences {
            Enabled = _enabled.IsChecked == true,
            Profiles = _drafts.Values.Where(p => !p.Layout.IsEmpty || p.Rules.Count > 0).Select(p => p.Copy()).ToList()
        };
        internal void LoadPreferences(AppearancePreferences preferences)
        {
            _previewTimer.Stop();
            _loading = true; _live.IsChecked = false; _loading = false;
            RequestCancelPreview();
            // Empty profiles remove scopes absent from the imported snapshot when it is saved.
            var removedKeys = _drafts.Keys.Concat(PropertiesAppearanceService.Saved.Profiles.Select(p => p.Key)).Distinct().ToArray();
            _drafts.Clear(); _dirty.Clear();
            foreach (var key in removedKeys) _drafts[key] = new AppearanceProfile { Key = key };
            foreach (var profile in preferences.Profiles) _drafts[profile.Key] = profile.Copy();
            if (!_drafts.ContainsKey("all")) _drafts["all"] = new AppearanceProfile();
            foreach (var key in _drafts.Keys) _dirty.Add(key);
            _loading = true; _enabled.IsChecked = preferences.Enabled; _loading = false;
            _revision++; _collapsedGroups.Clear(); _search.Text = "";
            ReloadContext(_context.CategoryKey ?? "all");
            Notify(UiLanguage.Get("PropertiesAppearanceWindow.PropertiesBackupLoadedClickSaveMySettingsTo"));
        }
        internal bool HasChanges => _dirty.Count > 0 || _enabled.IsChecked != PropertiesAppearanceService.Saved.Enabled;
        private void UpdateState()
        {
            bool changed = _dirty.Count > 0 || _enabled.IsChecked != PropertiesAppearanceService.Saved.Enabled;
            _changeState.Text = changed ? UiLanguage.Get("PropertiesAppearanceWindow.UnsavedChanges") : _enabled.IsChecked == true ? UiLanguage.Get("PropertiesAppearanceWindow.YourColorsAreSavedAndEnabled") : UiLanguage.Get("PropertiesAppearanceWindow.ColorsDisabledInRevit");
        }
        private void SchedulePreview() { if (_closed || _live.IsChecked != true) return; _previewTimer.Stop(); _previewTimer.Start(); }
        private void RequestCancelPreview()
        {
            try { PropertiesAppearanceCommand.Request(app => { PropertiesAppearanceService.CancelPreview(); PropertiesAppearanceService.ProcessIdling(app); }); } catch (Exception ex) { Notify(ex.Message); }
        }
        private void Preview()
        {
            var profiles = Changes(); bool enabled = _enabled.IsChecked == true;
            try { PropertiesAppearanceCommand.Request(app => { if (_closed) return; PropertiesAppearanceService.SetPreview(profiles, enabled); PropertiesAppearanceService.ProcessIdling(app); Notify((_darkPreview != PropertiesAppearanceService.IsDark ? UiLanguage.Get("PropertiesAppearanceWindow.InRevitTheActiveThemeDiffersFromThe") : UiLanguage.Get("PropertiesAppearanceWindow.LiveTest")) + PropertiesAppearanceService.Status); }); } catch (Exception ex) { Notify(ex.Message); }
        }
        private void Save() => SaveChanges(null);
        internal void SaveChanges(Action completed, Action<Exception> failed = null)
        {
            _previewTimer.Stop(); var profiles = Changes(); bool enabled = _enabled.IsChecked == true; int revision = _revision;
            PropertiesAppearanceCommand.Request(app =>
            {
                if (_closed) return;
                try
                {
                    PropertiesAppearanceService.Commit(profiles, enabled);
                    if (revision == _revision) _dirty.Clear();
                    PropertiesAppearanceService.ProcessIdling(app); UpdateState();
                    Notify(UiLanguage.Get("PropertiesAppearanceWindow.DisplaySaved") + PropertiesAppearanceService.Status); completed?.Invoke();
                }
                catch (Exception ex) { if (failed == null) throw; failed(ex); }
            });
        }
        internal void Notify(string message) { _status.Text = message; }
    }
}
