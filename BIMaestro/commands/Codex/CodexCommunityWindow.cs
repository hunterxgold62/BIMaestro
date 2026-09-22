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
using BIMaestro.Localization;

namespace BIMaestro.Codex
{
    internal static class CommunityStyle
    {
        internal static readonly Dictionary<string, string> Categories = new Dictionary<string, string> {
            {"", "Toutes les catégories"}, {"generic", "Modèles génériques"}, {"furniture", "Mobilier"}, {"door", "Portes"}, {"window", "Fenêtres"},
            {"casework", "Agencement"}, {"plumbing_fixture", "Appareils sanitaires"}, {"mechanical_equipment", "Équipement mécanique"},
            {"electrical_equipment", "Équipement électrique"}, {"lighting_fixture", "Luminaires"}, {"pipe_accessory", "Accessoires de canalisation"}, {"pipe_fitting", "Raccords de canalisation"},
            {"duct_accessory", "Accessoires de gaine"}, {"duct_fitting", "Raccords de gaine"}, {"specialty_equipment", "Équipement spécialisé"}, {"structural_column", "Poteaux porteurs"} };
        internal static string L(string text) => UiLanguage.T(text);
        internal static string Category(string key) => key != null && Categories.TryGetValue(key, out var label) ? L(label) : key ?? L("Non classée");
        internal static string Origin(string key) => L(key == "ai" ? "Créée avec IA" : key == "personal" ? "Famille personnelle" : "Origine non renseignée");
        internal static void Apply(Window window)
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BIMaestro;component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            window.SetResourceReference(Window.BackgroundProperty, "App.Background"); window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");
            window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 13; window.UseLayoutRounding = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        internal static Button Button(string text, bool primary = false)
        { var b = new Button { Content = L(text), Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 8, 12, 8) }; b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "SecondaryButton"); return b; }
        internal static TextBlock Text(string text, int size = 13)
        { var t = new TextBlock { Text = L(text), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); return t; }
        internal static Border Card(UIElement content)
        { var b = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), BorderThickness = new Thickness(1) }; b.SetResourceReference(Border.BackgroundProperty, "Surface"); b.SetResourceReference(Border.BorderBrushProperty, "Border"); return b; }
        internal static async Task<string> SaveDownloadedFamilyAsync(Window owner, JObject item)
        {
            var picker = new SaveFileDialog {
                Title = L("Enregistrer la famille Revit"), Filter = "Famille Revit (*.rfa)|*.rfa",
                DefaultExt = ".rfa", AddExtension = true, OverwritePrompt = true,
                FileName = CodexCommunityLibrary.SuggestedFileName(item)
            };
            if (picker.ShowDialog(owner) != true) return null;
            using (var service = new CodexCommunityLibrary())
            {
                string cached = await service.Download(item);
                if (!string.Equals(Path.GetFullPath(cached), Path.GetFullPath(picker.FileName), StringComparison.OrdinalIgnoreCase))
                    File.Copy(cached, picker.FileName, true);
            }
            return picker.FileName;
        }
    }

    internal sealed class CodexCommunityWindow : Window
    {
        private readonly CodexRevitBridge bridge;
        private readonly Action<JObject> useAsBase;
        private readonly TextBox query = new TextBox { MaxLength = 120, MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
        private readonly ListBox categories = new ListBox { SelectedValuePath = "Key", BorderThickness = new Thickness(0) };
        private readonly ObservableCollection<KeyValuePair<string, string>> categoryItems = new ObservableCollection<KeyValuePair<string, string>>();
        private readonly ComboBox origin = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 12, 0) };
        private readonly CheckBox mine = new CheckBox { Content = "Mes publications", VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Dictionary<Button, FrameworkElement> galleryPreviews = new Dictionary<Button, FrameworkElement>();
        private readonly StackPanel details = new StackPanel();
        private readonly TextBlock status = CommunityStyle.Text("Recherchez ou parcourez les familles de la communauté.");
        private readonly Button search = CommunityStyle.Button("Rechercher", true), more = CommunityStyle.Button("Afficher la suite"), share = CommunityStyle.Button("Partager une famille…"), download = CommunityStyle.Button("Télécharger le RFA"), load = CommunityStyle.Button("Charger dans le projet", true), openAsBase = CommunityStyle.Button("Utiliser comme base dans Famille IA"), remove = CommunityStyle.Button("Retirer ma publication"), changeCover = CommunityStyle.Button("Modifier la photo de couverture…"), editDetails = CommunityStyle.Button("Modifier les informations…"), newVersion = CommunityStyle.Button("Publier une nouvelle version…"), history = CommunityStyle.Button("Historique des versions…");
        private string cursor, activeQuery = "", activeCategory = "", activeOrigin = "";
        private bool activeMine, busy, initialized;
        private JObject selected;
        private Border selectedCard;
        private double galleryWidth;
        private int count;
        internal CodexCommunityWindow(CodexRevitBridge bridge, string initialQuery = null, Action<JObject> useAsBase = null)
        {
            this.bridge = bridge; this.useAsBase = useAsBase; CommunityStyle.Apply(this);
            if (!string.IsNullOrWhiteSpace(initialQuery)) query.Text = initialQuery;
            Title = "BIMaestro — Bibliothèque commune"; Width = 1500; Height = 880; MinWidth = 1100; MinHeight = 700;
            var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
            var heading = new DockPanel { Margin = new Thickness(4, 2, 4, 10) }; DockPanel.SetDock(share, Dock.Right); heading.Children.Add(share);
            var titles = new StackPanel(); var title = CommunityStyle.Text("Bibliothèque commune", 28); title.FontWeight = FontWeights.SemiBold; titles.Children.Add(title); var subtitle = CommunityStyle.Text(CommunityStyle.L("Découvrez, partagez et mettez à jour les familles de la communauté · Revit 2023 à ") + bridge.RevitVersion); subtitle.Opacity = 0.72; titles.Children.Add(subtitle); heading.Children.Add(titles); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            var filters = new DockPanel { Margin = new Thickness(0, 4, 0, 12) }; query.ToolTip = "Rechercher par nom ou description"; DockPanel.SetDock(search, Dock.Right); filters.Children.Add(search);
            var options = new StackPanel { Orientation = Orientation.Horizontal }; origin.Items.Add(CommunityStyle.L("Toutes les origines")); origin.Items.Add(CommunityStyle.L("Personnelles")); origin.Items.Add(CommunityStyle.L("Créées avec IA")); origin.SelectedIndex = 0; options.Children.Add(origin); options.Children.Add(mine); DockPanel.SetDock(options, Dock.Right); filters.Children.Add(options); filters.Children.Add(query);
            var filterCard = CommunityStyle.Card(filters); filterCard.Padding = new Thickness(14, 9, 6, 9); filterCard.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(filterCard, Dock.Top); root.Children.Add(filterCard); status.Margin = new Thickness(6, 8, 0, 0); status.Opacity = 0.72; DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) }); root.Children.Add(columns);
            var side = new DockPanel(); var catTitle = CommunityStyle.Text("CATÉGORIES", 12); DockPanel.SetDock(catTitle, Dock.Top); side.Children.Add(catTitle); side.Children.Add(categories);
            foreach (var entry in CommunityStyle.Categories) categoryItems.Add(new KeyValuePair<string, string>(entry.Key, CommunityStyle.L(entry.Value)));
            var categoryText = new FrameworkElementFactory(typeof(TextBlock)); categoryText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value")); categoryText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            categories.ItemTemplate = new DataTemplate { VisualTree = categoryText };
            ScrollViewer.SetHorizontalScrollBarVisibility(categories, ScrollBarVisibility.Disabled);
            categories.ItemsSource = categoryItems; categories.SelectedIndex = 0; var sideCard = CommunityStyle.Card(side); sideCard.Padding = new Thickness(12); columns.Children.Add(sideCard);
            var center = new DockPanel(); DockPanel.SetDock(more, Dock.Bottom); center.Children.Add(more);
            var galleryScroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            galleryScroll.SizeChanged += (_, e) => ResizeGallery(e.NewSize.Width);
            center.Children.Add(galleryScroll); Grid.SetColumn(center, 1); columns.Children.Add(center);
            var right = CommunityStyle.Card(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); right.Margin = new Thickness(8, 0, 0, 12); Grid.SetColumn(right, 2); columns.Children.Add(right); Select(null);
            search.Click += async (_, __) => await Refresh(); query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Refresh(); };
            categories.SelectionChanged += async (_, __) => { if (initialized) await Refresh(); };
            origin.SelectionChanged += async (_, __) => { if (initialized) await Refresh(); };
            mine.Checked += async (_, __) => { if (initialized) await Refresh(); }; mine.Unchecked += async (_, __) => { if (initialized) await Refresh(); };
            more.Click += async (_, __) => await Run(() => Search(false));
            download.Click += async (_, __) => await Run(() => Download(false)); load.Click += async (_, __) => await Run(() => Download(true));
            openAsBase.Click += async (_, __) => await Run(OpenAsBase);
            remove.Click += async (_, __) => await Run(Remove);
            changeCover.Click += async (_, __) => await Run(ChangeCover);
            editDetails.Click += async (_, __) => await Run(EditDetails);
            newVersion.Click += async (_, __) => await Run(PublishNewVersion);
            history.Click += async (_, __) => await Run(ShowHistory);
            share.Click += async (_, __) => await Run(async () => { if (await CodexCommunityPublishWindow.ShowAsync(this, bridge)) { cursor = null; await Search(true); } else status.Text = CommunityStyle.L("Publication annulée."); });
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
            try { status.Text = CommunityStyle.L("Connexion à la bibliothèque…"); await action(); }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? CommunityStyle.L("Le service ne répond pas. Réessayez plus tard.") : ex.Message; }
            finally { busy = false; SetControls(); }
        }
        private void SetControls()
        {
            search.IsEnabled = share.IsEnabled = query.IsEnabled = categories.IsEnabled = origin.IsEnabled = mine.IsEnabled = !busy;
            more.IsEnabled = !busy && !string.IsNullOrEmpty(cursor); download.IsEnabled = load.IsEnabled = !busy && selected != null; remove.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true;
            openAsBase.IsEnabled = !busy && selected != null && useAsBase != null;
            changeCover.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true;
            editDetails.IsEnabled = newVersion.IsEnabled = !busy && selected?.Value<bool?>("isOwner") == true; history.IsEnabled = !busy && selected != null;
        }
        private void ResizeGallery(double viewportWidth)
        {
            if (viewportWidth < 1) return;
            galleryWidth = Math.Max(210, viewportWidth - 20);
            int columns = galleryWidth >= 1040 ? 4 : galleryWidth >= 650 ? 3 : galleryWidth >= 430 ? 2 : 1;
            double itemWidth = galleryWidth / columns;
            cards.Width = galleryWidth;
            cards.ItemWidth = itemWidth;
            foreach (var entry in galleryPreviews)
            {
                entry.Key.Width = itemWidth - 12;
                entry.Value.Height = Math.Max(170, Math.Min(270, itemWidth * 0.82));
            }
        }
        private async Task Search(bool clear)
        {
            using (var service = new CodexCommunityLibrary())
            {
                var page = await service.Search(activeQuery, bridge.RevitVersion, cursor, activeCategory, activeOrigin, activeMine);
                if (clear) { cards.Children.Clear(); galleryPreviews.Clear(); count = 0; Select(null); }
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
                    var body = new StackPanel();
                    var preview = CreatePreview(item, double.NaN, 215);
                    preview.Margin = new Thickness(0);
                    body.Children.Add(preview);
                    var caption = new Border { Padding = new Thickness(12, 9, 12, 11) };
                    var familyName = CommunityStyle.Text((string)item["name"], 15);
                    familyName.FontWeight = FontWeights.SemiBold; familyName.Margin = new Thickness(0); familyName.MaxHeight = 44;
                    caption.Child = familyName; body.Children.Add(caption);
                    var card = CommunityStyle.Card(body); card.Padding = new Thickness(0); card.Margin = new Thickness(6, 0, 6, 14); card.ClipToBounds = true;
                    var button = new Button { Content = card, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = (string)item["name"] };
                    button.Click += (_, __) => Select(item, card);
                    galleryPreviews.Add(button, preview); cards.Children.Add(button); count++;
                }
                ResizeGallery(galleryWidth > 0 ? galleryWidth + 20 : 800);
                cursor = (string)page["nextCursor"]; status.Text = count + CommunityStyle.L(" famille(s) affichée(s).") + (count == 0 ? CommunityStyle.L(" Aucune famille dans cette sélection.") : "") + (!string.IsNullOrEmpty(cursor) ? CommunityStyle.L(" D'autres résultats peuvent être disponibles avec « Afficher la suite ».") : "");
            }
        }
        private void Select(JObject item, Border card = null)
        {
            if (selectedCard != null) { selectedCard.BorderThickness = new Thickness(1); selectedCard.SetResourceReference(Border.BorderBrushProperty, "Border"); }
            selectedCard = card;
            if (selectedCard != null) { selectedCard.BorderThickness = new Thickness(2); selectedCard.SetResourceReference(Border.BorderBrushProperty, "Brand"); }
            selected = item; details.Children.Clear(); details.Children.Add(CommunityStyle.Text("DÉTAILS", 12));
            if (item == null) { details.Children.Add(CommunityStyle.Text("Sélectionnez une famille pour consulter sa fiche et la charger dans votre projet.")); SetControls(); return; }
            var name = CommunityStyle.Text((string)item["name"], 21); name.FontWeight = FontWeights.SemiBold; details.Children.Add(name);
            details.Children.Add(CreatePreview(item, double.NaN, 230));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.Category((string)item["category"])));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.Origin((string)item["origin"])));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.L("Créateur : ") + ((string)item["creatorName"] ?? CommunityStyle.L("Non renseigné"))));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.L("Version d'origine : Revit ") + item["revitVersion"]));
            details.Children.Add(CommunityStyle.Text(CommunityStyle.L("Révision ") + (item.Value<int?>("revisionNumber") ?? 1) + "  ·  " + ((item.Value<long?>("sizeBytes") ?? 0) / 1000000d).ToString("0.0") + CommunityStyle.L(" Mo")));
            details.Children.Add(CommunityStyle.Text((item.Value<long?>("downloadCount") ?? 0).ToString("N0") + CommunityStyle.L(" téléchargement(s)")));
            if (!string.IsNullOrWhiteSpace((string)item["changeNote"])) details.Children.Add(CommunityStyle.Text(CommunityStyle.L("Modifications : ") + (string)item["changeNote"], 11));
            details.Children.Add(CommunityStyle.Text((string)item["description"] ?? "Aucune description.")); if (useAsBase != null) details.Children.Add(openAsBase); details.Children.Add(load); details.Children.Add(download);
            details.Children.Add(history);
            if (item.Value<bool?>("isOwner") == true) { details.Children.Add(editDetails); details.Children.Add(newVersion); details.Children.Add(changeCover); details.Children.Add(remove); }
            SetControls();
        }
        private static FrameworkElement CreatePreview(JObject item, double width, double height)
        {
            var preview = new Grid { Width = width, Height = height, Margin = new Thickness(0, 0, 0, 10), Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)) };
            var fallback = CommunityStyle.Text("APERÇU\nNON DISPONIBLE", 12); fallback.TextAlignment = TextAlignment.Center; fallback.VerticalAlignment = VerticalAlignment.Center; fallback.Opacity = 0.55; preview.Children.Add(fallback);
            if (Uri.TryCreate((string)item?["previewUrl"], UriKind.Absolute, out var uri))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = uri; bitmap.DecodePixelWidth = (int)(double.IsNaN(width) ? 640 : width * 2); bitmap.CacheOption = BitmapCacheOption.OnDemand; bitmap.EndInit();
                preview.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
            return preview;
        }
        private async Task Download(bool intoProject)
        {
            if (selected == null) return;
            if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(this)) { status.Text = CommunityStyle.L("Téléchargement annulé : profil non renseigné."); return; }
            if (!intoProject)
            {
                string saved = await CommunityStyle.SaveDownloadedFamilyAsync(this, selected);
                status.Text = saved == null ? CommunityStyle.L("Téléchargement annulé.") : CommunityStyle.L("RFA enregistré : ") + saved;
                return;
            }
            using (var service = new CodexCommunityLibrary())
            {
                string path = await service.Download(selected);
                await bridge.LoadCommunityFamilyAsync(path);
                status.Text = CommunityStyle.L("Famille chargée dans le projet. Aucune instance placée.");
            }
        }
        private async Task Remove()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (MessageBox.Show(this, CommunityStyle.L("Retirer « ") + (string)selected["name"] + CommunityStyle.L(" » de la bibliothèque commune ?\nLes copies déjà téléchargées resteront sur les ordinateurs de leurs utilisateurs."), CommunityStyle.L("Retirer ma publication"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) { status.Text = CommunityStyle.L("Retrait annulé."); return; }
            using (var service = new CodexCommunityLibrary()) await service.Remove((string)selected["id"]);
            cursor = null; await Search(true); status.Text = CommunityStyle.L("Publication retirée de la bibliothèque.");
        }
        private async Task OpenAsBase()
        {
            if (selected == null || useAsBase == null) return;
            if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(this)) { status.Text = CommunityStyle.L("Ouverture annulée : profil non renseigné."); return; }
            using (var service = new CodexCommunityLibrary())
            {
                string path = await service.Download(selected);
                await bridge.OpenCommunityFamilyAsync(path);
            }
            useAsBase(selected);
            status.Text = CommunityStyle.L("Copie de la famille ouverte dans Revit. Vérifiez les paramètres avant toute modification.");
            Close();
        }
        private async Task ChangeCover()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false, Title = "Choisir la photo de couverture" };
            if (picker.ShowDialog(this) != true) { status.Text = CommunityStyle.L("Modification de la photo annulée."); return; }
            using (var service = new CodexCommunityLibrary()) await service.UpdatePreview(selected, picker.FileName);
            Select(selected); status.Text = CommunityStyle.L("Photo de couverture mise à jour.");
        }
        private async Task EditDetails()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (await CodexCommunityEditWindow.ShowAsync(this, selected)) { Select(selected); status.Text = CommunityStyle.L("Informations mises à jour."); }
        }
        private async Task PublishNewVersion()
        {
            if (selected?.Value<bool?>("isOwner") != true) return;
            if (await CodexCommunityPublishWindow.ShowAsync(this, bridge, null, (string)selected["origin"], selected)) { cursor = null; await Search(true); status.Text = CommunityStyle.L("Nouvelle version publiée."); }
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
            panel.Children.Add(CommunityStyle.Text("Catégorie")); foreach (var entry in CommunityStyle.Categories) if (entry.Key != "") category.Items.Add(new KeyValuePair<string, string>(entry.Key, CommunityStyle.L(entry.Value)));
            string categoryKey = (string)replaces?["category"] ?? (string)metadata["category"] ?? "generic";
            foreach (var entry in CommunityStyle.Categories) if (string.Equals(entry.Value, categoryKey, StringComparison.OrdinalIgnoreCase)) { categoryKey = entry.Key; break; }
            if (!CommunityStyle.Categories.ContainsKey(categoryKey)) category.Items.Add(new KeyValuePair<string, string>(categoryKey, categoryKey));
            category.SelectedValue = categoryKey; panel.Children.Add(category);
            panel.Children.Add(CommunityStyle.Text("Origine")); origin.Items.Add(CommunityStyle.L("Famille IA")); origin.Items.Add(CommunityStyle.L("Famille personnelle")); origin.SelectedIndex = source == "personal" ? 1 : 0; panel.Children.Add(origin);
            panel.Children.Add(CommunityStyle.Text("Photo de couverture")); coverName.Text = string.IsNullOrEmpty(coverPath) ? "Aucune vignette trouvée — choisissez une image." : "Vignette du fichier sélectionné"; panel.Children.Add(coverName); panel.Children.Add(chooseCover);
            panel.Children.Add(CommunityStyle.Text("Description")); description.Text = (string)replaces?["description"] ?? ""; panel.Children.Add(description); if (replaces != null) { panel.Children.Add(CommunityStyle.Text("Résumé des modifications")); panel.Children.Add(changeNote); } panel.Children.Add(consent);
            panel.Children.Add(CommunityStyle.Text("Sans compte : le droit de retrait est conservé dans ce profil Windows. Il ne sera pas disponible depuis un autre ordinateur ou après la perte de cette identité locale.", 11)); panel.Children.Add(publish); panel.Children.Add(status);
            chooseCover.Click += (_, __) => ChooseCover(); publish.Click += async (_, __) => await Publish(); Closing += (_, e) => { if (sending) { e.Cancel = true; return; } try { if (!string.IsNullOrEmpty(generatedPreviewPath) && File.Exists(generatedPreviewPath)) File.Delete(generatedPreviewPath); } catch { } };
        }
        internal static async Task<bool> ShowAsync(Window owner, CodexRevitBridge bridge, string filePath = null, string origin = "ai", JObject replaces = null)
        {
            if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(owner)) return false;
            if (string.IsNullOrEmpty(filePath)) { var picker = new OpenFileDialog { Filter = "Famille Revit (*.rfa)|*.rfa", CheckFileExists = true, Multiselect = false }; if (picker.ShowDialog(owner) != true) return false; filePath = picker.FileName; }
            var metadata = await bridge.InspectCommunityFamilyAsync(filePath);
            metadata["previewPath"] = await Task.Run(() => CreateShellPreview(filePath));
            var dialog = new CodexCommunityPublishWindow(metadata, origin, replaces) { Owner = owner }; dialog.ShowDialog(); return dialog.published;
        }
        private static string CreateShellPreview(string filePath)
        {
            try
            {
                if (!Famille.ShellThumbnailProvider.TryGetThumbnail(filePath, 512, out var thumbnail)) return null;
                string folder = Path.Combine(CodexClient.DataDirectory, "CommunityPreviewDrafts");
                Directory.CreateDirectory(folder);
                string previewPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(thumbnail));
                using (var stream = File.Create(previewPath)) encoder.Save(stream);
                return previewPath;
            }
            catch { return null; }
        }
        private async Task Publish()
        {
            if (sending) return;
            if (consent.IsChecked != true || string.IsNullOrWhiteSpace(name.Text) || category.SelectedValue == null) { status.Text = CommunityStyle.L("Renseignez le nom, la catégorie et votre accord de partage."); return; }
            sending = true; publish.IsEnabled = false; name.IsEnabled = category.IsEnabled = origin.IsEnabled = description.IsEnabled = consent.IsEnabled = false;
            try
            {
                status.Text = CommunityStyle.L("Envoi en cours…");
                using (var service = new CodexCommunityLibrary())
                {
                    var result = await service.Publish(path, name.Text.Trim(), (string)category.SelectedValue, description.Text.Trim(), version, origin.SelectedIndex == 0 ? "ai" : "personal", coverPath, (string)replaces?["id"], changeNote.Text.Trim());
                    if (result.Value<bool?>("restored") == true) MessageBox.Show(this, CommunityStyle.L("Votre famille retirée a été remise dans la bibliothèque."), CommunityStyle.L("Famille réactivée"), MessageBoxButton.OK, MessageBoxImage.Information);
                    else if (result.Value<bool?>("duplicate") == true) MessageBox.Show(this, CommunityStyle.L("Ce fichier existe déjà dans la bibliothèque. La publication existante a été conservée ; ses droits de retrait ne sont pas transférés."), CommunityStyle.L("Famille déjà disponible"), MessageBoxButton.OK, MessageBoxImage.Information);
                    if (result.Value<bool?>("previewUploaded") == false)
                        MessageBox.Show(this, CommunityStyle.L("La famille a bien été publiée, mais son aperçu n’a pas pu être envoyé.\n\n") + ((string)result["previewError"] ?? CommunityStyle.L("Service d’aperçu indisponible.")), CommunityStyle.L("Aperçu non publié"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    published = true;
                }
                sending = false; Close();
            }
            catch (Exception ex) { status.Text = ex is OperationCanceledException ? CommunityStyle.L("Délai dépassé. Vérifiez la bibliothèque avant de renvoyer le fichier.") : ex.Message; }
            finally { sending = false; publish.IsEnabled = true; name.IsEnabled = category.IsEnabled = origin.IsEnabled = description.IsEnabled = consent.IsEnabled = true; }
        }
        private void ChooseCover()
        {
            var picker = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false, Title = "Choisir la photo de couverture" };
            if (picker.ShowDialog(this) != true) return;
            coverPath = picker.FileName; coverName.Text = Path.GetFileName(coverPath) + CommunityStyle.L(" · compression automatique si nécessaire");
        }
    }

    internal sealed class CodexCommunityEditWindow : Window
    {
        private readonly JObject item; private readonly TextBox name = new TextBox { MaxLength = 120 }, description = new TextBox { MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120 }; private readonly ComboBox category = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key" }, origin = new ComboBox(); private bool saved;
        private CodexCommunityEditWindow(JObject item) { this.item = item; CommunityStyle.Apply(this); Title = "Modifier les informations"; Width = 560; Height = 560; var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel; panel.Children.Add(CommunityStyle.Text("Modifier les informations", 24)); panel.Children.Add(CommunityStyle.Text("Nom")); name.Text = (string)item["name"]; panel.Children.Add(name); panel.Children.Add(CommunityStyle.Text("Catégorie")); foreach (var entry in CommunityStyle.Categories) if (entry.Key != "") category.Items.Add(new KeyValuePair<string, string>(entry.Key, CommunityStyle.L(entry.Value))); category.SelectedValue = (string)item["category"]; panel.Children.Add(category); panel.Children.Add(CommunityStyle.Text("Origine")); origin.Items.Add(CommunityStyle.L("Famille IA")); origin.Items.Add(CommunityStyle.L("Famille personnelle")); origin.SelectedIndex = (string)item["origin"] == "personal" ? 1 : 0; panel.Children.Add(origin); panel.Children.Add(CommunityStyle.Text("Description")); description.Text = (string)item["description"] ?? ""; panel.Children.Add(description); var save = CommunityStyle.Button("Enregistrer", true); panel.Children.Add(save); save.Click += async (_, __) => { if (string.IsNullOrWhiteSpace(name.Text) || category.SelectedValue == null) return; save.IsEnabled = false; try { using (var service = new CodexCommunityLibrary()) await service.EditMetadata(item, name.Text.Trim(), (string)category.SelectedValue, description.Text.Trim(), origin.SelectedIndex == 0 ? "ai" : "personal"); saved = true; Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, CommunityStyle.L("Modification impossible"), MessageBoxButton.OK, MessageBoxImage.Warning); save.IsEnabled = true; } }; }
        internal static Task<bool> ShowAsync(Window owner, JObject item) { var dialog = new CodexCommunityEditWindow(item) { Owner = owner }; dialog.ShowDialog(); return Task.FromResult(dialog.saved); }
    }

    internal sealed class CodexCommunityHistoryWindow : Window
    {
        internal CodexCommunityHistoryWindow(JArray versions)
        {
            CommunityStyle.Apply(this); Title = "Historique des versions"; Width = 600; Height = 580;
            var panel = new StackPanel { Margin = new Thickness(24) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(CommunityStyle.Text("Historique des versions", 24));
            foreach (JObject version in versions)
            {
                var card = new StackPanel();
                card.Children.Add(CommunityStyle.Text("Révision " + (version.Value<int?>("revisionNumber") ?? 1) + " · Revit " + version["revitVersion"], 17));
                card.Children.Add(CommunityStyle.Text((string)version["createdAt"], 11));
                if (!string.IsNullOrWhiteSpace((string)version["changeNote"])) card.Children.Add(CommunityStyle.Text((string)version["changeNote"]));
                var download = CommunityStyle.Button("Télécharger cette version"); card.Children.Add(download);
                download.Click += async (_, __) =>
                {
                    try
                    {
                        if (!BIMaestro.Welcome.WelcomeManager.EnsureCommunityProfile(this)) return;
                        string path = await CommunityStyle.SaveDownloadedFamilyAsync(this, version);
                        if (path != null) MessageBox.Show(this, "RFA enregistré :\n" + path, "Téléchargement terminé", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "Téléchargement impossible", MessageBoxButton.OK, MessageBoxImage.Warning); }
                };
                panel.Children.Add(CommunityStyle.Card(card));
            }
        }
    }
}
