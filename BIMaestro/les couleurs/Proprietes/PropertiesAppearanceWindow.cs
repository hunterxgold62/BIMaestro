using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMaestro.UI;

namespace BIMaestro.PropertiesAppearance
{
    internal sealed class PropertiesAppearanceWindow : Window
    {
        private AppearanceContext _context;
        private readonly Dictionary<string, AppearanceProfile> _drafts = new Dictionary<string, AppearanceProfile>();
        private readonly HashSet<string> _dirty = new HashSet<string>();
        private AppearanceProfile _profile;
        private bool _loading;
        private int _revision;
        private readonly ListBox _profiles = new ListBox(), _rules = new ListBox();
        private readonly ComboBox _kind = new ComboBox(), _target = new ComboBox(), _tone = new ComboBox();
        private readonly SimpleColorPicker _text = new SimpleColorPicker { TransparentLabel = "Couleur Revit" }, _background = new SimpleColorPicker { TransparentLabel = "Fond Revit" };
        private readonly CheckBox _enabled = new CheckBox { Content = "Activer mes couleurs dans la palette Propriétés", Margin = new Thickness(0, 5, 0, 14) };
        private readonly TextBlock _caption = new TextBlock { TextWrapping = TextWrapping.Wrap }, _status = new TextBlock { TextWrapping = TextWrapping.Wrap }, _scope = new TextBlock { TextWrapping = TextWrapping.Wrap }, _previewTitle = new TextBlock { FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 8, 12, 8) };
        private readonly Border _preview = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 6) };
        private readonly StackPanel _previewRows = new StackPanel();

        internal PropertiesAppearanceWindow(AppearanceContext context)
        {
            _context = context;
            Title = "Apparence des propriétés"; Width = 1090; Height = 760; MinWidth = 960; MinHeight = 650;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "App.Background");
            SetResourceReference(ForegroundProperty, "Text.Primary");
            foreach (var p in PropertiesAppearanceService.Saved.Profiles) _drafts[p.Key] = p.Copy();
            if (!_drafts.ContainsKey("all")) _drafts["all"] = new AppearanceProfile();
            _enabled.IsChecked = PropertiesAppearanceService.Saved.Enabled;
            Build(); ReloadProfiles("all"); ApplyWindowTheme(); Activated += (_, __) => ApplyWindowTheme();
        }

        private static TextBlock Text(string value, double size = 14, bool bold = false)
        {
            var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); return t;
        }
        private static Button Button(string label, Action action, bool primary = false)
        {
            var b = new Button { Content = label, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 8, 0), MinHeight = 34 };
            if (primary) { b.Background = new SolidColorBrush(Color.FromRgb(15, 81, 50)); b.Foreground = Brushes.White; }
            b.Click += (_, __) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Apparence des propriétés", MessageBoxButton.OK, MessageBoxImage.Information); } };
            return b;
        }
        private static Border Card(UIElement child)
        {
            var card = new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = child };
            card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Border"); return card;
        }
        private void Build()
        {
            var root = new Grid { Margin = new Thickness(22) }; Content = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(Text("Apparence des propriétés", 26, true));
            heading.Children.Add(Text("Choisissez où appliquer vos couleurs, puis un groupe ou un paramètre. Les valeurs et unités du modèle restent inchangées."));
            heading.Children.Add(_enabled); root.Children.Add(heading);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); body.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(body, 1); root.Children.Add(body);

            var left = new Grid(); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition()); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var context = new StackPanel(); context.Children.Add(Text("Où appliquer ces réglages ?", 17, true)); context.Children.Add(_caption);
            context.Children.Add(Button("Actualiser la sélection", () => PropertiesAppearanceCommand.Request(app => { _context = AppearanceContext.Capture(app); ReloadProfiles(_profile?.Key); Notify("Contexte actualisé. Les règles non enregistrées sont conservées."); })));
            context.Children.Add(Text("", 5)); left.Children.Add(context); _profiles.SelectionChanged += ProfileChanged;
            Grid.SetRow(_profiles, 1); left.Children.Add(_profiles);
            var scopes = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            scopes.Children.Add(Button("+ Cette catégorie", () => AddScope(false)));
            scopes.Children.Add(new Border { Height = 8 }); scopes.Children.Add(Button("+ Ce type", () => AddScope(true)));
            scopes.Children.Add(Text("Catégorie : tous ses éléments. Type : même catégorie et même nom de famille/type, dans vos projets.", 12)); Grid.SetRow(scopes, 2); left.Children.Add(scopes);
            body.Children.Add(Card(left));

            var right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); right.RowDefinitions.Add(new RowDefinition());
            var editor = new StackPanel(); editor.Children.Add(_scope); editor.Children.Add(new Border { Height = 10 });
            var targetRow = new Grid(); targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); targetRow.ColumnDefinitions.Add(new ColumnDefinition());
            _kind.Items.Add("Tout un groupe"); _kind.Items.Add("Un paramètre"); _kind.SelectedIndex = 0; _kind.Margin = new Thickness(0, 0, 10, 0); _kind.SelectionChanged += (_, __) => LoadTargets();
            targetRow.Children.Add(_kind); _target.MinWidth = 230; Grid.SetColumn(_target, 1); targetRow.Children.Add(_target); _target.SelectionChanged += (_, __) => LoadColors(); editor.Children.Add(targetRow);
            var tones = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 10) }; tones.Children.Add(Text("Variante à régler", 14, true));
            _tone.Items.Add("Revit clair"); _tone.Items.Add("Revit sombre"); _tone.SelectedIndex = PropertiesAppearanceService.IsDark ? 1 : 0; _tone.MinWidth = 150; _tone.Margin = new Thickness(16, -3, 0, 0); _tone.SelectionChanged += (_, __) => LoadColors(); tones.Children.Add(_tone); editor.Children.Add(tones);
            var colors = new Grid(); colors.ColumnDefinitions.Add(new ColumnDefinition()); colors.ColumnDefinitions.Add(new ColumnDefinition());
            var textBox = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; textBox.Children.Add(Text("Texte")); textBox.Children.Add(_text); colors.Children.Add(textBox);
            var bgBox = new StackPanel(); bgBox.Children.Add(Text("Fond")); bgBox.Children.Add(_background); Grid.SetColumn(bgBox, 1); colors.Children.Add(bgBox); editor.Children.Add(colors);
            _text.SelectedColorChanged += (_, __) => ColorsChanged(); _background.SelectedColorChanged += (_, __) => ColorsChanged();
            var sample = new StackPanel(); sample.Children.Add(_previewTitle); sample.Children.Add(_previewRows); _preview.Child = sample; editor.Children.Add(_preview);
            editor.Children.Add(Text("Aperçu indicatif. Le contraste est corrigé si nécessaire. Les champs grisés et les éditeurs en cours de saisie gardent leur apparence Revit.", 12));
            var editButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 12) };
            editButtons.Children.Add(Button("Retirer cette règle", RemoveRule)); editButtons.Children.Add(Button("Vider ce profil", ClearProfile)); editor.Children.Add(editButtons); right.Children.Add(editor);
            var rulesPanel = new Grid(); rulesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); rulesPanel.RowDefinitions.Add(new RowDefinition()); rulesPanel.Children.Add(Text("Règles de ce profil", 14, true)); _rules.DisplayMemberPath = "Label"; Grid.SetRow(_rules, 1); rulesPanel.Children.Add(_rules); Grid.SetRow(rulesPanel, 1); right.Children.Add(rulesPanel);
            _rules.SelectionChanged += (_, __) => SelectRule(); var rightCard = Card(right); Grid.SetColumn(rightCard, 2); body.Children.Add(rightCard);

            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status.VerticalAlignment = VerticalAlignment.Center; _status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); footer.Children.Add(_status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(Button("Aperçu dans Revit", Preview)); actions.Children.Add(Button("Enregistrer et appliquer", Save, true)); actions.Children.Add(Button("Fermer", Close)); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
            Notify("Aucune modification n’est appliquée avant « Aperçu » ou « Enregistrer ». Fermer annule l’aperçu non enregistré.");
        }
        private void AddScope(bool type)
        {
            string key = type ? _context.TypeKey : _context.CategoryKey;
            if (key == null) { Notify("Sélectionnez un seul élément ou une vue dans Revit, puis actualisez la sélection."); return; }
            if (!_drafts.ContainsKey(key)) _drafts[key] = new AppearanceProfile { Key = key, Name = type ? "Type · " + _context.TypeName : "Catégorie · " + _context.CategoryName };
            ReloadProfiles(key);
        }
        private void ReloadProfiles(string key)
        {
            _caption.Text = _context.Caption; _profiles.ItemsSource = null; _profiles.ItemsSource = _drafts.Values.OrderBy(p => p.Key == "all" ? 0 : 1).ThenBy(p => p.Name).ToList();
            _profiles.SelectedItem = _drafts.TryGetValue(key ?? "all", out var profile) ? profile : _drafts["all"];
        }
        private void ProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            _profile = _profiles.SelectedItem as AppearanceProfile; if (_profile == null) return;
            _scope.Text = _profile.Name + "\nPriorité : type, puis catégorie, puis réglages généraux."; LoadTargets(); RefreshRules();
        }
        private void ApplyWindowTheme()
        {
            bool dark = PropertiesAppearanceService.IsDark;
            Resources["App.Background"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#202730" : "#F6F8FB"));
            Resources["Surface"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#2D3643" : "#FFFFFF"));
            Resources["Text.Primary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#EDF2F6" : "#0F172A"));
            Resources["Text.Secondary"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#BAC5D2" : "#64748B"));
            Resources["Border"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#4B5868" : "#E6EAF0"));
        }
        private void LoadTargets()
        {
            if (_profile == null) return;
            _loading = true; bool matches = _profile.Key == "all" || _profile.Key == _context.CategoryKey || _profile.Key == _context.TypeKey; _target.ItemsSource = !matches ? new List<PropertyTarget>() : _kind.SelectedIndex == 0 ? _context.Groups : _context.Parameters; _target.SelectedIndex = _target.Items.Count > 0 ? 0 : -1; _loading = false; LoadColors();
        }
        private AppearanceRule CurrentRule => _target.SelectedItem is PropertyTarget target ? _profile?.Rules.FirstOrDefault(r => r.Kind == (_kind.SelectedIndex == 0 ? "group" : "parameter") && r.TargetKey == target.Key) : null;
        private void LoadColors()
        {
            if (_loading) return;
            _loading = true; var colors = (_tone.SelectedIndex == 1 ? CurrentRule?.Dark : CurrentRule?.Light) ?? new PaletteColors();
            _text.SelectedColor = AppearancePreferences.Parse(colors.Text); _background.SelectedColor = AppearancePreferences.Parse(colors.Background); _loading = false; UpdatePreview();
        }
        private static string Hex(Color? c) => c.HasValue && c.Value.A == 255 ? c.Value.ToString() : null;
        private void ColorsChanged()
        {
            if (_loading || _profile == null || !(_target.SelectedItem is PropertyTarget target)) return;
            var rule = CurrentRule;
            if (rule == null) { rule = new AppearanceRule { Kind = _kind.SelectedIndex == 0 ? "group" : "parameter", TargetKey = target.Key, Label = target.Name }; _profile.Rules.Add(rule); }
            var colors = _tone.SelectedIndex == 1 ? rule.Dark : rule.Light; colors.Text = Hex(_text.SelectedColor); colors.Background = Hex(_background.SelectedColor);
            if (rule.Light.IsEmpty && rule.Dark.IsEmpty) _profile.Rules.Remove(rule);
            _dirty.Add(_profile.Key); _revision++; _enabled.IsChecked = true; RefreshRules(); UpdatePreview(); Notify("Réglages non enregistrés. Vous pouvez les essayer dans Revit.");
        }
        private void UpdatePreview()
        {
            bool dark = _tone.SelectedIndex == 1; var colors = new PaletteColors { Text = Hex(_text.SelectedColor), Background = Hex(_background.SelectedColor) };
            var bg = AppearancePreferences.Parse(colors.Background) ?? (dark ? Color.FromRgb(59, 68, 83) : Colors.White); var fg = AppearancePreferences.ReadableText(colors, dark);
            _preview.Background = new SolidColorBrush(bg); _preview.BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(100, 110, 125) : Color.FromRgb(210, 220, 215));
            _previewTitle.Foreground = new SolidColorBrush(fg); _previewTitle.Text = (_target.SelectedItem as PropertyTarget)?.Name ?? "Choisissez un groupe ou un paramètre";
            _previewRows.Children.Clear();
            foreach (var line in new[] { "Propriété modifiable                 Exemple de valeur", "Propriété indisponible              Valeur conservée" })
                _previewRows.Children.Add(new TextBlock { Text = line, Margin = new Thickness(12, 4, 12, 7), Foreground = new SolidColorBrush(line.Contains("indisponible") ? (dark ? Colors.LightGray : Colors.Gray) : fg) });
        }
        private void RefreshRules() { _rules.ItemsSource = null; _rules.ItemsSource = _profile?.Rules.ToList(); }
        private void SelectRule()
        {
            if (!(_rules.SelectedItem is AppearanceRule rule)) return;
            _kind.SelectedIndex = rule.Kind == "group" ? 0 : 1;
            _target.SelectedItem = _target.Items.Cast<PropertyTarget>().FirstOrDefault(t => t.Key == rule.TargetKey);
            if (_target.SelectedItem == null) Notify("Cette règle appartient à un autre contexte. Sélectionnez l’élément correspondant dans Revit pour la modifier.");
        }
        private void RemoveRule() { var rule = _rules.SelectedItem as AppearanceRule ?? CurrentRule; if (rule == null) return; _profile.Rules.Remove(rule); _dirty.Add(_profile.Key); _revision++; RefreshRules(); LoadColors(); }
        private void ClearProfile() { _profile.Rules.Clear(); _dirty.Add(_profile.Key); _revision++; RefreshRules(); LoadColors(); Notify("Profil vidé dans le brouillon. Enregistrez pour confirmer ; les profils plus généraux restent applicables."); }
        private AppearanceProfile[] Changes() => _dirty.Select(k => _drafts[k].Copy()).ToArray();
        private void Preview()
        {
            var profiles = Changes(); bool enabled = _enabled.IsChecked == true;
            PropertiesAppearanceCommand.Request(app => { PropertiesAppearanceService.SetPreview(profiles, enabled); PropertiesAppearanceService.ProcessIdling(app); Notify("Aperçu temporaire · " + PropertiesAppearanceService.Status); });
        }
        private void Save()
        {
            var profiles = Changes(); bool enabled = _enabled.IsChecked == true; int revision = _revision;
            PropertiesAppearanceCommand.Request(app => { PropertiesAppearanceService.Commit(profiles, enabled); if (revision == _revision) _dirty.Clear(); PropertiesAppearanceService.ProcessIdling(app); Notify("Préférences enregistrées · " + PropertiesAppearanceService.Status); });
        }
        internal void Notify(string message) { _status.Text = message; }
    }
}
