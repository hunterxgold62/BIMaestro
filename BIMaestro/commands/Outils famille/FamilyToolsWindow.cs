using BIMaestro.Localization;
using Licensing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DB = Autodesk.Revit.DB;

namespace Famille
{
    /// <summary>
    /// Fenêtre « Outils famille » : menu à gauche avec les icônes du ruban, un outil affiché à la fois à droite.
    /// Les actions s'exécutent dans le contexte de la commande Revit (fenêtre modale) : une action = un Ctrl+Z.
    /// </summary>
    internal sealed class FamilyToolsWindow : Window
    {
        private enum Page { Convert, Purge, Translate, Units }
        private enum Tone { Neutral, Success, Warning, Error }

        private sealed class NavItem
        {
            public Border Root;
            public TextBlock Label;
            public TextBlock Badge;
            public Border BadgeBox;
        }

        private static readonly Brush SuccessBg = Frozen("#DCEEE3"), SuccessFg = Frozen("#155C42");
        private static readonly Brush WarningBg = Frozen("#FFF4DB"), WarningFg = Frozen("#92400E");
        private static readonly Brush ErrorBg = Frozen("#FDE8E8"), ErrorFg = Frozen("#B91C1C");

        private readonly DB.Document _doc;
        private readonly Dictionary<Page, NavItem> _nav = new Dictionary<Page, NavItem>();
        private readonly Dictionary<Page, (Tone Tone, string Text, IList<string> Lines)> _lastResult = new Dictionary<Page, (Tone, string, IList<string>)>();
        private readonly ContentControl _content = new ContentControl();
        private Page _page = Page.Convert;
        private bool _busy;

        // État de la famille, relu après chaque action.
        private List<DB.FamilyParameter> _shared = new List<DB.FamilyParameter>();
        private ObservableCollection<ParameterSelection> _purge = new ObservableCollection<ParameterSelection>();
        private List<string> _renameableParameters = new List<string>();
        private int _viewCount;
        private string _unitsPath;
        private bool _hasUnits;

        internal FamilyToolsWindow(DB.Document doc)
        {
            _doc = doc;

            ThemeManager.EnsureThemeLoaded();
            Title = UiLanguage.T("Outils famille", "Family tools");
            Width = 860; Height = 580; MinWidth = 720; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13; UseLayoutRounding = true;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BIMaestro;component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            SetResourceReference(BackgroundProperty, "App.Background");
            SetResourceReference(ForegroundProperty, "Text.Primary");

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(BuildHeader());

            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(body, 1);
            body.Children.Add(BuildSidebar());

            var contentCard = Card(_content, new Thickness(24, 20, 24, 20));
            Grid.SetColumn(contentCard, 2);
            body.Children.Add(contentCard);
            root.Children.Add(body);

            Content = root;
            Reload();
            Show(Page.Convert);
        }

        // ───────────────────────── Structure ─────────────────────────

        private UIElement BuildHeader()
        {
            var header = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 14, 20, 14) };
            header.SetResourceReference(Border.BackgroundProperty, "Brand");
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = UiLanguage.T("Outils famille", "Family tools"), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            stack.Children.Add(new TextBlock { Text = _doc.Title, FontSize = 13, Foreground = Brushes.White, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            header.Child = stack;
            return header;
        }

        private UIElement BuildSidebar()
        {
            var list = new StackPanel();
            list.Children.Add(NavButton(Page.Convert, "Convertir paramètres (2).png", UiLanguage.T("Convertir", "Convert")));
            list.Children.Add(NavButton(Page.Purge, "Purge famille.png", UiLanguage.T("Purge", "Purge")));
            list.Children.Add(NavButton(Page.Translate, "Tard IA.png", UiLanguage.T("Traduction IA", "AI translation")));
            list.Children.Add(NavButton(Page.Units, "Unités.png", UiLanguage.T("Unités", "Units")));

            var hint = Label(UiLanguage.T("Chaque action s'annule avec Ctrl+Z dans Revit.", "Each action can be undone with Ctrl+Z in Revit."), 11, false, "Text.Muted");
            hint.Margin = new Thickness(10, 0, 10, 4);
            hint.VerticalAlignment = VerticalAlignment.Bottom;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(list);
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);
            return Card(grid, new Thickness(8));
        }

