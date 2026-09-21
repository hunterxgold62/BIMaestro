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
using System.Windows.Media.Imaging;

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
        private readonly Button search = CommunityStyle.Button("Rechercher", true), more = CommunityStyle.Button("Afficher la suite"), share = CommunityStyle.Button("Partager une famille…"), download = CommunityStyle.Button("Télécharger le RFA"), load = CommunityStyle.Button("Charger dans le projet", true), remove = CommunityStyle.Button("Retirer ma publication"), changeCover = CommunityStyle.Button("Modifier la photo de couverture…"), editDetails = CommunityStyle.Button("Modifier les informations…"), newVersion = CommunityStyle.Button("Publier une nouvelle version…"), history = CommunityStyle.Button("Historique des versions…");
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
            changeCover.Click += async (_, __) => await Run(ChangeCover);
            editDetails.Click += async (_, __) => await Run(EditDetails);
            newVersion.Click += async (_, __) => await Run(PublishNewVersion);
            history.Click += async (_, __) => await Run(ShowHistory);
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
            changeCover.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true;
            editDetails.IsEnabled = newVersion.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true; history.IsEnabled = !busy && selected != null;
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
                    var previewUri = service.PreviewUri(item);
                    if (previewUri != null) item["previewUrl"] = previewUri.AbsoluteUri;
                    var body = new StackPanel { Width = 205 }; body.Children.Add(CreatePreview(item, 205, 145)); body.Children.Add(CommunityStyle.Text(CommunityStyle.Origin((string)item["origin"]).ToUpperInvariant(), 11));
                    body.Children.Add(CommunityStyle.Text((string)item["name"], 16)); body.Children.Add(CommunityStyle.Text(CommunityStyle.Category((string)item["category"])));
                    body.Children.Add(CommunityStyle.Text("Créateur : " + ((string)item["creatorName"] ?? "Non renseigné"), 11));
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
            details.Children.Add(CreatePreview(item, 225, 210));
            details.Children.Add(CommunityStyle.Text("Créateur : " + ((string)item["creatorName"] ?? "Non renseigné")));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.Origin((string)item["origin"]))); details.Children.Add(CommunityStyle.Text("Version d'origine : Revit " + item["revitVersion"]));
            details.Children.Add(CommunityStyle.Text("Révision " + (item.Value<int?>("revisionNumber") ?? 1)));
            if (!string.IsNullOrWhiteSpace((string)item["changeNote"])) details.Children.Add(CommunityStyle.Text("Modifications : " + (string)item["changeNote"], 11));
            details.Children.Add(CommunityStyle.Text((string)item["description"] ?? "Aucune description.")); details.Children.Add(load); details.Children.Add(download);
            details.Children.Add(history);
            if (item.Value<bool?>("isOwner") == true) { details.Children.Add(editDetails); details.Children.Add(newVersion); details.Children.Add(changeCover); details.Children.Add(remove); }
            SetControls();
        }
        private static FrameworkElement CreatePreview(JObject item, double width, double height)
        {
            var preview = new Grid { Width = width, Height = height, Margin = new Thickness(0, 0, 0, 10) };
            var fallback = CommunityStyle.Text("APERÇU\nNON DISPONIBLE", 12); fallback.TextAlignment = TextAlignment.Center; fallback.VerticalAlignment = VerticalAlignment.Center; fallback.Opacity = 0.55; preview.Children.Add(fallback);
            if (Uri.TryCreate((string)item?["previewUrl"], UriKind.Absolute, out var uri))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = uri; bitmap.DecodePixelWidth = (int)(width * 2); bitmap.CacheOption = BitmapCacheOption.OnDemand; bitmap.EndInit();
                preview.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
            return preview;
        }
        private async Task Download(bool intoProject)
        {
            if (selected == null) return;
            if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(this)) { status.Text = "Téléchargement annulé : profil non renseigné."; return; }
            using (var service = new CodexCommunityLibrary()) { string path = await service.Download(selected); status.Text = "RFA disponible : " + path; if (intoProject) { await bridge.LoadCommunityFamilyAsync(path); status.Text = "Famille chargée dans le projet. Aucune instance placée."; } }
        }
        private async Task Remove()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (MessageBox.Show(this, "Retirer « " + (string)selected["name"] + " » de la bibliothèque commune ?\nLes copies déjà téléchargées resteront sur les ordinateurs de leurs utilisateurs.", "Retirer ma publication", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { status.Text = "Retrait annulé."; return; }
            using (var service = new CodexCommunityLibrary()) await service.Remove((string)selected["id"]);
            cursor = null; await Search(true); status.Text = "Publication retirée de la bibliothèque.";
        }
        private async Task ChangeCover()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false, Title = "Choisir la photo de couverture" };
            if (picker.ShowDialog(this) != true) { status.Text = "Modification de la photo annulée."; return; }
            using (var service = new CodexCommunityLibrary()) await service.UpdatePreview(selected, picker.FileName);
            Select(selected); status.Text = "Photo de couverture mise à jour.";
        }
        private async Task EditDetails()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (await CodexCommunityEditWindow.ShowAsync(this, selected)) { Select(selected); status.Text = "Informations mises à jour."; }
        }
        private async Task PublishNewVersion()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (await CodexCommunityPublishWindow.ShowAsync(this, bridge, null, (string)selected["origin"], selected)) { cursor = null; await Search(true); status.Text = "Nouvelle version publiée."; }
        }
        private async Task ShowHistory()
        {
            if (selected == null) return; using (var service = new CodexCommunityLibrary()) { var versions = await service.GetVersions(selected); new CodexCommunityHistoryWindow(versions) { Owner = this }.ShowDialog(); }
        }
    }

    internal sealed class CodexCommunityPublishWindow : Window
    {
        private readonly TextBox name = new TextBox { MaxLength = 120 }, description = new TextBox { MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110 };
        private readonly ComboBox category = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key" }, origin = new ComboBox();
        private readonly CheckBox consent = new CheckBox { Content = "Je peux partager ce fichier et j'accepte de le rendre accessible à tous.", Margin = new Thickness(0, 16, 0, 12) };
        private readonly TextBlock status = CommunityStyle.Text("");
        private readonly TextBlock coverName = CommunityStyle.Text("");
        private readonly TextBox changeNote = new TextBox { MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
        private readonly Button publish = CommunityStyle.Button("Publier dans la bibliothèque", true);
        private readonly Button chooseCover = CommunityStyle.Button("Choisir une autre photo…");
        private readonly string path, version, generatedPreviewPath; private readonly JObject replaces;
        private string coverPath;
        private bool sending, published;
        private CodexCommunityPublishWindow(JObject metadata, string source, JObject replaces = null)
        {
            this.replaces = replaces; CommunityStyle.Apply(this); Title = replaces == null ? "Partager une famille" : "Publier une nouvelle version"; Width = 620; Height = 740; MinWidth = 550; MinHeight = 600;
            path = (string)metadata["filePath"]; version = (string)metadata["revitVersion"];
            generatedPreviewPath = (string)metadata["previewPath"]; coverPath = generatedPreviewPath;
            var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(CommunityStyle.Text(replaces == null ? "Partager une famille" : "Nouvelle version", 24)); panel.Children.Add(CommunityStyle.Text(Path.GetFileName(path) + " · Revit " + version));
            panel.Children.Add(CommunityStyle.Text("Nom")); name.Text = (string)replaces?["name"] ?? (string)metadata["name"] ?? Path.GetFileNameWithoutExtension(path); panel.Children.Add(name);
            panel.Children.Add(CommunityStyle.Text("Catégorie")); foreach (var entry in CommunityStyle.Categories) if (entry.Key != "") category.Items.Add(entry);
            string categoryKey = (string)replaces?["category"] ?? (string)metadata["category"] ?? "generic";
            foreach (var entry in CommunityStyle.Categories) if (string.Equals(entry.Value, categoryKey, StringComparison.OrdinalIgnoreCase)) { categoryKey = entry.Key; break; }
            if (!CommunityStyle.Categories.ContainsKey(categoryKey)) category.Items.Add(new KeyValuePair<string, string>(categoryKey, categoryKey));
            category.SelectedValue = categoryKey; panel.Children.Add(category);
            panel.Children.Add(CommunityStyle.Text("Origine")); origin.Items.Add("Famille IA"); origin.Items.Add("Famille personnelle"); origin.SelectedIndex = source == "personal" ? 1 : 0; panel.Children.Add(origin);
            panel.Children.Add(CommunityStyle.Text("Photo de couverture")); coverName.Text = string.IsNullOrEmpty(coverPath) ? "Aucun aperçu automatique — choisissez une image." : "Aperçu Revit automatique"; panel.Children.Add(coverName); panel.Children.Add(chooseCover);
            panel.Children.Add(CommunityStyle.Text("Description")); description.Text = (string)replaces?["description"] ?? ""; panel.Children.Add(description); if (replaces != null) { panel.Children.Add(CommunityStyle.Text("Résumé des modifications")); panel.Children.Add(changeNote); } panel.Children.Add(consent);
            panel.Children.Add(CommunityStyle.Text("Sans compte : le droit de retrait est conservé dans ce profil Windows. Il ne sera pas disponible depuis un autre ordinateur ou après la perte de cette identité locale.", 11)); panel.Children.Add(publish); panel.Children.Add(status);
            chooseCover.Click += (_, __) => ChooseCover(); publish.Click += async (_, __) => await Publish(); Closing += (_, e) => { if (sending) { e.Cancel = true; return; } try { if (!string.IsNullOrEmpty(generatedPreviewPath) && File.Exists(generatedPreviewPath)) File.Delete(generatedPreviewPath); } catch { } };
        }
        internal static async Task<bool> ShowAsync(Window owner, CodexRevitBridge bridge, string filePath = null, string origin = "ai", JObject replaces = null)
        {
            if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(owner)) return false;
            if (string.IsNullOrEmpty(filePath)) { var picker = new OpenFileDialog { Filter = "Famille Revit (*.rfa)|*.rfa", CheckFileExists = true, Multiselect = false }; if (picker.ShowDialog(owner) != true) return false; filePath = picker.FileName; }
            var metadata = await bridge.InspectCommunityFamilyAsync(filePath);
            var dialog = new CodexCommunityPublishWindow(metadata, origin, replaces) { Owner = owner }; dialog.ShowDialog(); return dialog.published;
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
                    var result = await service.Publish(path, name.Text.Trim(), (string)category.SelectedValue, description.Text.Trim(), version, origin.SelectedIndex == 0 ? "ai" : "personal", coverPath, (string)replaces?["id"], changeNote.Text.Trim());
                    if (result.Value<bool?>("restored") == true) MessageBox.Show(this, "Votre famille retirée a été remise dans la bibliothèque.", "Famille réactivée", MessageBoxButton.OK, MessageBoxImage.Information);
                    else if (result.Value<bool?>("duplicate") == true) MessageBox.Show(this, "Ce fichier existe déjà dans la bibliothèque. La publication existante a été conservée ; ses droits de retrait ne sont pas transférés.", "Famille déjà disponible", MessageBoxButton.OK, MessageBoxImage.Information);
                    if (result.Value<bool?>("previewUploaded") == false)
                        MessageBox.Show(this, "La famille a bien été publiée, mais son aperçu n’a pas pu être envoyé.\n\n" + ((string)result["previewError"] ?? "Service d’aperçu indisponible."), "Aperçu non publié", MessageBoxButton.OK, MessageBoxImage.Warning);
                    published = true;
                }
                sending = false; Close();
            }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? "Délai dépassé. Vérifiez la bibliothèque avant de renvoyer le fichier." : ex.Message; }
            finally { sending = false; publish.IsEnabled = true; name.IsEnabled = category.IsEnabled = origin.IsEnabled = description.IsEnabled = consent.IsEnabled = true; }
        }
        private void ChooseCover()
        {
            var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false, Title = "Choisir la photo de couverture" };
            if (picker.ShowDialog(this) != true) return;
            coverPath = picker.FileName; coverName.Text = Path.GetFileName(coverPath) + " · compression automatique si nécessaire";
        }
    }

    internal sealed class CodexCommunityEditWindow : Window
    {
        private readonly JObject item; private readonly TextBox name = new TextBox { MaxLength = 120 }, description = new TextBox { MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120 }; private readonly ComboBox category = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key" }, origin = new ComboBox(); private bool saved;
        private CodexCommunityEditWindow(JObject item) { this.item = item; CommunityStyle.Apply(this); Title = "Modifier les informations"; Width = 560; Height = 560; var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel; panel.Children.Add(CommunityStyle.Text("Modifier les informations", 24)); panel.Children.Add(CommunityStyle.Text("Nom")); name.Text = (string)item["name"]; panel.Children.Add(name); panel.Children.Add(CommunityStyle.Text("Catégorie")); foreach (var entry in CommunityStyle.Categories) if (entry.Key != "") category.Items.Add(entry); category.SelectedValue = (string)item["category"]; panel.Children.Add(category); panel.Children.Add(CommunityStyle.Text("Origine")); origin.Items.Add("Famille IA"); origin.Items.Add("Famille personnelle"); origin.SelectedIndex = (string)item["origin"] == "personal" ? 1 : 0; panel.Children.Add(origin); panel.Children.Add(CommunityStyle.Text("Description")); description.Text = (string)item["description"] ?? ""; panel.Children.Add(description); var save = CommunityStyle.Button("Enregistrer", true); panel.Children.Add(save); save.Click += async (_, __) => { if (string.IsNullOrWhiteSpace(name.Text) || category.SelectedValue == null) return; save.IsEnabled = false; try { using (var service = new CodexCommunityLibrary()) await service.EditMetadata(item, name.Text.Trim(), (string)category.SelectedValue, description.Text.Trim(), origin.SelectedIndex == 0 ? "ai" : "personal"); saved = true; Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Modification impossible", MessageBoxButton.OK, MessageBoxImage.Warning); save.IsEnabled = true; } }; }
        internal static Task<bool> ShowAsync(Window owner, JObject item) { var dialog = new CodexCommunityEditWindow(item) { Owner = owner }; dialog.ShowDialog(); return Task.FromResult(dialog.saved); }
    }

    internal sealed class CodexCommunityHistoryWindow : Window
    {
        internal CodexCommunityHistoryWindow(JArray versions) { CommunityStyle.Apply(this); Title = "Historique des versions"; Width = 600; Height = 580; var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(CommunityStyle.Text("Historique des versions", 24)); foreach (JObject version in versions) { var card = new StackPanel(); card.Children.Add(CommunityStyle.Text("Révision " + (version.Value<int?>("revisionNumber") ?? 1) + " · Revit " + version["revitVersion"], 17)); card.Children.Add(CommunityStyle.Text((string)version["createdAt"], 11)); if (!string.IsNullOrWhiteSpace((string)version["changeNote"])) card.Children.Add(CommunityStyle.Text((string)version["changeNote"])); var download = CommunityStyle.Button("Télécharger cette version"); card.Children.Add(download); download.Click += async (_, __) => { try { if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(this)) return; using (var service = new CodexCommunityLibrary()) { string path = await service.Download(version); MessageBox.Show(this, "RFA disponible :\n" + path, "Téléchargement terminé", MessageBoxButton.OK, MessageBoxImage.Information); } } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Téléchargement impossible", MessageBoxButton.OK, MessageBoxImage.Warning); } }; panel.Children.Add(CommunityStyle.Card(card)); } }
    }
}
