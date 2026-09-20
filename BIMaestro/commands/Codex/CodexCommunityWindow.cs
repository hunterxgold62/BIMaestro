using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BIMaestro.Codex
{
    internal static class CommunityStyle
    {
        internal static readonly Dictionary<string, string> Categories = new Dictionary<string, string> {
            {"", "Toutes les catégories"}, {"generic", "Modèles génériques"}, {"furniture", "Mobilier"}, {"door", "Portes"}, {"window", "Fenêtres"},
            {"casework", "Agencement"}, {"plumbing_fixture", "Appareils sanitaires"}, {"mechanical_equipment", "Équipement mécanique"},
            {"electrical_equipment", "Équipement électrique"}, {"lighting_fixture", "Luminaires"}, {"pipe_accessory", "Accessoires de canalisation"}, {"pipe_fitting", "Raccords de canalisation"},
            {"duct_accessory", "Accessoires de gaine"}, {"duct_fitting", "Raccords de gaine"}, {"specialty_equipment", "Équipement spécialisé"}, {"structural_column", "Poteaux porteurs"} };
        internal static string Category(string key) => key != null && Categories.TryGetValue(key, out var label) ? label : key ?? "Non classée";
        internal static string Origin(string key) => key == "ai" ? "Créée avec IA" : key == "personal" ? "Famille personnelle" : "Origine non renseignée";
        internal static void Apply(Window window)
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BIMaestro;component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            window.SetResourceReference(Window.BackgroundProperty, "App.Background"); window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
            window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 13; window.UseLayoutRounding = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        internal static Button Button(string text, bool primary = false)
        { var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 8, 12, 8) }; b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton"); return b; }
        internal static TextBlock Text(string text, int size = 13)
        { var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); return t; }
        internal static Border Card(UIElement content)
        { var b = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), BorderThickness = new Thickness(1) }; b.SetResourceReference(Border.BackgroundProperty, "Surface"); b.SetResourceReference(Border.BorderBrushProperty, "Border"); return b; }
    }

    internal sealed class CodexCommunityWindow : Window
    {
        private readonly CodexRevitBridge bridge;
        private readonly TextBox query = new TextBox { MaxLength = 120, MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
        private readonly ListBox categories = new ListBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", BorderThickness = new Thickness(0) };
        private readonly ObservableCollection<KeyValuePair<string, string>> categoryItems = new ObservableCollection<KeyValuePair<string, string>>(CommunityStyle.Categories);
        private readonly ComboBox origin = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 12, 0) };
        private readonly CheckBox mine = new CheckBox { Content = "Mes publications", VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel cards = new WrapPanel();
        private readonly StackPanel details = new StackPanel();
        private readonly TextBlock status = CommunityStyle.Text("Recherchez ou parcourez les familles de la communauté.");
        private readonly Button search = CommunityStyle.Button("Rechercher", true), more = CommunityStyle.Button("Afficher la suite"), share = CommunityStyle.Button("Partager une famille…"), download = CommunityStyle.Button("Télécharger le RFA"), load = CommunityStyle.Button("Charger dans le projet", true), remove = CommunityStyle.Button("Retirer ma publication");
        private string cursor, activeQuery = "", activeCategory = "", activeOrigin = "";
        private bool activeMine, busy, initialized;
        private JObject selected;
        private int count;
        internal CodexCommunityWindow(CodexRevitBridge bridge)
        {
            this.bridge = bridge; CommunityStyle.Apply(this);
            Title = "BIMaestro — Bibliothèque commune"; Width = 1180; Height = 780; MinWidth = 950; MinHeight = 620;
            var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
            var heading = new DockPanel(); DockPanel.SetDock(share, Dock.Right); heading.Children.Add(share);
            var titles = new StackPanel(); titles.Children.Add(CommunityStyle.Text("Bibliothèque commune", 26)); titles.Children.Add(CommunityStyle.Text("Familles personnelles et créations IA · Revit 2023 à " + bridge.RevitVersion + " · sans inscription")); heading.Children.Add(titles); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            var filters = new DockPanel { Margin = new Thickness(0, 14, 0, 8) }; DockPanel.SetDock(search, Dock.Right); filters.Children.Add(search);
            var options = new StackPanel { Orientation = Orientation.Horizontal }; origin.Items.Add("Toutes les origines"); origin.Items.Add("Personnelles"); origin.Items.Add("Créées avec IA"); origin.SelectedIndex = 0; options.Children.Add(origin); options.Children.Add(mine); DockPanel.SetDock(options, Dock.Right); filters.Children.Add(options); filters.Children.Add(query);
            DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters); DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) }); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(265) }); root.Children.Add(columns);
            var side = new DockPanel(); var catTitle = CommunityStyle.Text("CATÉGORIES", 12); DockPanel.SetDock(catTitle, Dock.Top); side.Children.Add(catTitle); side.Children.Add(categories);
            categories.ItemsSource = categoryItems; categories.SelectedIndex = 0; columns.Children.Add(CommunityStyle.Card(side));
            var center = new DockPanel(); DockPanel.SetDock(more, Dock.Bottom); center.Children.Add(more); center.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetColumn(center, 1); columns.Children.Add(center);
            var right = CommunityStyle.Card(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); right.Margin = new Thickness(8, 0, 0, 12); Grid.SetColumn(right, 2); columns.Children.Add(right); Select(null);
            search.Click += async (_, __) => await Refresh(); query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Refresh(); };
            categories.SelectionChanged += async (_, __) => { if (initialized) await Refresh(); };
            origin.SelectionChanged += async (_, __) => { if (initialized) await Refresh(); };
            mine.Checked += async (_, __) => { if (initialized) await Refresh(); }; mine.Unchecked += async (_, __) => { if (initialized) await Refresh(); };
            more.Click += async (_, __) => await Run(() => Search(false));
            download.Click += async (_, __) => await Run(() => Download(false)); load.Click += async (_, __) => await Run(() => Download(true));
            remove.Click += async (_, __) => await Run(Remove);
            share.Click += async (_, __) => await Run(async () => { if (await CodexCommunityPublishWindow.ShowAsync(this, bridge)) { cursor = null; await Search(true); } else status.Text = "Publication annulée."; });
            Loaded += async (_, __) => { initialized = true; await Refresh(); };
        }
        private async Task Refresh()
        {
            if (busy) return;
            activeQuery = query.Text.Trim(); activeCategory = categories.SelectedValue as string ?? ""; activeOrigin = origin.SelectedIndex == 1 ? "personal" : origin.SelectedIndex == 2 ? "ai" : ""; activeMine = mine.IsChecked == true; cursor = null;
            await Run(() => Search(true));
        }
        private async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; SetControls();
            try { status.Text = "Connexion à la bibliothèque…"; await action(); }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? "Le service ne répond pas. Réessayez plus tard." : ex.Message; }
            finally { busy = false; SetControls(); }
        }
        private void SetControls()
        {
            search.IsEnabled = share.IsEnabled = query.IsEnabled = categories.IsEnabled = origin.IsEnabled = mine.IsEnabled = !busy;
            more.IsEnabled = !busy && !string.IsNullOrEmpty(cursor); download.IsEnabled = load.IsEnabled = !busy && selected != null; remove.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true;
        }
        private async Task Search(bool clear)
        {
            using (var service = new CodexCommunityLibrary())
            {
                var page = await service.Search(activeQuery, bridge.RevitVersion, cursor, activeCategory, activeOrigin, activeMine);
                if (clear) { cards.Children.Clear(); count = 0; Select(null); }
                foreach (JObject item in page["items"] as JArray ?? new JArray())
                {
                    string categoryKey = (string)item["category"];
                    if (!string.IsNullOrWhiteSpace(categoryKey))
                    {
                        bool known = false; foreach (var entry in categoryItems) if (entry.Key == categoryKey) { known = true; break; }
                        if (!known) categoryItems.Add(new KeyValuePair<string, string>(categoryKey, CommunityStyle.Category(categoryKey)));
                    }
                    var body = new StackPanel { Width = 205 }; body.Children.Add(CommunityStyle.Text(CommunityStyle.Origin((string)item["origin"]).ToUpperInvariant(), 11));
                    body.Children.Add(CommunityStyle.Text((string)item["name"], 16)); body.Children.Add(CommunityStyle.Text(CommunityStyle.Category((string)item["category"])));
                    body.Children.Add(CommunityStyle.Text("Revit " + item["revitVersion"] + "  ·  " + ((item.Value<long?>("sizeBytes") ?? 0) / 1000000d).ToString("0.0") + " Mo"));
                    body.Children.Add(CommunityStyle.Text((item.Value<long?>("downloadCount") ?? 0).ToString("N0") + " téléchargement(s)" + (item.Value<bool?>("isOwner") == true ? " · à vous" : ""), 11));
                    var button = new Button { Content = CommunityStyle.Card(body), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = (string)item["description"] };
                    button.Click += (_, __) => Select(item); cards.Children.Add(button); count++;
                }
                cursor = (string)page["nextCursor"]; status.Text = count + " famille(s) affichée(s)." + (count == 0 ? " Aucune famille dans cette sélection." : "") + (!string.IsNullOrEmpty(cursor) ? " D'autres résultats peuvent être disponibles avec « Afficher la suite »." : "");
            }
        }
        private void Select(JObject item)
        {
            selected = item; details.Children.Clear(); details.Children.Add(CommunityStyle.Text("DÉTAILS", 12));
            if (item == null) { details.Children.Add(CommunityStyle.Text("Sélectionnez une famille pour consulter sa fiche et la charger dans votre projet.")); SetControls(); return; }
            details.Children.Add(CommunityStyle.Text((string)item["name"], 21)); details.Children.Add(CommunityStyle.Text(CommunityStyle.Category((string)item["category"])));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.Origin((string)item["origin"]))); details.Children.Add(CommunityStyle.Text("Version d'origine : Revit " + item["revitVersion"]));
            details.Children.Add(CommunityStyle.Text((string)item["description"] ?? "Aucune description.")); details.Children.Add(load); details.Children.Add(download);
            if (item.Value<bool?>("isOwner") == true) details.Children.Add(remove);
            SetControls();
        }
        private async Task Download(bool intoProject)
        {
            if (selected == null) return;
            using (var service = new CodexCommunityLibrary()) { string path = await service.Download(selected); status.Text = "RFA disponible : " + path; if (intoProject) { await bridge.LoadCommunityFamilyAsync(path); status.Text = "Famille chargée dans le projet. Aucune instance placée."; } }
        }
        private async Task Remove()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (MessageBox.Show(this, "Retirer « " + (string)selected["name"] + " » de la bibliothèque commune ?\nLes copies déjà téléchargées resteront sur les ordinateurs de leurs utilisateurs.", "Retirer ma publication", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { status.Text = "Retrait annulé."; return; }
            using (var service = new CodexCommunityLibrary()) await service.Remove((string)selected["id"]);
            cursor = null; await Search(true); status.Text = "Publication retirée de la bibliothèque.";
        }
    }

    internal sealed class CodexCommunityPublishWindow : Window
    {
        private readonly TextBox name = new TextBox { MaxLength = 120 }, description = new TextBox { MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        private readonly ComboBox category = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key" }, origin = new ComboBox();
        private readonly CheckBox consent = new CheckBox { Content = "Je peux partager ce fichier et j'accepte de le rendre accessible à tous.", Margin = new Thickness(0, 16, 0, 12) };
        private readonly TextBlock status = CommunityStyle.Text("");
        private readonly Button publish = CommunityStyle.Button("Publier dans la bibliothèque", true);
        private readonly string path, version;
        private bool sending, published;
        private CodexCommunityPublishWindow(JObject metadata, string source)
        {
            CommunityStyle.Apply(this); Title = "Partager une famille"; Width = 620; Height = 690; MinWidth = 550; MinHeight = 600;
            path = (string)metadata["filePath"]; version = (string)metadata["revitVersion"];
            var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(CommunityStyle.Text("Partager une famille", 24)); panel.Children.Add(CommunityStyle.Text(Path.GetFileName(path) + " · Revit " + version));
            panel.Children.Add(CommunityStyle.Text("Nom")); name.Text = (string)metadata["name"] ?? Path.GetFileNameWithoutExtension(path); panel.Children.Add(name);
            panel.Children.Add(CommunityStyle.Text("Catégorie")); foreach (var entry in CommunityStyle.Categories) if (entry.Key != "") category.Items.Add(entry);
            string categoryKey = (string)metadata["category"] ?? "generic";
            foreach (var entry in CommunityStyle.Categories) if (string.Equals(entry.Value, categoryKey, StringComparison.OrdinalIgnoreCase)) { categoryKey = entry.Key; break; }
            if (!CommunityStyle.Categories.ContainsKey(categoryKey)) category.Items.Add(new KeyValuePair<string, string>(categoryKey, categoryKey));
            category.SelectedValue = categoryKey; panel.Children.Add(category);
            panel.Children.Add(CommunityStyle.Text("Origine")); origin.Items.Add("Famille personnelle"); origin.Items.Add("Création IA"); origin.SelectedIndex = source == "ai" ? 1 : 0; panel.Children.Add(origin);
            panel.Children.Add(CommunityStyle.Text("Description")); panel.Children.Add(description); panel.Children.Add(consent);
            panel.Children.Add(CommunityStyle.Text("Sans compte : le droit de retrait est conservé dans ce profil Windows. Il ne sera pas disponible depuis un autre ordinateur ou après la perte de cette identité locale.", 11)); panel.Children.Add(publish); panel.Children.Add(status);
            publish.Click += async (_, __) => await Publish(); Closing += (_, e) => { if (sending) e.Cancel = true; };
        }
        internal static async Task<bool> ShowAsync(Window owner, CodexRevitBridge bridge, string filePath = null, string origin = "personal")
        {
            if (string.IsNullOrEmpty(filePath)) { var picker = new OpenFileDialog { Filter = "Famille Revit (*.rfa)|*.rfa", CheckFileExists = true, Multiselect = false }; if (picker.ShowDialog(owner) != true) return false; filePath = picker.FileName; }
            var metadata = await bridge.InspectCommunityFamilyAsync(filePath);
            var dialog = new CodexCommunityPublishWindow(metadata, origin) { Owner = owner }; dialog.ShowDialog(); return dialog.published;
        }
        private async Task Publish()
        {
            if (sending) return;
            if (consent.IsChecked != true || string.IsNullOrWhiteSpace(name.Text) || category.SelectedValue == null) { status.Text = "Renseignez le nom, la catégorie et votre accord de partage."; return; }
            sending = true; publish.IsEnabled = false; name.IsEnabled = category.IsEnabled = origin.IsEnabled = description.IsEnabled = consent.IsEnabled = false;
            try
            {
                status.Text = "Envoi en cours…";
                using (var service = new CodexCommunityLibrary())
                {
                    var result = await service.Publish(path, name.Text.Trim(), (string)category.SelectedValue, description.Text.Trim(), version, origin.SelectedIndex == 1 ? "ai" : "personal");
                    if (result.Value<bool?>("duplicate") == true) MessageBox.Show(this, "Ce fichier existe déjà dans la bibliothèque. La publication existante a été conservée ; ses droits de retrait ne sont pas transférés.", "Famille déjà disponible", MessageBoxButton.OK, MessageBoxImage.Information);
                    published = true;
                }
                sending = false; Close();
            }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? "Délai dépassé. Vérifiez la bibliothèque avant de renvoyer le fichier." : ex.Message; }
            finally { sending = false; publish.IsEnabled = true; name.IsEnabled = category.IsEnabled = origin.IsEnabled = description.IsEnabled = consent.IsEnabled = true; }
        }
    }
}