        private UIElement NavButton(Page page, string icon, string text)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var image = new Image { Source = LoadIcon(icon, 32), Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            row.Children.Add(image);

            var label = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var badgeBox = new Border { Child = badge, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), MinWidth = 22, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badgeBox, 2);
            row.Children.Add(badgeBox);

            var root = new Border { Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            root.MouseLeftButtonUp += (_, __) => { if (!_busy) Show(page); };
            root.MouseEnter += (_, __) => { if (page != _page) root.SetResourceReference(Border.BackgroundProperty, "Hover.Fill"); };
            root.MouseLeave += (_, __) => { if (page != _page) root.Background = Brushes.Transparent; };

            _nav[page] = new NavItem { Root = root, Label = label, Badge = badge, BadgeBox = badgeBox };
            return root;
        }

        private void Show(Page page)
        {
            _page = page;
            foreach (var pair in _nav)
            {
                bool on = pair.Key == page;
                var item = pair.Value;
                if (on) item.Root.SetResourceReference(Border.BackgroundProperty, "Brand"); else item.Root.Background = Brushes.Transparent;
                item.Label.Foreground = on ? Brushes.White : (Brush)FindResource("Text.Primary");
                item.BadgeBox.Background = on ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) : SuccessBg;
                item.Badge.Foreground = on ? Brushes.White : SuccessFg;
            }

