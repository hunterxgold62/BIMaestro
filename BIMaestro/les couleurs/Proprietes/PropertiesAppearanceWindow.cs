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
        private readonly SimpleColorPicker _text = new SimpleColorPicker { TransparentLabel = UiLanguage.T("Texte Revit", "Revit text") }, _background = new SimpleColorPicker { TransparentLabel = UiLanguage.T("Fond Revit", "Revit background") };
        private readonly CheckBox _enabled = new CheckBox { Content = UiLanguage.T("Utiliser mes couleurs", "Use my colors"), Margin = new Thickness(0, 12, 0, 6) };
        private readonly CheckBox _live = new CheckBox { Content = UiLanguage.T("Tester en direct dans Revit", "Live test in Revit"), Margin = new Thickness(0, 8, 0, 4), ToolTip = UiLanguage.T("Essai temporaire. Fermer sans enregistrer annule cet essai.", "Temporary test. Closing without saving cancels this test.") };
        private readonly Expander _savedRules = new Expander { Header = UiLanguage.T("Mes couleurs enregistrées", "My saved colors"), Margin = new Thickness(0, 18, 0, 0) };
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
            _lightButton = MakeButton(UiLanguage.T("☀  Clair", "☀  Light"), () => SetTone(false));
            _darkButton = MakeButton(UiLanguage.T("☾  Sombre", "☾  Dark"), () => SetTone(true));
            _lightButton.MinWidth = _darkButton.MinWidth = 110;
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
        private void Build()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
            var root = new Grid { Margin = new Thickness(0, 8, 4, 4), ClipToBounds = true }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new StackPanel(); title.Children.Add(Label(UiLanguage.T("Propriétés", "Properties"), 14, true));
            title.Children.Add(Label(UiLanguage.T("Personnalisez la palette Propriétés : couleurs des groupes et paramètres, profils et ordre d’affichage.", "Customize the Properties palette: group and parameter colors, profiles and display order."), 12, false, true)); root.Children.Add(title);
            var groupCreation=new WrapPanel {Margin=new Thickness(0,4,0,8)};
            _groupName.SetResourceReference(Control.ForegroundProperty,"Ink");_groupRenameName.SetResourceReference(Control.ForegroundProperty,"Ink");
            groupCreation.Children.Add(_groupName);groupCreation.Children.Add(MakeButton(UiLanguage.T("＋  Créer un groupe", "＋  Create a group"),CreateCustomGroup));
            var context = new Grid(); context.ColumnDefinitions.Add(new ColumnDefinition()); context.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); context.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            BindText(_caption, 13, true); _caption.TextWrapping = TextWrapping.NoWrap; _caption.TextTrimming = TextTrimming.CharacterEllipsis; _caption.VerticalAlignment = VerticalAlignment.Center; _caption.Margin = new Thickness(0, 0, 14, 0); context.Children.Add(_caption);
            _enabled.Margin = new Thickness(0, 0, 16, 0); _enabled.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_enabled, 1); context.Children.Add(_enabled);
            var refresh = MakeButton(UiLanguage.T("↻  Actualiser la sélection", "↻  Refresh selection"), RefreshContext); Grid.SetColumn(refresh, 2); context.Children.Add(refresh);
            var contextCard = Card(context, 12); contextCard.Margin = new Thickness(0, 4, 0, 12); Grid.SetRow(contextCard, 1); root.Children.Add(contextCard);

            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); Grid.SetRow(body, 2); root.Children.Add(body);
            var left = new Grid(); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition());
            var introduction = new StackPanel(); introduction.Children.Add(Label(UiLanguage.T("Aperçu des propriétés", "Properties preview"), 16, true)); BindText(_hint, 12, false, true); _hint.Margin = new Thickness(0, 0, 0, 8); introduction.Children.Add(_hint);
            var groupExpander = GroupExpander = new Expander { Header = UiLanguage.T("Créer un groupe personnalisé", "Create a custom group"), Content = groupCreation, Margin = new Thickness(0, 0, 0, 10) }; introduction.Children.Add(groupExpander); left.Children.Add(introduction);
            var search = new Grid { Margin = new Thickness(0, 0, 0, 14) }; _search.Padding = new Thickness(10, 8, 10, 8); _search.FontSize = 13; _search.SetResourceReference(Control.ForegroundProperty, "Ink"); _search.SetResourceReference(Control.BackgroundProperty, "Surface"); _search.SetResourceReference(Control.BorderBrushProperty, "Line");
            _search.TextChanged += (_, __) => RenderRows(); search.Children.Add(_search);
            var placeholder = Label(UiLanguage.T("⌕  Rechercher un groupe ou un paramètre…", "⌕  Search for a group or parameter…"), 13, false, true); placeholder.Margin = new Thickness(11, 9, 0, 0); placeholder.IsHitTestVisible = false; search.Children.Add(placeholder); _search.TextChanged += (_, __) => placeholder.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(search, 1); left.Children.Add(search);
            var headings = Pair(Label(UiLanguage.T("REVIT D’ORIGINE", "ORIGINAL REVIT"), 11, true, true), Label(UiLanguage.T("VOTRE PERSONNALISATION", "YOUR CUSTOMIZATION"), 11, true)); headings.Margin = new Thickness(8, 0, 8, 5); Grid.SetRow(headings, 2); left.Children.Add(headings);
            var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; _previewScroll = scroll; scroll.AllowDrop = true; scroll.PreviewDragOver += (_, e) => AutoScrollWhileDragging(e); headings.AllowDrop = true; headings.PreviewDragOver += (_, e) => { AutoScrollWhileDragging(e); e.Effects = DragDropEffects.None; e.Handled = true; }; Grid.SetRow(scroll, 3); left.Children.Add(scroll); body.Children.Add(Card(left));

            var right = new StackPanel(); right.Children.Add(Label(UiLanguage.T("Personnaliser la sélection", "Customize the selection"), 16, true)); BindText(_selection, 15, true); _selection.Margin = new Thickness(0, 8, 0, 6); right.Children.Add(_selection);
            BindText(_selectionHint, 12, false, true); _selectionHint.Margin = new Thickness(0, 0, 0, 15); right.Children.Add(_selectionHint);
            right.Children.Add(_groupTools);
            right.Children.Add(Label(UiLanguage.T("Appliquer à…", "Apply to…"), 13, true)); _scope.SelectionChanged += (_, __) => ScopeChanged(); right.Children.Add(_scope);
            BindText(_scopeHint, 12, false, true); _scopeHint.Margin = new Thickness(0, 7, 0, 14); right.Children.Add(_scopeHint);
            var tones = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) }; tones.Children.Add(_lightButton); tones.Children.Add(_darkButton); right.Children.Add(tones);
            _editor.Children.Add(Label(UiLanguage.T("Une ambiance en un clic", "One-click theme"), 12, true));
            var presets = new Grid(); presets.ColumnDefinitions.Add(new ColumnDefinition()); presets.ColumnDefinitions.Add(new ColumnDefinition()); presets.RowDefinitions.Add(new RowDefinition()); presets.RowDefinitions.Add(new RowDefinition());
            AddPreset(presets, UiLanguage.T("Sauge", "Sage"), "#155C42", "#E4F1E9", 0, 0); AddPreset(presets, UiLanguage.T("Bleu", "Blue"), "#234F78", "#E7F1FB", 0, 1);
            AddPreset(presets, UiLanguage.T("Ambre", "Amber"), "#77521A", "#FBF1D9", 1, 0); AddPreset(presets, UiLanguage.T("Lavande", "Lavender"), "#604483", "#F1EBFA", 1, 1); _editor.Children.Add(presets);
            var custom = new Expander { Header = UiLanguage.T("Choisir mes propres couleurs", "Choose my own colors"), Margin = new Thickness(0, 14, 0, 12) };
            var customContent = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; customContent.Children.Add(Label(UiLanguage.T("Couleur du texte", "Text color"), 12)); _text.Height = 34; customContent.Children.Add(_text); customContent.Children.Add(new Border { Height = 10 }); customContent.Children.Add(Label(UiLanguage.T("Couleur du fond", "Background color"), 12)); _background.Height = 34; customContent.Children.Add(_background); custom.Content = customContent; _editor.Children.Add(custom);
            _text.SelectedColorChanged += (_, __) => ColorsChanged(); _background.SelectedColorChanged += (_, __) => ColorsChanged();
            BindText(_colorHint, 12, false, true); _colorHint.Margin = new Thickness(0, 0, 0, 10); _editor.Children.Add(_colorHint);
            _editor.Children.Add(MakeButton(UiLanguage.T("↶  Retirer mes couleurs ici", "↶  Remove my colors here"), ResetSelection));
            right.Children.Add(_editor);
            _live.Margin = new Thickness(0, 14, 0, 12); right.Children.Add(_live);
            _enabled.Checked += (_, __) => EnabledChanged(); _enabled.Unchecked += (_, __) => EnabledChanged();
            _live.Checked += (_, __) => SchedulePreview(); _live.Unchecked += (_, __) => { if (_loading) return; _previewTimer.Stop(); RequestCancelPreview(); };
            _savedRules.Content = _rules; right.Children.Add(_savedRules); right.Children.Add(MakeButton(UiLanguage.T("↶  Remettre l’ordre Revit", "↶  Restore Revit order"), ResetOrder)); right.Children.Add(MakeButton(UiLanguage.T("Réinitialiser tout…", "Reset all…"), () => _resetConfirmation.Visibility = Visibility.Visible));
            var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; var rightCard = Card(rightScroll); Grid.SetColumn(rightCard, 2); body.Children.Add(rightCard);
            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var messages = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; BindText(_changeState, 13, true); BindText(_status, 12, false, true); _status.Margin = new Thickness(0, 4, 0, 0); messages.Children.Add(_changeState); messages.Children.Add(_status); footer.Children.Add(messages);
            Grid.SetRow(footer, 3); root.Children.Add(footer);
            var confirmation = BuildResetConfirmation(); Grid.SetRowSpan(confirmation, 4); Panel.SetZIndex(confirmation, 10);
            _resetConfirmation.MaxWidth = 680; _resetConfirmation.HorizontalAlignment = HorizontalAlignment.Center; _resetConfirmation.VerticalAlignment = VerticalAlignment.Center; root.Children.Add(confirmation);
            Notify(UiLanguage.T("Utilisez « Enregistrer mes réglages » pour conserver vos modifications. Fermer sans enregistrer annule vos essais.", "Use “Save my settings” to keep your changes. Closing without saving cancels your tests."));
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
            _loading = true; _caption.Text = string.IsNullOrWhiteSpace(_context.CategoryName) ? _context.Caption : UiLanguage.T("Sélection actuelle : ", "Current selection:") + _context.Caption; _hint.Text = _context.PreviewHint ?? _context.Caption;
            var choices = new List<ScopeChoice>();
            if (_context.CategoryKey != null) { EnsureProfile(_context.CategoryKey, "Catégorie · " + _context.CategoryName); choices.Add(new ScopeChoice { Key = _context.CategoryKey, Label = UiLanguage.T("Toute la catégorie : ", "Entire category:") + _context.CategoryName, Explanation = UiLanguage.T("Même apparence pour tous les éléments de cette catégorie, dans vos projets.", "Same appearance for all elements in this category, across your projects.") }); }
            if (_context.TypeKey != null) { EnsureProfile(_context.TypeKey, "Type · " + _context.TypeName); choices.Add(new ScopeChoice { Key = _context.TypeKey, Label = UiLanguage.T("Ce type : ", "This type:") + _context.TypeName, Explanation = UiLanguage.T("Même catégorie et même famille/type, dans vos projets. Les autres types gardent leurs couleurs.", "Same category and same family/type, across your projects. Other types keep their colors.") }); }
            choices.Add(new ScopeChoice { Key = "all", Label = UiLanguage.T("Toutes les catégories", "All categories"), Explanation = UiLanguage.T("Ce groupe ou ce paramètre sera personnalisé partout où il est reconnu.", "This group or parameter will be customized wherever it is recognized.") });
            _scope.ItemsSource = choices; _scope.SelectedItem = choices.FirstOrDefault(c => c.Key == scopeKey) ?? choices[0]; _profile = _drafts[((ScopeChoice)_scope.SelectedItem).Key]; _scopeHint.Text = ((ScopeChoice)_scope.SelectedItem).Explanation;
            var oldKey = _selected?.ParameterKey ?? _selected?.GroupKey;
            _selected = _context.PreviewRows.FirstOrDefault(r => !r.ReadOnly && (r.ParameterKey ?? r.GroupKey) == oldKey) ?? _context.PreviewRows.FirstOrDefault(r => r.Header && !r.ReadOnly && AppearancePreferences.Normalize(r.Label) == "GRAPHISMES") ?? _context.PreviewRows.FirstOrDefault(r => !r.ReadOnly);
            _loading = false; LoadColors(); RenderRows(); RefreshRules(); UpdateState();
        }
        private void EnsureProfile(string key, string name) { if (!_drafts.ContainsKey(key)) _drafts[key] = new AppearanceProfile { Key = key, Name = name }; }
        private void RefreshContext()
        {
            PropertiesAppearanceCommand.Request(app => { if (_closed) return; _context = AppearanceContext.ForEditor(app); _search.Text = ""; ReloadContext(_context.CategoryKey ?? "all"); TutorialAction?.Invoke("context-refreshed"); Notify(UiLanguage.T("Sélection actualisée. Vos couleurs en cours sont conservées.", "Selection refreshed. Your current colors are kept.")); });
        }
        private void ScopeChanged()
        {
            if (_loading || !(_scope.SelectedItem is ScopeChoice choice)) return;
            _profile = _drafts[choice.Key]; _scopeHint.Text = choice.Explanation; LoadColors(); RenderRows(); RefreshRules();
        }
        private AppearanceRule CurrentRule => _selected == null || _profile == null ? null : _profile.Rules.FirstOrDefault(r => r.Kind == (_selected.Header ? "group" : "parameter") && r.TargetKey == (_selected.Header ? _selected.GroupKey : _selected.ParameterKey));
        private PaletteColors Effective(PropertyPreviewRow row)
        {
            var preferences = new AppearancePreferences { Profiles = _drafts.Values.ToList() };
            return row.ReadOnly ? null : preferences.Resolve(_context.CategoryKey, _context.TypeKey, row.GroupKey, row.ParameterKey, _darkPreview);
        }
        private void Select(PropertyPreviewRow row)
        {
            _selected = row; LoadColors(); RenderRows();
            if (row.ReadOnly) Notify(UiLanguage.T("Cette ligne reste dans son apparence Revit : elle est grisée, non modifiable ou non reconnue.", "This row keeps its Revit appearance: it is grayed out, read-only or not recognized."));
        }
        private void SetTone(bool dark) { _darkPreview = dark; LoadColors(); RenderRows(); RefreshRules(); }
        private void LoadColors()
        {
            _loading = true; var c = (_darkPreview ? CurrentRule?.Dark : CurrentRule?.Light) ?? new PaletteColors(); _text.SelectedColor = AppearancePreferences.Parse(c.Text) ?? Colors.Transparent; _background.SelectedColor = AppearancePreferences.Parse(c.Background) ?? Colors.Transparent; _loading = false;
            _selection.Text = _selected == null ? UiLanguage.T("Ouvrez une sélection dans Revit", "Open a selection in Revit") : (_selected.Header ? UiLanguage.T("Groupe : ", "Group:") : UiLanguage.T("Paramètre : ", "Parameter:")) + _selected.Label;
            _selectionHint.Text = _selected == null ? UiLanguage.T("Ouvrez la palette Propriétés, puis choisissez une vue ou un seul élément.", "Open the Properties palette, then choose a view or a single element.") : _selected.ReadOnly ? UiLanguage.T("Cette ligne conserve son apparence native.", "This row keeps its native appearance.") : _selected.Header ? UiLanguage.T("Le titre et les lignes modifiables de ce groupe changeront ensemble.", "The header and editable rows of this group will change together.") : UiLanguage.T("Seule cette ligne change. Les autres paramètres gardent leurs couleurs.", "Only this row changes. Other parameters keep their colors.");
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
        private void Changed()
        {
            _dirty.Add(_profile.Key); _revision++; RenderRows(); RefreshRules(); UpdateColorHint(); UpdateState(); SchedulePreview();
        }
        private void EnabledChanged() { if (_loading) return; _revision++; UpdateState(); SchedulePreview(); }
        private void UpdateColorHint()
        {
            var effective = _selected == null ? null : Effective(_selected);
            var own = _darkPreview ? CurrentRule?.Dark : CurrentRule?.Light;
            string source = own == null || own.IsEmpty ? effective == null ? UiLanguage.T("Couleurs Revit d’origine.", "Original Revit colors.") : UiLanguage.T("Couleurs héritées d’un réglage plus général.", "Colors inherited from a more general setting.") : UiLanguage.T("Couleurs personnalisées pour cette sélection.", "Custom colors for this selection.");
            var colors = effective ?? new PaletteColors(); var bg = AppearancePreferences.Parse(colors.Background) ?? (_darkPreview ? Color.FromRgb(59, 68, 83) : Colors.White); var fg = AppearancePreferences.Parse(colors.Text) ?? (_darkPreview ? Colors.White : Colors.Black);
            if (own != null && !own.IsEmpty && effective != null && !ReferenceEquals(own, effective)) source = UiLanguage.T("Un réglage plus précis reste prioritaire pour cet élément.", "A more specific setting takes priority for this element.");
            _colorHint.Text = source + (AppearancePreferences.Contrast(fg, bg) < 4.5 ? UiLanguage.T(" Le texte est ajusté dans l’aperçu pour rester lisible.", " The text is adjusted in the preview to stay readable.") : "");
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
            var colors = modified ? Effective(row) : null; Color bg = AppearancePreferences.Parse(colors?.Background) ?? nativeBg;
            Color fg = row.ReadOnly ? (_darkPreview ? Color.FromRgb(165, 175, 187) : Color.FromRgb(138, 146, 143)) : colors == null ? (_darkPreview ? Colors.White : Color.FromRgb(34, 47, 42)) : AppearancePreferences.ReadableText(new PaletteColors { Text = colors.Text, Background = bg.ToString() }, _darkPreview);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = (row.Header ? (IsCollapsed(row) ? "›  " : "⌄  ") : "") + row.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(fg), FontWeight = row.Header ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) }; grid.Children.Add(label);
            if (row.Header) Grid.SetColumnSpan(label, 2);
            else { var value = new TextBlock { Text = row.Value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(fg), FontSize = 12 }; Grid.SetColumn(value, 1); grid.Children.Add(value); }
            UIElement cellContent = grid;
            if (modified)
            {
                var handleRow = new Grid(); handleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); handleRow.ColumnDefinitions.Add(new ColumnDefinition());
                handleRow.Children.Add(new TextBlock { Text = "⠿", Foreground = new SolidColorBrush(fg), VerticalAlignment = VerticalAlignment.Center, ToolTip = UiLanguage.T("Glisser pour déplacer", "Drag to move") });
                Grid.SetColumn(grid, 1); handleRow.Children.Add(grid); cellContent = handleRow;
            }
            var numbered = new DockPanel { LastChildFill=true };
            var reference = new TextBlock { Text=_originNumbers.TryGetValue(row,out var origin)?origin:"", MinWidth=38, Margin=new Thickness(8,0,0,0), TextAlignment=TextAlignment.Right, VerticalAlignment=VerticalAlignment.Center, FontSize=11, FontWeight=FontWeights.SemiBold, ToolTip="Position dans l’ordre Revit d’origine" };
            reference.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
            DockPanel.SetDock(reference,Dock.Right);numbered.Children.Add(reference);numbered.Children.Add(cellContent);cellContent=numbered;
            var button = MakeButton("", () => ClickRow(row)); button.Style = (Style)Resources[typeof(Button)]; button.MinWidth = 0; button.Content = cellContent; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(8, 7, 8, 7); button.Margin = new Thickness(0); button.MinHeight = row.Header ? 34 : 32; button.Background = new SolidColorBrush(bg); button.Foreground = new SolidColorBrush(fg);
            button.ToolTip = row.Label + (row.Header ? UiLanguage.T(" · Tout le groupe", " · Entire group") : " : " + row.Value) + (row.ReadOnly ? UiLanguage.T(" · Apparence native conservée", " · Native appearance kept") : UiLanguage.T(" · Cliquer pour personnaliser", " · Click to customize"));
            bool selected = ReferenceEquals(row, _selected) && modified; button.BorderBrush = selected ? Brush("#22A875") : new SolidColorBrush(bg); button.BorderThickness = new Thickness(selected ? 2 : 1); if (modified) EnableDragging(button, row); return button;
        }
        private void ResetSelection()
        {
            var rule = CurrentRule; if (rule == null) { Notify(UiLanguage.T("Cette ligne utilise déjà ses couleurs d’origine ou des couleurs héritées.", "This row already uses its original or inherited colors.")); return; }
            if (_darkPreview) rule.Dark = new PaletteColors(); else rule.Light = new PaletteColors();
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) _profile.Rules.Remove(rule); Changed(); LoadColors(); Notify(UiLanguage.T("Couleurs retirées pour cette variante. Un réglage plus général peut rester applicable.", "Colors removed for this variant. A more general setting may still apply."));
        }
        private void RefreshRules()
        {
            _rules.Children.Clear(); if (_profile == null) return;
            var rules = _profile.Rules.Where(r => !(_darkPreview ? r.Dark : r.Light).IsEmpty).ToArray(); _savedRules.Header = UiLanguage.T("Personnalisations ici (", "Customizations here (") + rules.Length + ")";
            foreach (var rule in rules)
            {
                var b = MakeButton((rule.Kind == "group" ? "▤  " : "—  ") + rule.Label, () => { var row = _context.PreviewRows.FirstOrDefault(r => rule.Kind == "group" ? r.Header && r.GroupKey == rule.TargetKey : r.ParameterKey == rule.TargetKey); if (row != null) { _search.Text = ""; Select(row); } else Notify("Affichez ce groupe dans la palette Revit, puis actualisez la sélection."); });
                b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 5, 0, 0); _rules.Children.Add(b);
            }
            if (rules.Length == 0) _rules.Children.Add(Label(UiLanguage.T("Aucune couleur personnalisée pour cette portée et ce thème.", "No custom color for this scope and theme."), 12, false, true));
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
            Notify(UiLanguage.T("Sauvegarde des propriétés chargée. Cliquez sur « Enregistrer mes réglages » pour l’appliquer.", "Properties backup loaded. Click “Save my settings” to apply it."));
        }
        internal bool HasChanges => _dirty.Count > 0 || _enabled.IsChecked != PropertiesAppearanceService.Saved.Enabled;
        private void UpdateState()
        {
            bool changed = _dirty.Count > 0 || _enabled.IsChecked != PropertiesAppearanceService.Saved.Enabled;
            _changeState.Text = changed ? UiLanguage.T("●  Modifications non enregistrées", "●  Unsaved changes") : _enabled.IsChecked == true ? UiLanguage.T("✓  Vos couleurs sont enregistrées et activées", "✓  Your colors are saved and enabled") : UiLanguage.T("Couleurs désactivées dans Revit", "Colors disabled in Revit");
        }
        private void SchedulePreview() { if (_closed || _live.IsChecked != true) return; _previewTimer.Stop(); _previewTimer.Start(); }
        private void RequestCancelPreview()
        {
            try { PropertiesAppearanceCommand.Request(app => { PropertiesAppearanceService.CancelPreview(); PropertiesAppearanceService.ProcessIdling(app); }); } catch (Exception ex) { Notify(ex.Message); }
        }
        private void Preview()
        {
            var profiles = Changes(); bool enabled = _enabled.IsChecked == true;
            try { PropertiesAppearanceCommand.Request(app => { if (_closed) return; PropertiesAppearanceService.SetPreview(profiles, enabled); PropertiesAppearanceService.ProcessIdling(app); Notify((_darkPreview != PropertiesAppearanceService.IsDark ? UiLanguage.T("Dans Revit, le thème actif est différent de celui de l’aperçu. ", "In Revit, the active theme differs from the preview theme.") : UiLanguage.T("Test en direct · ", "Live test ·")) + PropertiesAppearanceService.Status); }); } catch (Exception ex) { Notify(ex.Message); }
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
                    Notify(UiLanguage.T("Affichage enregistré. ", "Display saved.") + PropertiesAppearanceService.Status); completed?.Invoke();
                }
                catch (Exception ex) { if (failed == null) throw; failed(ex); }
            });
        }
        internal void Notify(string message) { _status.Text = message; }
    }
}