            switch (page)
            {
                case Page.Convert: _content.Content = BuildConvertPage(); break;
                case Page.Purge: _content.Content = BuildPurgePage(); break;
                case Page.Translate: _content.Content = BuildTranslatePage(); break;
                case Page.Units: _content.Content = BuildUnitsPage(); break;
            }
        }

        // ───────────────────────── Pages ─────────────────────────

        private UIElement BuildConvertPage()
        {
            var names = _shared.Select(p => p.Definition.Name + "   ·   " + (p.IsInstance ? UiLanguage.T("Instance", "Instance") : UiLanguage.T("Type", "Type")));
            var list = _shared.Count == 0
                ? Empty(UiLanguage.T("Aucun paramètre partagé dans cette famille.", "No shared parameter in this family."))
                : NameList(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));

            var convert = ActionButton(UiLanguage.T("Convertir en paramètres de famille", "Convert to family parameters"), true, RunConvert);
            convert.IsEnabled = _shared.Count > 0;

            return PageLayout(Page.Convert, "Convertir paramètres (2).png",
                UiLanguage.T("Convertir les paramètres partagés", "Convert shared parameters"),
                UiLanguage.T("Même nom, même groupe, même type ou instance.", "Same name, group and type/instance."),
                Count(_shared.Count, "paramètre partagé", "paramètres partagés", "shared parameter", "shared parameters"),
                list, convert);
        }

        private UIElement BuildPurgePage()
        {
            UIElement list;
            var purge = ActionButton(UiLanguage.T("Purger la sélection", "Purge selection"), true, RunPurge);
            if (_purge.Count == 0)
            {
                list = Empty(UiLanguage.T("Aucun paramètre inutilisé.", "No unused parameter."));
                purge.IsEnabled = false;
            }
            else
            {
                var checks = new List<CheckBox>();
                var all = new CheckBox { Content = UiLanguage.T("Tout cocher", "Select all"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 8) };
                void Sync()
                {
                    int n = _purge.Count(c => c.IsSelected);
                    all.IsChecked = n == 0 ? false : n == _purge.Count ? (bool?)true : null;
                    purge.IsEnabled = n > 0;
                }
                all.Click += (_, __) =>
                {
                    bool value = all.IsChecked == true;
                    foreach (var cb in checks) cb.IsChecked = value;
                };

                var rows = new StackPanel();
                int i = 0;
                foreach (var c in _purge.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = c;
                    var cb = new CheckBox { Content = item.Name, IsChecked = item.IsSelected, VerticalContentAlignment = VerticalAlignment.Center };
                    cb.Checked += (_, __) => { item.IsSelected = true; Sync(); };
                    cb.Unchecked += (_, __) => { item.IsSelected = false; Sync(); };
                    checks.Add(cb);
                    rows.Children.Add(Row(cb, i++));
                }

                var stack = new DockPanel();
                DockPanel.SetDock(all, Dock.Top);
                stack.Children.Add(all);
                stack.Children.Add(ListBox(rows));
                list = stack;
                Sync();
            }

            return PageLayout(Page.Purge, "Purge famille.png",
                UiLanguage.T("Purger les paramètres inutilisés", "Purge unused parameters"),
                UiLanguage.T("Ni cote, ni formule. Une copie du .rfa est faite avant.", "No dimension, no formula. The .rfa is copied first."),
                Count(_purge.Count, "paramètre inutilisé", "paramètres inutilisés", "unused parameter", "unused parameters"),
                list, purge);
        }

        private UIElement BuildTranslatePage()
        {
            UIElement list;
            if (_lastResult.TryGetValue(Page.Translate, out var last) && last.Lines != null && last.Lines.Count > 0)
                list = NameList(last.Lines);
            else if (_renameableParameters.Count == 0)
                list = Empty(UiLanguage.T("Aucun paramètre renommable.", "No renameable parameter."));
            else
                list = NameList(_renameableParameters.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));

            var parameters = ActionButton(UiLanguage.T("Traduire les paramètres", "Translate parameters"), true, () => RunTranslate(false));
            parameters.IsEnabled = _renameableParameters.Count > 0;
            var views = ActionButton(UiLanguage.T("Traduire les vues", "Translate views"), false, () => RunTranslate(true));
            views.IsEnabled = _viewCount > 0;

            return PageLayout(Page.Translate, "Tard IA.png",
                UiLanguage.T("Traduire en français", "Translate into French"),
                UiLanguage.T("Les noms déjà en français ne bougent pas.", "Names already in French are kept."),
                Count(_renameableParameters.Count, "paramètre renommable", "paramètres renommables", "renameable parameter", "renameable parameters")
                    + "  ·  " + Count(_viewCount, "vue", "vues", "view", "views"),
                list, views, parameters);
        }

        private UIElement BuildUnitsPage()
        {
            var tiles = new UniformGrid2();
            var apply = Tile("Import unités.png",
                UiLanguage.T("Appliquer mes unités", "Apply my units"),
                _hasUnits ? UiLanguage.T("Unités enregistrées le ", "Saved on ") + File.GetLastWriteTime(_unitsPath).ToString("g")
                          : UiLanguage.T("Aucune unité enregistrée pour l'instant.", "No saved units yet."),
                RunImportUnits, _hasUnits);
            var save = Tile("Unités.png",
                UiLanguage.T("Enregistrer comme référence", "Save as reference"),
                UiLanguage.T("Garde les unités et précisions de cette famille pour les réappliquer ailleurs.", "Keeps this family's units and accuracy to reuse elsewhere."),
                RunExportUnits, true);
            tiles.Add(apply, save);

            return PageLayout(Page.Units, "Unités.png",
                UiLanguage.T("Unités", "Units"),
                UiLanguage.T("Longueurs, surfaces, angles… et leur précision.", "Lengths, areas, angles… and their accuracy."),
                _hasUnits ? UiLanguage.T("Référence disponible", "Reference available") : UiLanguage.T("Pas encore de référence", "No reference yet"),
                tiles.Element);
        }

        /// <summary>Gabarit commun : en-tête avec grande icône, contenu, bandeau de résultat et boutons en bas à droite.</summary>
        private UIElement PageLayout(Page page, string icon, string title, string subtitle, string status, UIElement body, params Button[] buttons)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var iconBox = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(12), Background = SuccessBg, Margin = new Thickness(0, 0, 14, 0) };
            var img = new Image { Source = LoadIcon(icon, 64), Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconBox.Child = img;
            head.Children.Add(iconBox);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(Label(title, 18, true));
            titles.Children.Add(Label(subtitle, 12, false, "Text.Secondary"));
            var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = SuccessBg };
            chip.Child = new TextBlock { Text = status, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = SuccessFg };
            titles.Children.Add(chip);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            grid.Children.Add(head);

            Grid.SetRow((FrameworkElement)body, 1);
            grid.Children.Add(body);

            if (_lastResult.TryGetValue(page, out var result) && !string.IsNullOrEmpty(result.Text))
            {
                var banner = Banner(result.Tone, result.Text);
                Grid.SetRow(banner, 2);
                grid.Children.Add(banner);
            }

            if (buttons.Length > 0)
            {
                var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
                foreach (var b in buttons) footer.Children.Add(b);
                Grid.SetRow(footer, 3);
                grid.Children.Add(footer);
            }
            return grid;
        }

        // ───────────────────────── Actions ─────────────────────────

        private void RunConvert()
        {
            Execute(Page.Convert, () =>
            {
                var o = ConvertSharedToFamilyParametersCommand.ConvertAll(_doc);
                if (o.Detected == 0) return (Tone.Neutral, UiLanguage.T("Aucun paramètre partagé à convertir.", "No shared parameter to convert."), null);
                if (o.Fatal)
                    return (Tone.Error, UiLanguage.T("Conversion annulée, famille inchangée : ", "Conversion cancelled, family unchanged: ") + o.Failed.FirstOrDefault(), null);
                int ignored = o.Detected - o.Converted;
                if (ignored > 0)
                    return (Tone.Warning, UiLanguage.T($"{o.Converted} converti(s), {ignored} ignoré(s) : ", $"{o.Converted} converted, {ignored} skipped: ")
                                          + string.Join(" ; ", o.Failed.Distinct().Take(3)), null);
                return (Tone.Success, Count(o.Converted, "paramètre converti", "paramètres convertis", "parameter converted", "parameters converted"), null);
            });
        }

        private void RunPurge()
        {
            var toRemove = _purge.Where(c => c.IsSelected).Select(c => c.Parameter).ToList();
            if (toRemove.Count == 0) return;

            Execute(Page.Purge, () =>
            {
                string backup = BackupFamilyFile();
                var removed = PurgeFamilyParametersCommand.RemoveParameters(_doc, toRemove);
                string text = Count(removed.Count, "paramètre supprimé", "paramètres supprimés", "parameter removed", "parameters removed");
                if (backup != null) text += "\n" + UiLanguage.T("Copie : ", "Backup: ") + backup;
                return (Tone.Success, text, null);
            });
        }

        private void RunTranslate(bool views)
        {
            Execute(Page.Translate, () =>
            {
                var o = views
                    ? TraduireVuesFamilleOpenAI.TranslateViews(_doc, BIMaestroApp.LicenseJwt)
                    : TraduireParametresFamilleOpenAI.TranslateParameters(_doc, BIMaestroApp.LicenseJwt);

                if (o.Renameable == 0)
                    return (Tone.Neutral, views ? UiLanguage.T("Aucune vue renommable.", "No renameable view.")
                                                : UiLanguage.T("Aucun paramètre renommable.", "No renameable parameter."), null);
                if (o.Proposed == 0 && o.Warnings.Count == 0)
                    return (Tone.Neutral, UiLanguage.T("Tout est déjà en français.", "Everything is already in French."), null);

                var lines = o.Renamed.Select(r => $"{r.OldName}   →   {r.NewName}").Concat(o.RenameErrors).Concat(o.Warnings).ToList();
                bool warn = o.RenameErrors.Count > 0 || o.Warnings.Count > 0;
                string text = views
                    ? Count(o.Renamed.Count, "vue traduite", "vues traduites", "view translated", "views translated")
                    : Count(o.Renamed.Count, "paramètre traduit", "paramètres traduits", "parameter translated", "parameters translated");
                return (warn ? Tone.Warning : Tone.Success, text, lines);
            }, UiLanguage.T("Traduction en cours…", "Translating…"));
        }

        private void RunImportUnits()
        {
            Execute(Page.Units, () =>
            {
                if (!_hasUnits) return (Tone.Neutral, UiLanguage.T("Aucune unité enregistrée.", "No saved units."), null);
                var (applied, _) = ImportProjectUnitsCommand.ApplyUnits(_doc, ImportProjectUnitsCommand.LoadUnits(_unitsPath));
                return (Tone.Success, Count(applied, "unité appliquée", "unités appliquées", "unit applied", "units applied"), null);
            });
        }

        private void RunExportUnits()
        {
            Execute(Page.Units, () =>
            {
                var list = ExportProjectUnitsCommand.ExportUnits(_doc, ImportProjectUnitsCommand.UnitsFilePath);
                return (Tone.Success, Count(list.Count, "unité enregistrée comme référence", "unités enregistrées comme référence", "unit saved as reference", "units saved as reference"), null);
            });
        }

        /// <summary>Lance une action Revit, affiche « en cours », puis le résultat dans le bandeau de la page et met à jour les compteurs.</summary>
        private void Execute(Page page, Func<(Tone Tone, string Text, IList<string> Lines)> action, string runningText = null)
        {
            if (_busy) return;
            _busy = true;
            _lastResult[page] = (Tone.Neutral, runningText ?? UiLanguage.T("En cours…", "Running…"), null);
            Show(page);
            IsEnabledContent(false);
            Cursor = Cursors.Wait;
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

            try
            {
                _lastResult[page] = action();
            }
            catch (Exception ex)
            {
                _lastResult[page] = (Tone.Error, Describe(ex), null);
            }
            finally
            {
                Cursor = null;
                _busy = false;
                IsEnabledContent(true);
                Reload();
                Show(page);
            }
        }

        private void IsEnabledContent(bool enabled)
        {
            if (_content.Content is UIElement e) e.IsEnabled = enabled;
        }

        // ───────────────────────── État de la famille ─────────────────────────

        private void Reload()
        {
            var parameters = _doc.FamilyManager.GetParameters().Where(p => p?.Definition != null).ToList();
            _shared = parameters.Where(IsShared).ToList();
            _purge = PurgeFamilyParametersCommand.FindRemovableParameters(_doc);
            _renameableParameters = parameters.Where(p => !IsShared(p) && !IsBuiltIn(p)).Select(p => p.Definition.Name).ToList();
            _viewCount = new DB.FilteredElementCollector(_doc).OfClass(typeof(DB.View)).Cast<DB.View>()
                .Count(v => v.ViewType != DB.ViewType.Internal && !v.IsTemplate);
            try { _unitsPath = ImportProjectUnitsCommand.UnitsFilePath; } catch { _unitsPath = null; }
            _hasUnits = _unitsPath != null && File.Exists(_unitsPath);

            SetBadge(Page.Convert, _shared.Count.ToString());
            SetBadge(Page.Purge, _purge.Count.ToString());
            SetBadge(Page.Translate, _renameableParameters.Count.ToString());
            SetBadge(Page.Units, _hasUnits ? "✓" : "");
        }

        private void SetBadge(Page page, string text)
        {
            if (!_nav.TryGetValue(page, out var item)) return;
            item.Badge.Text = text;
            item.BadgeBox.Visibility = string.IsNullOrEmpty(text) || text == "0" ? Visibility.Collapsed : Visibility.Visible;
        }

        // ───────────────────────── Éléments visuels ─────────────────────────

        private Button ActionButton(string label, bool primary, System.Action onClick)
        {
            var b = new Button { Content = label, MinHeight = 36, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(8, 0, 0, 0) };
            b.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
            b.Click += (_, __) => onClick();
            return b;
        }

        private UIElement Tile(string icon, string title, string text, System.Action onClick, bool enabled)
        {
            var stack = new StackPanel { Margin = new Thickness(4) };
            var img = new Image { Source = LoadIcon(icon, 64), Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            stack.Children.Add(img);
            stack.Children.Add(Label(title, 15, true));
            stack.Children.Add(Label(text, 12, false, "Text.Secondary"));

            var tile = new Border { Child = stack, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), BorderThickness = new Thickness(1), Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.5 };
            tile.SetResourceReference(Border.BackgroundProperty, "Surface.Subtle");
            tile.SetResourceReference(Border.BorderBrushProperty, "Border");
            if (enabled)
            {
                tile.MouseEnter += (_, __) => tile.SetResourceReference(Border.BorderBrushProperty, "Brand");
                tile.MouseLeave += (_, __) => tile.SetResourceReference(Border.BorderBrushProperty, "Border");
                tile.MouseLeftButtonUp += (_, __) => { if (!_busy) onClick(); };
            }
            return tile;
        }

        /// <summary>Deux tuiles côte à côte, alignées en haut.</summary>
        private sealed class UniformGrid2
        {
            public readonly Grid Element = new Grid { VerticalAlignment = VerticalAlignment.Top };
            public void Add(UIElement left, UIElement right)
            {
                Element.ColumnDefinitions.Add(new ColumnDefinition());
                Element.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                Element.ColumnDefinitions.Add(new ColumnDefinition());
                Element.Children.Add(left);
                Grid.SetColumn(right, 2);
                Element.Children.Add(right);
            }
        }

        private UIElement NameList(IEnumerable<string> names)
        {
            var rows = new StackPanel();
            int i = 0;
            foreach (var n in names)
                rows.Children.Add(Row(new TextBlock { Text = n, TextTrimming = TextTrimming.CharacterEllipsis }, i++));
            return ListBox(rows);
        }

        private static UIElement Row(UIElement child, int index)
        {
            var row = new Border { Child = child, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6) };
            if (index % 2 == 1) row.SetResourceReference(Border.BackgroundProperty, "Surface.Subtle");
            return row;
        }

        private static UIElement ListBox(UIElement rows)
        {
            var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4) };
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            box.Child = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            return box;
        }

        private static UIElement Empty(string text)
        {
            var t = Label(text, 13, false, "Text.Secondary");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            return new Border { Child = t };
        }

        private static Border Banner(Tone tone, string text)
        {
            Brush bg = tone == Tone.Success ? SuccessBg : tone == Tone.Warning ? WarningBg : tone == Tone.Error ? ErrorBg : null;
            Brush fg = tone == Tone.Success ? SuccessFg : tone == Tone.Warning ? WarningFg : tone == Tone.Error ? ErrorFg : null;
            string icon = tone == Tone.Success ? "✓  " : tone == Tone.Warning ? "⚠  " : tone == Tone.Error ? "✕  " : "";
            var t = new TextBlock { Text = icon + text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var b = new Border { Child = t, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0) };
            if (bg != null) { b.Background = bg; t.Foreground = fg; }
            else { b.SetResourceReference(Border.BackgroundProperty, "Surface.Subtle"); t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); }
            return b;
        }

        private static Border Card(UIElement child, Thickness padding)
        {
            var card = new Border { Child = child, Padding = padding, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            card.SetResourceReference(EffectProperty, "CardShadow");
            return card;
        }

        private static TextBlock Label(string text, double size, bool bold = false, string brushKey = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            if (brushKey != null) t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        // ───────────────────────── Outils ─────────────────────────

        private static ImageSource LoadIcon(string resourceFileName, int decodeSize)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BIMaestro.Resources." + resourceFileName))
                {
                    if (stream == null) return null;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    bmp.DecodePixelWidth = decodeSize;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Copie le .rfa enregistré sans changer le document ouvert (contrairement à SaveAs).</summary>
        private string BackupFamilyFile()
        {
            string source = _doc.PathName;
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BIMaestro", "FamilleRevit");
                Directory.CreateDirectory(folder);
                string baseName = Path.GetFileNameWithoutExtension(source) + "_purger";
                string target = Path.Combine(folder, baseName + ".rfa");
                for (int i = 1; File.Exists(target); i++) target = Path.Combine(folder, $"{baseName}_{i}.rfa");
                File.Copy(source, target);
                return _doc.IsModified ? target + UiLanguage.T(" (dernier état enregistré)", " (last saved state)") : target;
            }
            catch (Exception ex)
            {
                return UiLanguage.T("impossible (", "failed (") + ex.Message + ")";
            }
        }

        private static string Count(int n, string frOne, string frMany, string enOne, string enMany)
        {
            return $"{n} " + UiLanguage.T(n > 1 ? frMany : frOne, n > 1 ? enMany : enOne);
        }

        private static bool IsShared(DB.FamilyParameter p)
        {
            try { return p.IsShared; } catch { return false; }
        }

        private static bool IsBuiltIn(DB.FamilyParameter p)
        {
            try { return p.Definition is DB.InternalDefinition d && d.BuiltInParameter != DB.BuiltInParameter.INVALID; }
            catch { return false; }
        }

        private static string Describe(Exception ex)
        {
            if (ex is AggregateException agg)
            {
                var inner = agg.Flatten().InnerExceptions;
                if (inner.Any(e => e is InvalidOperationException && e.Message == AiClient.QuotaExceededMessage))
                    return UiLanguage.T("Quota IA dépassé.", "AI quota exceeded.");
                return string.Join(" | ", inner.Select(e => e.Message).Distinct().Take(3));
            }
            if (ex is InvalidOperationException && ex.Message == AiClient.QuotaExceededMessage)
                return UiLanguage.T("Quota IA dépassé.", "AI quota exceeded.");
            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_busy) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
