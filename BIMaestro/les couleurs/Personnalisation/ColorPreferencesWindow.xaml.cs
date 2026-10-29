using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMaestro.Localization;
using Autodesk.Revit.UI;
using BrowserOrganization = Autodesk.Revit.DB.BrowserOrganization;
using Document = Autodesk.Revit.DB.Document;
using FilteredElementCollector = Autodesk.Revit.DB.FilteredElementCollector;
using FolderItemInfo = Autodesk.Revit.DB.FolderItemInfo;
using ViewSheet = Autodesk.Revit.DB.ViewSheet;

namespace Couleur
{
    public partial class ColorPreferencesWindow :
        Window,
        INotifyPropertyChanged
    {
        private readonly System.IntPtr _mainWindowHandle;
        private readonly Document _document;
        private readonly BIMaestro.PropertiesAppearance.PropertiesAppearanceEditor _propertiesEditor;
        private readonly BIMaestro.Home.HomeAppearanceEditor _homeEditor;
        private bool _savingAppearance;
        private readonly bool _canApplyBrowserToProject;
        private bool _hasSharedBrowserAppearance;
        private readonly BrowserProjectWriteHandler _projectWriteHandler;
        private readonly ExternalEvent _projectWriteEvent;
        private string _selectedPresetName;
        private ProjectBrowserColorSettings _browserPreferences;
        private readonly System.Random _previewRandom = new System.Random();
        private string _browserPreviewPrimaryViewName;
        private string _browserPreviewSecondaryViewName;
        private string _browserPreviewSectionName;
        private bool _isUpdatingBrowserColoringMode;
        private ProjectBrowserColorProfile _selectedBrowserColorProfile;
        private string _newBrowserColorProfileName = string.Empty;
        private bool _areColoredPanelsEnabled;
        private bool _useFullPanelColoring;
        private readonly System.Collections.Generic.Stack<BrowserRuleUndoState> _browserRuleUndo =
            new System.Collections.Generic.Stack<BrowserRuleUndoState>();

        private sealed class BrowserRuleUndoState
        {
            public ProjectBrowserColorSettings Settings;
            public bool WasShared;
        }

        public ColorPreferencesWindow(
            System.IntPtr mainWindowHandle,
            Document document = null,
            UIApplication application = null)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            AppearanceTabs.SelectionChanged += (_, args) =>
            {
                if (ReferenceEquals(args.Source, AppearanceTabs) && ViewsAndFoldersTab.IsSelected)
                    BrowserTabs.SelectedItem = BrowserBackgroundTab;
            };
            ViewsAndFoldersTab.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (new Rect(ViewsAndFoldersTab.RenderSize).Contains(args.GetPosition(ViewsAndFoldersTab)))
                    BrowserTabs.SelectedItem = BrowserBackgroundTab;
            };

            _mainWindowHandle = mainWindowHandle;
            _document = document;
            if (application != null)
            {
                _propertiesEditor = BIMaestro.PropertiesAppearance.PropertiesAppearanceCommand.CreateEditor(application);
                _propertiesEditor.SaveBackupRequested = () => ExportCurrentPack(false, false,
                    UiLanguage.T("Sauvegarder mes propriétés avant réinitialisation", "Back up my properties before reset"), UiLanguage.T("Mes propriétés avant réinitialisation.bimaestro-style.json", "My properties before reset.bimaestro-style.json"), true, false);
                PropertiesEditorHost.Content = _propertiesEditor;
                _homeEditor = new BIMaestro.Home.HomeAppearanceEditor();
                HomeEditorHost.Content = _homeEditor;
                _homeEditor.ShowHomeRequested += ShowHome;
            }
            else
            {
                PropertiesTab.IsEnabled = false;
                HomeTab.IsEnabled = false;
            }
            bool browserSourceChanged = document != null &&
                ProjectBrowserProjectStorage.Activate(document);
            _canApplyBrowserToProject = document != null && !document.IsReadOnly && !document.IsFamilyDocument;
            _hasSharedBrowserAppearance = document != null && ProjectBrowserProjectStorage.HasAppearance(document);
            _areColoredPanelsEnabled =
                ColoringStateManager.IsColoringActive;
            _useFullPanelColoring =
                ColoringStateManager.IsFullMode;
            PanelColors = CreateItems(RibbonColorPreferences.Load());
            BrowserPreferences =
                ProjectBrowserColorPreferences.Load();
            if (browserSourceChanged)
            {
                ProjectBrowserColoring.Reset();
                ProjectBrowserColoring.Apply(_mainWindowHandle);
            }
            RefreshBrowserProfiles();
            PreferenceFilePath = UiLanguage.T("Sauvegarde : ", "Saved at: ") +
                RibbonColorPreferences.PreferenceFilePath;
            PresetEntries = RibbonColorPresetCatalog.StandardPresetNames
                .Select(name => new PresetMenuEntry(name, false))
                .Concat(new[] { new PresetMenuEntry("Animé", true) })
                .Concat(
                    RibbonColorPresetCatalog.AnimatedPresetNames
                        .Select(name => new PresetMenuEntry(name, false)))
                .ToList()
                .AsReadOnly();
            _selectedPresetName =
                RibbonColorPresetCatalog.StandardPresetNames.FirstOrDefault();
            GenerateBrowserPreviewNames();
            DetectBrowserCategories();
            _projectWriteHandler = new BrowserProjectWriteHandler();
            _projectWriteEvent = ExternalEvent.Create(_projectWriteHandler);
            _projectWriteHandler.Event = _projectWriteEvent;
            Closing += (_, args) =>
            {
                if (_projectWriteHandler.HasPending || _savingAppearance)
                {
                    args.Cancel = true;
                    MessageBox.Show(this, UiLanguage.T("Patientez : Revit termine l’enregistrement des réglages.", "Please wait: Revit is finishing saving the settings."), UiLanguage.T("Enregistrement en cours", "Saving in progress"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            Closed += (_, __) =>
            {
                if (_propertiesEditor != null)
                    BIMaestro.PropertiesAppearance.PropertiesAppearanceCommand.ReleaseEditor(_propertiesEditor);
                _projectWriteHandler.DisposeWhenIdle = true;
                if (!_projectWriteHandler.HasPending)
                    _projectWriteHandler.DisposeEventOnce();
            };
            DataContext = this;
        }

        internal void SelectAppearanceSection(string section)
        {
            if (section == "properties") AppearanceTabs.SelectedItem = PropertiesTab;
            else if (section == "home") AppearanceTabs.SelectedItem = HomeTab;
        }

        private void AppearanceActionFailed(System.Exception error)
        {
            SetSavingAppearance(false);
            ShowSaveError(error);
        }

        private void SetSavingAppearance(bool saving)
        {
            _savingAppearance = saving;
            AppearanceTabs.IsEnabled = !saving;
            TutorialSave.IsEnabled = !saving;
        }

        private void ShowHome()
        {
            SaveSettingsAndContinue(() =>
            {
                SetSavingAppearance(true);
                BIMaestro.PropertiesAppearance.PropertiesAppearanceCommand.Request(app =>
                {
                    try
                    {
                        var command = RevitCommandId.LookupPostableCommandId(PostableCommand.ToggleHome);
                        SetSavingAppearance(false);
                        if (!app.CanPostCommand(command))
                        {
                            MessageBox.Show(this, UiLanguage.T("Vos réglages sont enregistrés. Revit ne peut pas afficher l’accueil pour le moment.", "Your settings are saved. Revit cannot display Home right now."), UiLanguage.T("Accueil Revit", "Revit Home"), MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        app.PostCommand(command);
                        Close();
                    }
                    catch (System.Exception ex) { AppearanceActionFailed(ex); }
                });
            });
        }

        private void SaveAppearanceEditorsAndContinue(System.Action next)
        {
            SetSavingAppearance(true);
            System.Action complete = () =>
            {
                try
                {
                    if (_homeEditor != null && _homeEditor.HasChanges) _homeEditor.SaveChanges();
                    SetSavingAppearance(false);
                    next();
                }
                catch (System.Exception ex) { AppearanceActionFailed(ex); }
            };
            try
            {
                if (_propertiesEditor != null && _propertiesEditor.HasChanges) _propertiesEditor.SaveChanges(complete, AppearanceActionFailed);
                else complete();
            }
            catch (System.Exception ex) { AppearanceActionFailed(ex); }
        }

        private sealed class BrowserProjectWriteRequest
        {
            public Document Document;
            public ProjectBrowserColorSettings Browser;
            public BrowserIconSettings Icons;
            public bool Clear;
            public System.Action<UIApplication> ApiAction;
            public System.Action<System.Exception> Completed;
            public System.Windows.Threading.Dispatcher Dispatcher;
        }

        private sealed class BrowserProjectWriteHandler : IExternalEventHandler
        {
            public BrowserProjectWriteRequest Pending;
            public ExternalEvent Event;
            public bool DisposeWhenIdle;
            private bool _isExecuting;
            private bool _isDisposed;
            public bool HasPending => Pending != null || _isExecuting;

            public void DisposeEventOnce()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                Event.Dispose();
            }

            public string GetName() => "BIMaestro - Apparence de l’arborescence du projet";

            public void Execute(UIApplication app)
            {
                BrowserProjectWriteRequest request = Pending;
                Pending = null;
                if (request == null) return;
                _isExecuting = true;
                System.Exception error = null;
                try
                {
                    Document active = app.ActiveUIDocument?.Document;
                    if (active == null || !active.Equals(request.Document))
                        throw new System.InvalidOperationException(UiLanguage.T("Le projet actif a changé. Revenez au projet initial puis réessayez.", "The active project has changed. Return to the original project and try again."));
                    if (request.ApiAction != null)
                        request.ApiAction(app);
                    else if (request.Clear)
                        ProjectBrowserProjectStorage.Clear(request.Document);
                    else
                    {
                        ProjectBrowserProjectStorage.Save(request.Document, request.Browser, request.Icons);
                        ProjectBrowserProjectStorage.Activate(request.Document);
                    }
                }
                catch (System.Exception ex) { error = ex; }
                request.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    _isExecuting = false;
                    try { request.Completed(error); }
                    finally { if (DisposeWhenIdle) DisposeEventOnce(); }
                }));
            }
        }

        private bool QueueProjectWrite(bool clear, ProjectBrowserColorSettings browser,
            BrowserIconSettings icons, System.Action<System.Exception> completed)
        {
            if (_projectWriteHandler.HasPending)
            {
                completed(new System.InvalidOperationException(UiLanguage.T("Une modification du projet est déjà en cours. Patientez un instant.", "A project change is already in progress. Please wait a moment.")));
                return false;
            }
            var request = new BrowserProjectWriteRequest
            {
                Document = _document,
                Browser = browser == null ? null : ProjectBrowserColorPreferences.Clone(browser),
                Icons = icons == null ? null : ProjectBrowserIcons.Clone(icons),
                Clear = clear,
                Completed = completed,
                Dispatcher = Dispatcher
            };
            return QueueExternalRequest(request);
        }

        private bool QueueBrowserRead(System.Action<UIApplication> action,
            System.Action<System.Exception> completed)
        {
            return QueueExternalRequest(new BrowserProjectWriteRequest
            {
                Document = _document,
                ApiAction = action,
                Completed = completed,
                Dispatcher = Dispatcher
            });
        }

        private bool QueueExternalRequest(BrowserProjectWriteRequest request)
        {
            if (_projectWriteHandler.HasPending)
            {
                request.Completed(new System.InvalidOperationException(UiLanguage.T("Une opération Revit est déjà en cours. Patientez un instant.", "A Revit operation is already in progress. Please wait a moment.")));
                return false;
            }
            _projectWriteHandler.Pending = request;
            ExternalEventRequest response = _projectWriteEvent.Raise();
            if (response == ExternalEventRequest.Accepted || response == ExternalEventRequest.Pending)
                return true;
            _projectWriteHandler.Pending = null;
            request.Completed(new System.InvalidOperationException(UiLanguage.T("Revit n’a pas accepté la demande. Réessayez lorsqu’il est disponible.", "Revit did not accept the request. Try again when it is available.")));
            return false;
        }

        public ObservableCollection<PanelColorItem> PanelColors { get; }

        public BrowserIconSettings BrowserIcons { get; private set; } = ProjectBrowserIcons.Load();
        public bool CanApplyBrowserToProject => _canApplyBrowserToProject;
        public bool HasSharedBrowserAppearance => _hasSharedBrowserAppearance;
        public string BrowserSourceLabel => HasSharedBrowserAppearance
            ? UiLanguage.T("Style partagé dans la maquette", "Style shared in the model")
            : UiLanguage.T("Mes réglages personnels", "My personal settings");
        public string BrowserSourceStatus => HasSharedBrowserAppearance
            ? UiLanguage.T("Source actuelle : style partagé dans cette maquette. « Enregistrer mes réglages » met aussi à jour les vues, dossiers et icônes de ce projet.", "Current source: style shared in this model. “Save my settings” also updates the views, folders and icons of this project.")
            : UiLanguage.T("Source actuelle : mes réglages personnels. Pour les partager, utilisez « Appliquer au projet ».", "Current source: my personal settings. To share them, use “Apply to project”.");

        private void NotifyBrowserSourceChanged(bool shared)
        {
            _hasSharedBrowserAppearance = shared;
            OnPropertyChanged(nameof(HasSharedBrowserAppearance));
            OnPropertyChanged(nameof(BrowserSourceLabel));
            OnPropertyChanged(nameof(BrowserSourceStatus));
        }
        private string _packStatus = UiLanguage.T("Sauvegardez ou restaurez les panneaux BIMaestro, l’arborescence et ses icônes, les propriétés et l’accueil Revit. Les profils et groupes personnalisés sont inclus.", "Back up or restore the BIMaestro panels, the Project Browser and its icons, properties and Revit Home. Profiles and custom groups are included.");
        public string PackStatus { get => _packStatus; private set { _packStatus = value; OnPropertyChanged(); } }

        private void ExportPack_Click(object sender, RoutedEventArgs e)
        {
            var scopeDialog = new AppearancePackScopeDialog { Owner = this };
            if (scopeDialog.ShowDialog() != true) return;
            try
            {
                ExportCurrentPack(scopeDialog.IncludesRibbon, scopeDialog.IncludesBrowser,
                    UiLanguage.T("Exporter un pack d’apparence", "Export an appearance pack"), UiLanguage.T("Mon pack.bimaestro-style.json", "My pack.bimaestro-style.json"), scopeDialog.IncludesProperties, scopeDialog.IncludesHome);
            }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, UiLanguage.T("Export du pack", "Pack export"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = UiLanguage.T("Importer un pack d’apparence", "Import an appearance pack"),
                Filter = UiLanguage.T("Pack BIMaestro|*.bimaestro-style.json|Fichier JSON|*.json", "BIMaestro pack|*.bimaestro-style.json|JSON file|*.json"), CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var pack = AppearancePackFile.Import(dialog.FileName);
                var scope = new AppearancePackScopeDialog(pack) { Owner = this };
                if (scope.ShowDialog() != true) return;
                if (!OfferBackup(UiLanguage.T("Avant l’import, voulez-vous enregistrer vos réglages actuels dans un pack de secours ?", "Before importing, do you want to save your current settings in a backup pack?"), UiLanguage.T("Import du pack", "Pack import")))
                    return;
                if (scope.IncludesRibbon)
                {
                    foreach (var item in PanelColors)
                        if (pack.Ribbon.TryGetValue(item.PanelName, out var scheme)) item.ApplyScheme(scheme);
                    AreColoredPanelsEnabled = pack.RibbonEnabled;
                    UseFullPanelColoring = pack.FullPanels;
                }
                if (scope.IncludesBrowser)
                {
                    BrowserPreferences = pack.Browser;
                    BrowserIcons = pack.Icons;
                    _browserIconAssets = null;
                    OnPropertyChanged(nameof(BrowserIcons));
                    OnPropertyChanged(nameof(BrowserIconAssets));
                }
                if (scope.IncludesProperties) _propertiesEditor.LoadPreferences(pack.Properties);
                if (scope.IncludesHome) _homeEditor.LoadSettings(pack.Home);
                PackStatus = UiLanguage.T("Pack chargé : ", "Pack loaded:") + System.IO.Path.GetFileName(dialog.FileName) + UiLanguage.T(". Vérifiez les onglets puis cliquez sur Enregistrer pour l’appliquer, ou Annuler pour abandonner.", ". Check the tabs, then click Save to apply it, or Cancel to discard it.");
            }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, UiLanguage.T("Import du pack", "Pack import"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private bool OfferBackup(string question, string title)
        {
            var choice = MessageBox.Show(this,
                question + UiLanguage.T("\n\nOui : enregistrer un fichier de secours.\nNon : continuer sans sauvegarder.\nAnnuler : ne rien modifier.", "\n\nYes: save a backup file.\nNo: continue without backing up.\nCancel: change nothing."),
                title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return false;
            return choice != MessageBoxResult.Yes || ExportCurrentPack(true, true,
                UiLanguage.T("Enregistrer mes réglages actuels", "Save my current settings"), UiLanguage.T("Mes réglages avant modification.bimaestro-style.json", "My settings before changes.bimaestro-style.json"), _propertiesEditor != null, _homeEditor != null);
        }

        private bool ExportCurrentPack(bool includesRibbon, bool includesBrowser, string title, string fileName,
            bool includesProperties = false, bool includesHome = false)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title,
                Filter = "Pack BIMaestro|*.bimaestro-style.json", FileName = fileName, AddExtension = true };
            if (dialog.ShowDialog(this) != true) return false;
            AppearancePackFile.Export(dialog.FileName, new AppearancePack {
                IncludesRibbon = includesRibbon, IncludesBrowser = includesBrowser,
                IncludesProperties = includesProperties, IncludesHome = includesHome,
                Properties = includesProperties ? _propertiesEditor.CapturePreferences() : null,
                Home = includesHome ? _homeEditor.CaptureSettings() : null,
                RibbonEnabled = AreColoredPanelsEnabled, FullPanels = UseFullPanelColoring,
                Ribbon = includesRibbon ? PanelColors.ToDictionary(item => item.PanelName, item => item.CreateScheme()) : null,
                Browser = includesBrowser ? BrowserPreferences : null,
                Icons = includesBrowser ? BrowserIcons : null
            });
            PackStatus = UiLanguage.T("Pack exporté : ", "Pack exported:") + System.IO.Path.GetFileName(dialog.FileName) + ".";
            return true;
        }

        private void ResetProperties_Click(object sender, RoutedEventArgs e)
        {
            SelectAppearanceSection("properties");
            _propertiesEditor?.ShowResetConfirmation();
        }

        private void ApplyBrowserToCurrentProject_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!CanApplyBrowserToProject)
            {
                PackStatus = UiLanguage.T("Aucun projet Revit modifiable n’est actif.", "No editable Revit project is active.");
                return;
            }

            try
            {
                BrowserPreferences.IsEnabled = true;
                PackStatus = UiLanguage.T("Application au projet en cours…", "Applying to the project…");
                QueueProjectWrite(false, BrowserPreferences, BrowserIcons, error =>
                {
                    if (error != null)
                    {
                        PackStatus = UiLanguage.T("Le projet n’a pas été modifié : ", "The project was not modified:") + error.Message;
                        MessageBox.Show(this, error.Message, UiLanguage.T("Configuration du projet", "Project configuration"), MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    ProjectBrowserColoring.Reset();
                    ProjectBrowserColoring.Apply(_mainWindowHandle);
                    NotifyBrowserSourceChanged(true);
                    PackStatus = UiLanguage.T("Arborescence enregistrée dans le projet. Elle sera partagée lors du prochain enregistrement ou de la prochaine synchronisation.", "Project Browser saved in the project. It will be shared at the next save or synchronization.");
                });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    this,
                    UiLanguage.T("Impossible d’enregistrer l’arborescence dans le projet.\n\n", "Unable to save the Project Browser in the project.\n\n") + ex.Message,
                    UiLanguage.T("Configuration du projet", "Project configuration"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        private ObservableCollection<BrowserIconAsset> _browserIconAssets;
        public ObservableCollection<BrowserIconAsset> BrowserIconAssets => _browserIconAssets ??
            (_browserIconAssets = new ObservableCollection<BrowserIconAsset>(ProjectBrowserIcons.Assets(BrowserIcons)));

        private void AddBrowserIconRule_Click(object sender, RoutedEventArgs e)
        {
            BrowserIcons.Rules.Add(new BrowserIconRule());
        }

        private void RemoveBrowserIconRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is BrowserIconRule rule)
                BrowserIcons.Rules.Remove(rule);
        }

        private void ImportBrowserIcon_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = UiLanguage.T("Importer une icône simple (PNG conseillé)", "Import a simple icon (PNG recommended)"),
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (new System.IO.FileInfo(dialog.FileName).Length > 10 * 1024 * 1024)
                    throw new System.InvalidOperationException(UiLanguage.T("Choisissez une image de moins de 10 Mo.", "Choose an image smaller than 10 MB."));
                using (var stream = System.IO.File.OpenRead(dialog.FileName))
                {
                    var asset = new BrowserIconAsset
                    {
                        Id = System.Guid.NewGuid().ToString("N"),
                        Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
                        Data = ProjectBrowserIcons.EncodeImage(stream)
                    };
                    // Initialize the catalog before adding the new custom asset.
                    var catalog = BrowserIconAssets;
                    BrowserIcons.CustomAssets.Add(asset);
                    catalog.Add(asset);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, UiLanguage.T("Impossible de lire cette image.\n", "Unable to read this image.\n") + ex.Message,
                    UiLanguage.T("Icônes", "Icons"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public ObservableCollection<BrowserCategorySuggestion>
            BrowserCategorySuggestions { get; } =
                new ObservableCollection<BrowserCategorySuggestion>();

        public ObservableCollection<ProjectBrowserColorProfile>
            BrowserColorProfiles { get; } =
                new ObservableCollection<ProjectBrowserColorProfile>();

        public ProjectBrowserColorProfile SelectedBrowserColorProfile
        {
            get => _selectedBrowserColorProfile;
            set
            {
                _selectedBrowserColorProfile = value;
                OnPropertyChanged();
            }
        }

        public string NewBrowserColorProfileName
        {
            get => _newBrowserColorProfileName;
            set
            {
                _newBrowserColorProfileName = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string BrowserCategorySuggestionTitle =>
            BrowserCategorySuggestions.Count == 0
                ? UiLanguage.T("Aucune nouvelle catégorie détectée", "No New Category Detected")
                : BrowserCategorySuggestions.Count + UiLanguage.T(" catégories détectées", " categories detected");

        public string PreferenceFilePath { get; }

        public bool AreColoredPanelsEnabled
        {
            get => _areColoredPanelsEnabled;
            set
            {
                if (_areColoredPanelsEnabled == value) return;
                _areColoredPanelsEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool UseFullPanelColoring
        {
            get => _useFullPanelColoring;
            set
            {
                if (_useFullPanelColoring == value) return;
                _useFullPanelColoring = value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<PresetMenuEntry> PresetEntries { get; }

        public IReadOnlyList<LocalizedOption> BrowserBackgroundModes { get; } =
            new[]
            {
                new LocalizedOption("Uni", "Solid"),
                new LocalizedOption("Verre dépoli", "Frosted Glass"),
                new LocalizedOption("Plan d'architecte", "Blueprint"),
                new LocalizedOption("Encre dans l'eau", "Ink in Water"),
                new LocalizedOption("Bulles pastel", "Pastel Bubbles"),
                new LocalizedOption("Vagues pastel", "Pastel Waves"),
                new LocalizedOption("Rubans fluides", "Flowing Ribbons"),
                new LocalizedOption("Courbes topographiques", "Topographic Contours"),
                new LocalizedOption("Grille d'architecte", "Architect Grid"),
                new LocalizedOption("Aurore boréale", "Northern Lights"),
                new LocalizedOption("Constellation douce", "Soft Constellation"),
                new LocalizedOption("Lucioles pastel", "Pastel Fireflies"),
                new LocalizedOption("Dégradé pastel animé", "Animated Pastel Gradient")
            };

        public IReadOnlyList<LocalizedOption> BrowserColoringModes { get; } =
            new[]
            {
                new LocalizedOption("Aucune coloration", "No Coloring"),
                new LocalizedOption("Par type de vue", "By View Type"),
                new LocalizedOption("Par catégories personnelles", "By Custom Categories"),
                new LocalizedOption("Combiner les deux", "Combine Both")
            };

        public IReadOnlyList<LocalizedOption> BrowserViewColorTargets { get; } =
            new[]
            {
                new LocalizedOption("Fond", "Background"),
                new LocalizedOption("Texte", "Text")
            };

        public string BrowserColoringMode
        {
            get
            {
                bool types =
                    BrowserPreferences?.IsViewTypeColoringEnabled == true;
                bool categories =
                    BrowserPreferences?.IsCategoryColoringEnabled == true;
                if (types && categories) return "Combiner les deux";
                if (types) return "Par type de vue";
                if (categories) return "Par catégories personnelles";
                return "Aucune coloration";
            }
            set
            {
                if (BrowserPreferences == null) return;
                _isUpdatingBrowserColoringMode = true;
                try
                {
                    BrowserPreferences.IsViewTypeColoringEnabled =
                        value == "Par type de vue" ||
                        value == "Combiner les deux";
                    BrowserPreferences.IsCategoryColoringEnabled =
                        value == "Par catégories personnelles" ||
                        value == "Combiner les deux";
                }
                finally
                {
                    _isUpdatingBrowserColoringMode = false;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
            }
        }

        public Visibility BrowserTypeColoringVisibility =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserCategoryColoringVisibility =>
            BrowserPreferences?.IsCategoryColoringEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public ProjectBrowserColorSettings BrowserPreferences
        {
            get => _browserPreferences;
            private set
            {
                if (_browserPreferences != null)
                {
                    _browserPreferences.PropertyChanged -=
                        BrowserPreferences_PropertyChanged;
                }

                _browserPreferences = value;
                if (_browserPreferences != null)
                {
                    _browserPreferences.PropertyChanged +=
                        BrowserPreferences_PropertyChanged;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserColoringMode));
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
                NotifyBrowserPreviewChanged();
            }
        }

        public Visibility BrowserAtmosphereVisibility => BrowserAtmosphere.IsSupported(BrowserPreferences?.BackgroundMode)
            ? Visibility.Visible : Visibility.Collapsed;
        public Brush BrowserAtmospherePreview => BrowserAtmosphere.Preview(BrowserPreferences);

        public Brush BrowserPreviewBackgroundBrush =>
            new SolidColorBrush(
                BrowserPreferences?.BackgroundColor ?? Colors.White);

        public Brush BrowserPreviewTextBrush =>
            new SolidColorBrush(
                BrowserPreferences?.TextColor ?? Colors.Black);

        public Brush BrowserPreviewAccentBrush =>
            new SolidColorBrush(
                BrowserPreferences?.AccentColor ?? Colors.DodgerBlue);

        public Brush BrowserPreviewActiveParentBrush =>
            new SolidColorBrush(
                BrowserPreferences?.ActiveViewParentColor ?? Colors.Red);

        public Brush BrowserPreviewPlanBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.PlanViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewSectionBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.SectionViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewThreeDBrush =>
            CreateViewTypePreviewBrush(
                BrowserPreferences?.ThreeDViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewPlanParentBrush =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true &&
            BrowserPreferences?.IsViewTypeParentColoringEnabled == true &&
            BrowserPreferences?.ViewColorTarget == "Fond"
                ? new SolidColorBrush(BrowserPreferences.PlanViewColor)
                : Brushes.Transparent;

        public Brush BrowserPreviewPlanTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.PlanViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewPlanParentTextBrush =>
            BrowserPreferences?.IsViewTypeColoringEnabled == true &&
            BrowserPreferences?.IsViewTypeParentColoringEnabled == true &&
            BrowserPreferences?.ViewColorTarget == "Texte"
                ? new SolidColorBrush(BrowserPreferences.PlanViewColor)
                : BrowserPreviewTextBrush;

        public Brush BrowserPreviewSectionTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.SectionViewColor ?? Colors.Transparent);

        public Brush BrowserPreviewThreeDTextBrush =>
            CreateViewTypePreviewTextBrush(
                BrowserPreferences?.ThreeDViewColor ?? Colors.Transparent);

        public Visibility BrowserBubblesVisibility =>
            BrowserModeVisibility("Bulles pastel");

        public Visibility BrowserWavesVisibility =>
            BrowserModeVisibility("Vagues pastel");

        public Visibility BrowserRibbonsVisibility =>
            BrowserModeVisibility("Rubans fluides");

        public Visibility BrowserTopographyVisibility =>
            BrowserModeVisibility("Courbes topographiques");

        public Visibility BrowserArchitectGridVisibility =>
            BrowserModeVisibility("Grille d'architecte");

        public Visibility BrowserNorthernLightsVisibility =>
            BrowserModeVisibility("Aurore boréale");

        public Visibility BrowserConstellationVisibility =>
            BrowserModeVisibility("Constellation douce");

        public Visibility BrowserFirefliesVisibility =>
            BrowserModeVisibility("Lucioles pastel");

        public Visibility BrowserAuroraVisibility =>
            BrowserModeVisibility("Dégradé pastel animé");

        public string BrowserPreviewPrimaryViewName
        {
            get => _browserPreviewPrimaryViewName;
            private set
            {
                _browserPreviewPrimaryViewName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BrowserPreviewSearchLabel));
            }
        }

        public string BrowserPreviewSecondaryViewName
        {
            get => _browserPreviewSecondaryViewName;
            private set
            {
                _browserPreviewSecondaryViewName = value;
                OnPropertyChanged();
            }
        }

        public string BrowserPreviewSectionName
        {
            get => _browserPreviewSectionName;
            private set
            {
                _browserPreviewSectionName = value;
                OnPropertyChanged();
            }
        }

        public string BrowserPreviewSearchLabel =>
            $"Rechercher : {BrowserPreviewPrimaryViewName}";

        public Visibility BrowserSearchVisibility =>
            BrowserPreferences?.IsSheetViewSearchEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserActiveParentVisibility =>
            BrowserPreferences?.IsActiveViewParentHighlightEnabled == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility BrowserDisabledVisibility =>
            BrowserPreferences?.IsEnabled == true
                ? Visibility.Collapsed
                : Visibility.Visible;

        public string SelectedPresetName
        {
            get => _selectedPresetName;
            set => _selectedPresetName = value;
        }

        private void ApplyPresetButton_Click(object sender, RoutedEventArgs e)
        {
            Dictionary<string, RibbonPanelColorScheme> preset =
                RibbonColorPresetCatalog.Create(SelectedPresetName);

            foreach (PanelColorItem item in PanelColors)
            {
                if (preset.TryGetValue(item.PanelName, out RibbonPanelColorScheme scheme))
                    item.ApplyScheme(scheme);
            }
        }

        private void BrowserPreferences_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectBrowserColorSettings.BackgroundMode) && BrowserAtmosphere.IsSupported(BrowserPreferences.BackgroundMode))
            {
                bool blueprint = BrowserPreferences.BackgroundMode == "Plan d'architecte";
                BrowserPreferences.BackgroundColor = blueprint ? Color.FromRgb(17, 37, 63) : Color.FromRgb(246, 248, 252);
                BrowserPreferences.TextColor = blueprint ? Color.FromRgb(226, 239, 252) : Color.FromRgb(36, 49, 66);
            }
            if (!_isUpdatingBrowserColoringMode)
            {
                OnPropertyChanged(nameof(BrowserColoringMode));
                OnPropertyChanged(nameof(BrowserTypeColoringVisibility));
                OnPropertyChanged(nameof(BrowserCategoryColoringVisibility));
            }
            NotifyBrowserPreviewChanged();
        }

        private void RegenerateBrowserPreviewButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            GenerateBrowserPreviewNames();
        }

        private void AddBrowserCategoryRuleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Color[] colors =
            {
                Color.FromRgb(209, 250, 229),
                Color.FromRgb(219, 234, 254),
                Color.FromRgb(252, 221, 235),
                Color.FromRgb(254, 229, 195),
                Color.FromRgb(237, 233, 254)
            };
            int index = BrowserPreferences.CategoryColorRules.Count;
            BrowserPreferences.CategoryColorRules.Add(
                new ProjectBrowserCategoryColorRule
                {
                    CategoryName = string.Empty,
                    Color = colors[index % colors.Length]
                });
            BrowserPreferences.IsCategoryColoringEnabled = true;
        }

        private void ChooseBrowserFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_document == null)
            {
                MessageBox.Show(this,
                    UiLanguage.T("Ouvrez d’abord un projet Revit pour choisir un dossier.", "First open a Revit project to choose a folder."),
                    UiLanguage.T("Arborescence du projet", "Project Browser"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            QueueBrowserRead(_ =>
            {
                if (!int.TryParse(_document.Application.VersionNumber, out int version) || version < 2024)
                    throw new System.InvalidOperationException(UiLanguage.T("La sélection des dossiers nécessite Revit 2024 ou plus récent.", "Folder selection requires Revit 2024 or later."));
                var dialog = new ProjectBrowserRuleWindow(
                    _document, BrowserPreferences,
                    (selected, previous, completed) => ApplyBrowserRule(selected, previous, completed),
                    (selected, completed) => ApplyBrowserRule(null, selected, completed),
                    UndoBrowserRule, _browserRuleUndo.Count > 0)
                    { Owner = this };
                if (_appearanceGuideStep == 7)
                {
                    dialog.StartTextColorTutorial();
                    AppearanceGuideCard.Visibility = Visibility.Collapsed;
                    dialog.Closed += (_, __) =>
                    {
                        if (_appearanceGuideStep != 7) return;
                        AppearanceGuideCard.Visibility = Visibility.Visible;
                        if (dialog.TextColorTutorialCompleted)
                        {
                            CompleteAppearanceGuideStep(7);
                            AppearanceGuideText.Text = UiLanguage.T("La couleur du texte a été appliquée au dossier choisi. Clique sur Suivant pour ajouter les icônes. Tu peux annuler cet essai avec « Annuler la dernière application » dans le choix des dossiers.", "The text color has been applied to the selected folder. Click Next to add icons. You can undo this test with “Undo last application” in the folder selection.");
                        }
                    };
                }
                dialog.Show();
            }, error =>
            {
                if (error != null)
                    MessageBox.Show(this, error.Message, UiLanguage.T("Arborescence du projet", "Project Browser"), MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void ApplyBrowserRule(
            ProjectBrowserCategoryColorRule selected,
            ProjectBrowserCategoryColorRule previous,
            System.Action<System.Exception> completed)
        {
            if (HasSharedBrowserAppearance && !CanApplyBrowserToProject)
            {
                completed(new System.InvalidOperationException(
                    UiLanguage.T("Le style de cette maquette est partagé, mais le projet est en lecture seule. Aucune modification personnelle ne peut le remplacer ici.", "This model's style is shared, but the project is read-only. No personal change can replace it here.")));
                return;
            }
            var undoState = new BrowserRuleUndoState
            {
                Settings = ProjectBrowserColorPreferences.Clone(BrowserPreferences),
                WasShared = HasSharedBrowserAppearance
            };
            var next = ProjectBrowserColorPreferences.Clone(BrowserPreferences);
            if (previous != null)
            {
                var old = next.CategoryColorRules.FirstOrDefault(rule =>
                    string.Equals(rule.FolderPath, previous.FolderPath,
                        System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rule.Effect, previous.Effect,
                        System.StringComparison.OrdinalIgnoreCase));
                if (old != null) next.CategoryColorRules.Remove(old);
            }
            if (selected != null)
            {
                var existing = next.CategoryColorRules.FirstOrDefault(rule =>
                    !string.IsNullOrWhiteSpace(rule.FolderPath) &&
                    string.Equals(rule.FolderPath, selected.FolderPath,
                        System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rule.Effect, selected.Effect,
                        System.StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    next.CategoryColorRules.Add(selected.Clone());
                else
                {
                    existing.CategoryName = selected.CategoryName;
                    existing.Color = selected.Color;
                    existing.Effect = selected.Effect;
                    existing.Scope = selected.Scope;
                }
            }
            next.IsCategoryColoringEnabled = next.CategoryColorRules.Count > 0;
            next.IsEnabled = true;
            System.Action<System.Exception> finish = error =>
            {
                if (error != null) { completed(error); return; }
                try
                {
                    if (!CanApplyBrowserToProject)
                        ProjectBrowserColorPreferences.Save(next);
                    BrowserPreferences = next;
                    NotifyBrowserSourceChanged(CanApplyBrowserToProject || HasSharedBrowserAppearance);
                    ProjectBrowserColoring.Reset();
                    ProjectBrowserColoring.Apply(_mainWindowHandle);
                    _browserRuleUndo.Push(undoState);
                    completed(null);
                }
                catch (System.Exception ex) { completed(ex); }
            };
            if (CanApplyBrowserToProject)
                QueueProjectWrite(false, next, BrowserIcons, finish);
            else
                finish(null);
        }

        private void UndoBrowserRule(
            System.Action<ProjectBrowserColorSettings, bool, System.Exception> completed)
        {
            if (_browserRuleUndo.Count == 0)
            {
                completed(null, false, new System.InvalidOperationException(UiLanguage.T("Aucune application récente à annuler.", "No recent application to undo.")));
                return;
            }
            BrowserRuleUndoState previous = _browserRuleUndo.Peek();
            System.Action<System.Exception> finish = error =>
            {
                if (error != null) { completed(null, true, error); return; }
                try
                {
                    if (!CanApplyBrowserToProject)
                        ProjectBrowserColorPreferences.Save(previous.Settings);
                    BrowserPreferences = ProjectBrowserColorPreferences.Clone(previous.Settings);
                    NotifyBrowserSourceChanged(previous.WasShared);
                    ProjectBrowserColoring.Reset();
                    ProjectBrowserColoring.Apply(_mainWindowHandle);
                    _browserRuleUndo.Pop();
                    completed(ProjectBrowserColorPreferences.Clone(BrowserPreferences),
                        _browserRuleUndo.Count > 0, null);
                }
                catch (System.Exception ex) { completed(null, true, ex); }
            };
            if (CanApplyBrowserToProject)
                QueueProjectWrite(!previous.WasShared, previous.Settings, BrowserIcons, finish);
            else
                finish(null);
        }

        private void EditBrowserCategoryRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) ||
                !(element.DataContext is ProjectBrowserCategoryColorRule rule) ||
                _document == null)
                return;
            QueueBrowserRead(_ =>
            {
            var dialog = new ProjectBrowserRuleWindow(
                _document, BrowserPreferences,
                (selected, previous, completed) => ApplyBrowserRule(selected, previous, completed),
                (selected, completed) => ApplyBrowserRule(null, selected, completed),
                UndoBrowserRule, _browserRuleUndo.Count > 0, rule)
                    { Owner = this };
                dialog.Show();
            }, error =>
            {
                if (error != null)
                    MessageBox.Show(this, error.Message, UiLanguage.T("Modifier un dossier", "Edit a folder"), MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void RemoveBrowserCategoryRuleButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is FrameworkElement element &&
                element.DataContext is ProjectBrowserCategoryColorRule rule)
            {
                BrowserPreferences.CategoryColorRules.Remove(rule);
            }
        }

        private void RefreshBrowserCategoriesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            QueueCategoryRefresh();
        }

        private void QueueCategoryRefresh()
        {
            if (_document == null) return;
            QueueBrowserRead(_ => DetectBrowserCategories(), error =>
            {
                if (error != null)
                    MessageBox.Show(this, error.Message, UiLanguage.T("Actualiser les dossiers", "Refresh folders"), MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void AddDetectedBrowserCategoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) ||
                !(element.DataContext is BrowserCategorySuggestion suggestion))
            {
                return;
            }

            bool alreadyExists = BrowserPreferences.CategoryColorRules.Any(
                rule => string.Equals(
                    rule.CategoryName?.Trim(),
                    suggestion.Name,
                    System.StringComparison.OrdinalIgnoreCase));
            if (!alreadyExists)
            {
                BrowserPreferences.CategoryColorRules.Add(
                    new ProjectBrowserCategoryColorRule
                    {
                        CategoryName = suggestion.Name,
                        Color = suggestion.SuggestedColor
                    });
            }

            BrowserPreferences.IsCategoryColoringEnabled = true;
            BrowserCategorySuggestions.Remove(suggestion);
            OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
        }

        private void SaveBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                ProjectBrowserColorProfilePreferences.Save(
                    NewBrowserColorProfileName,
                    BrowserPreferences);
                string savedName = NewBrowserColorProfileName.Trim();
                RefreshBrowserProfiles(savedName);
                NewBrowserColorProfileName = string.Empty;
            }
            catch (System.Exception ex)
            {
                ShowSaveError(ex);
            }
        }

        private void ApplyBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SelectedBrowserColorProfile?.Settings == null) return;
            BrowserPreferences = ProjectBrowserColorPreferences.Clone(
                SelectedBrowserColorProfile.Settings);
            QueueCategoryRefresh();
        }

        private void DeleteBrowserProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SelectedBrowserColorProfile == null) return;
            ProjectBrowserColorProfilePreferences.Delete(
                SelectedBrowserColorProfile.Name);
            RefreshBrowserProfiles();
        }

        private void RefreshBrowserProfiles(string selectedName = null)
        {
            BrowserColorProfiles.Clear();
            foreach (ProjectBrowserColorProfile profile in
                     ProjectBrowserColorProfilePreferences.Load())
            {
                BrowserColorProfiles.Add(profile);
            }
            SelectedBrowserColorProfile = BrowserColorProfiles
                .FirstOrDefault(profile => string.Equals(
                    profile.Name,
                    selectedName,
                    System.StringComparison.OrdinalIgnoreCase)) ??
                BrowserColorProfiles.FirstOrDefault();
        }

        private void DetectBrowserCategories()
        {
            BrowserCategorySuggestions.Clear();
            if (_document == null)
            {
                OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
                return;
            }

            var counts = new Dictionary<string, int>(
                System.StringComparer.OrdinalIgnoreCase);
            try
            {
                BrowserOrganization organization =
                    BrowserOrganization
                        .GetCurrentBrowserOrganizationForViews(_document);
                foreach (Autodesk.Revit.DB.View view in
                         new FilteredElementCollector(_document)
                             .OfClass(typeof(Autodesk.Revit.DB.View))
                             .Cast<Autodesk.Revit.DB.View>())
                {
                    if (view == null ||
                        view.IsTemplate ||
                        view is ViewSheet ||
                        organization == null ||
                        !organization.AreFiltersSatisfied(view.Id))
                    {
                        continue;
                    }

                    IList<FolderItemInfo> folderItems = null;
                    try
                    {
                        folderItems = organization
                            .GetFolderItems(view.Id)
                            ?.Where(item => item != null)
                            .ToList();
                        if (folderItems == null) continue;

                        foreach (string name in folderItems
                                     .Select(item => item.Name?.Trim())
                                     .Where(name =>
                                         !string.IsNullOrWhiteSpace(name))
                                     .Distinct(
                                         System.StringComparer
                                             .OrdinalIgnoreCase))
                        {
                            counts[name] = counts.TryGetValue(
                                name,
                                out int count)
                                ? count + 1
                                : 1;
                        }
                    }
                    finally
                    {
                        if (folderItems != null)
                        {
                            foreach (FolderItemInfo item in folderItems)
                            {
                                try { item.Dispose(); }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Une organisation en cours de modification sera relue avec
                // le bouton Actualiser lorsque Revit sera de nouveau disponible.
            }

            var existingNames = new HashSet<string>(
                BrowserPreferences.CategoryColorRules
                    .Select(rule => rule.CategoryName?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                System.StringComparer.OrdinalIgnoreCase);
            Color[] palette =
            {
                Color.FromRgb(209, 250, 229),
                Color.FromRgb(219, 234, 254),
                Color.FromRgb(252, 221, 235),
                Color.FromRgb(254, 229, 195),
                Color.FromRgb(237, 233, 254),
                Color.FromRgb(254, 240, 138)
            };
            int colorIndex = 0;
            foreach (KeyValuePair<string, int> item in counts
                         .Where(item => !existingNames.Contains(item.Key))
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key))
            {
                BrowserCategorySuggestions.Add(
                    new BrowserCategorySuggestion(
                        item.Key,
                        item.Value,
                        palette[colorIndex++ % palette.Length]));
            }

            OnPropertyChanged(nameof(BrowserCategorySuggestionTitle));
        }

        private void GenerateBrowserPreviewNames()
        {
            string[] disciplines =
            {
                "Architecture", "Structure", UiLanguage.T("Aménagement", "Interiors"), "Coordination"
            };
            string[] levels =
            {
                UiLanguage.T("RDC", "Ground Floor"), UiLanguage.T("Niveau 01", "Level 01"), UiLanguage.T("Niveau 02", "Level 02"), UiLanguage.T("Toiture", "Roof")
            };
            string[] sections =
            {
                UiLanguage.T("Coupe AA · Hall central", "Section AA · Main Hall"),
                UiLanguage.T("Coupe BB · Escalier principal", "Section BB · Main Stair"),
                UiLanguage.T("Façade · Nord", "Elevation · North"),
                UiLanguage.T("Détail · Entrée principale", "Detail · Main Entrance")
            };

            string level = levels[_previewRandom.Next(levels.Length)];
            string secondLevel;
            do
            {
                secondLevel = levels[_previewRandom.Next(levels.Length)];
            }
            while (secondLevel == level);

            BrowserPreviewPrimaryViewName =
                $"Plan {disciplines[_previewRandom.Next(disciplines.Length)]} · {level}";
            BrowserPreviewSecondaryViewName =
                $"Plan {disciplines[_previewRandom.Next(disciplines.Length)]} · {secondLevel}";
            BrowserPreviewSectionName =
                sections[_previewRandom.Next(sections.Length)];
        }

        private Visibility BrowserModeVisibility(string mode)
        {
            return string.Equals(
                       BrowserPreferences?.BackgroundMode,
                       mode,
                       System.StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void NotifyBrowserPreviewChanged()
        {
            OnPropertyChanged(nameof(BrowserAtmosphereVisibility));
            OnPropertyChanged(nameof(BrowserAtmospherePreview));
            OnPropertyChanged(nameof(BrowserPreviewBackgroundBrush));
            OnPropertyChanged(nameof(BrowserPreviewTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewAccentBrush));
            OnPropertyChanged(nameof(BrowserPreviewActiveParentBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanBrush));
            OnPropertyChanged(nameof(BrowserPreviewSectionBrush));
            OnPropertyChanged(nameof(BrowserPreviewThreeDBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanParentBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewPlanParentTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewSectionTextBrush));
            OnPropertyChanged(nameof(BrowserPreviewThreeDTextBrush));
            OnPropertyChanged(nameof(BrowserBubblesVisibility));
            OnPropertyChanged(nameof(BrowserWavesVisibility));
            OnPropertyChanged(nameof(BrowserRibbonsVisibility));
            OnPropertyChanged(nameof(BrowserTopographyVisibility));
            OnPropertyChanged(nameof(BrowserArchitectGridVisibility));
            OnPropertyChanged(nameof(BrowserNorthernLightsVisibility));
            OnPropertyChanged(nameof(BrowserConstellationVisibility));
            OnPropertyChanged(nameof(BrowserFirefliesVisibility));
            OnPropertyChanged(nameof(BrowserAuroraVisibility));
            OnPropertyChanged(nameof(BrowserSearchVisibility));
            OnPropertyChanged(nameof(BrowserActiveParentVisibility));
            OnPropertyChanged(nameof(BrowserDisabledVisibility));
        }

        private Brush CreateViewTypePreviewBrush(Color color)
        {
            return BrowserPreferences?.IsViewTypeColoringEnabled == true &&
                   BrowserPreferences?.ViewColorTarget == "Fond"
                ? new SolidColorBrush(color)
                : Brushes.Transparent;
        }

        private Brush CreateViewTypePreviewTextBrush(Color color)
        {
            return BrowserPreferences?.IsViewTypeColoringEnabled == true &&
                   BrowserPreferences?.ViewColorTarget == "Texte"
                ? new SolidColorBrush(color)
                : BrowserPreviewTextBrush;
        }

        private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!OfferBackup(UiLanguage.T("Avant de revenir aux valeurs par défaut, voulez-vous enregistrer vos réglages actuels ?", "Before reverting to the default values, do you want to save your current settings?"), UiLanguage.T("Valeurs par défaut", "Default values")))
                    return;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, UiLanguage.T("Sauvegarde des réglages", "Settings backup"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            BrowserIcons.Enabled = false;
            BrowserIcons.Rules.Clear();
            foreach (var rule in ProjectBrowserIcons.Defaults().Rules)
                BrowserIcons.Rules.Add(rule);
            Dictionary<string, RibbonPanelColorScheme> defaults =
                RibbonColorPreferences.GetDefaults();

            foreach (PanelColorItem item in PanelColors)
            {
                if (defaults.TryGetValue(item.PanelName, out RibbonPanelColorScheme scheme))
                    item.ApplyScheme(scheme);
            }

            BrowserPreferences =
                ProjectBrowserColorPreferences.GetDefaults();
            AreColoredPanelsEnabled = true;
            UseFullPanelColoring = false;
            PackStatus = HasSharedBrowserAppearance
                ? UiLanguage.T("Valeurs par défaut préparées. Si vous enregistrez, elles remplaceront le style partagé de ce projet ; vos préférences personnelles resteront intactes.", "Default values prepared. If you save, they will replace this project's shared style; your personal preferences will remain intact.")
                : UiLanguage.T("Valeurs personnelles remises à zéro dans cette fenêtre. Cliquez sur « Enregistrer mes réglages » pour les appliquer.", "Personal values reset in this window. Click “Save my settings” to apply them.");
        }

        private void ResetBrowserDefaultsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (!OfferBackup(UiLanguage.T("Avant de restaurer l’arborescence Revit, voulez-vous enregistrer vos réglages actuels ?", "Before restoring the Revit Project Browser, do you want to save your current settings?"), UiLanguage.T("Restaurer Revit", "Restore Revit")))
                    return;
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, UiLanguage.T("Sauvegarde des réglages", "Settings backup"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (HasSharedBrowserAppearance && MessageBox.Show(this,
                UiLanguage.T("Cette maquette contient un style d’arborescence partagé. Le retirer modifiera la maquette pour toute l’équipe après enregistrement ou synchronisation. Continuer ?", "This model contains a shared Project Browser style. Removing it will modify the model for the whole team after saving or synchronizing. Continue?"),
                UiLanguage.T("Retirer le style partagé", "Remove shared style"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            if (HasSharedBrowserAppearance)
            {
                QueueProjectWrite(true, null, null, error =>
                {
                    if (error != null)
                    {
                        MessageBox.Show(this, error.Message, UiLanguage.T("Restaurer l’arborescence Revit", "Restore Revit Project Browser"), MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    BrowserPreferences = ProjectBrowserColorPreferences.Load();
                    BrowserIcons = ProjectBrowserIcons.Load();
                    _browserIconAssets = null;
                    OnPropertyChanged(nameof(BrowserIcons));
                    OnPropertyChanged(nameof(BrowserIconAssets));
                    NotifyBrowserSourceChanged(false);
                    ProjectBrowserColoring.Reset();
                    ProjectBrowserColoring.Apply(_mainWindowHandle);
                    PackStatus = UiLanguage.T("Style partagé retiré. Ce projet utilise à nouveau vos réglages personnels, sans les modifier.", "Shared style removed. This project uses your personal settings again, without changing them.");
                });
                return;
            }
            RestoreBrowserLocally();
        }

        private void RestoreBrowserLocally()
        {
            ProjectBrowserColorSettings reset =
                ProjectBrowserColorPreferences.GetDefaults();
            reset.IsEnabled = false;
            reset.IsActiveViewParentHighlightEnabled = false;
            reset.BackgroundMode = "Uni";
            BrowserPreferences = reset;
            BrowserIcons.Enabled = false;
            ProjectBrowserIcons.Save(BrowserIcons);
            ProjectBrowserColorPreferences.Save(reset);
            ProjectBrowserColoring.Reset();
            ProjectBrowserColoring.Apply(_mainWindowHandle);
            NotifyBrowserSourceChanged(false);
            PackStatus = UiLanguage.T("Arborescence Revit restaurée. Le style partagé a été retiré du projet s’il était présent.", "Revit Project Browser restored. The shared style was removed from the project if it was present.");
        }

        private void OpenRevitColorsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            SaveSettingsAndContinue(() =>
            {
                RevitColorPreferencesWindow.ShowModeless(_mainWindowHandle);
                Close();
            });
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            bool guided = _appearanceGuideStep == AppearanceGuideStepCount - 1;
            SaveSettingsAndContinue(() =>
            {
                if (guided)
                {
                    BIMaestro.Tutorials.TutorialAnalytics.Complete("colors");
                    StopAppearanceTutorial();
                }
                Close();
                if (guided)
                {
                    BIMaestro.Tutorials.DemoTourCompletion.Show(_mainWindowHandle, "colors",
                        UiLanguage.T("Tes réglages d’apparence sont enregistrés. Tu as terminé le tutoriel Couleurs et vues, avec les onglets Propriétés, Accueil Revit et partage des réglages.", "Your appearance settings are saved. You have completed the Colors and Views tutorial, including the Properties, Revit Home and settings sharing tabs."));
                }
            });
        }

        private void SaveSettingsAndContinue(System.Action next)
        {
            if (_savingAppearance || _projectWriteHandler.HasPending) return;
            SetSavingAppearance(true);
            if (HasSharedBrowserAppearance)
            {
                if (!CanApplyBrowserToProject)
                {
                    SetSavingAppearance(false);
                    MessageBox.Show(this,
                        UiLanguage.T("Cette maquette partage son apparence, mais elle est en lecture seule. Les changements ne peuvent pas être conservés dans ce projet.", "This model shares its appearance, but it is read-only. Changes cannot be kept in this project."),
                        UiLanguage.T("Enregistrer mes réglages", "Save my settings"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                QueueProjectWrite(false, BrowserPreferences, BrowserIcons, error =>
                {
                    if (error != null)
                    {
                        AppearanceActionFailed(error);
                        return;
                    }
                    try
                    {
                        NotifyBrowserSourceChanged(true);
                        SaveCurrentColors(false);
                        CustomizeRibbonColorsCommand.ReapplyColors(_mainWindowHandle);
                        SaveAppearanceEditorsAndContinue(next);
                    }
                    catch (System.Exception ex) { AppearanceActionFailed(ex); }
                });
                return;
            }
            try
            {
                SaveCurrentColors(true);
                CustomizeRibbonColorsCommand.ReapplyColors(_mainWindowHandle);
                SaveAppearanceEditorsAndContinue(next);
            }
            catch (System.Exception ex)
            {
                AppearanceActionFailed(ex);
            }
        }

        private void SaveCurrentColors(bool saveBrowserLocally)
        {
            ColoringStateManager.SetColoringActive(
                AreColoredPanelsEnabled);
            ColoringStateManager.SetFullMode(
                UseFullPanelColoring);
            var colors = PanelColors.ToDictionary(
                item => item.PanelName,
                item => item.CreateScheme());
            RibbonColorPreferences.Save(colors);
            if (saveBrowserLocally)
            {
                ProjectBrowserIcons.Save(BrowserIcons);
                ProjectBrowserColorPreferences.Save(BrowserPreferences);
            }
            ProjectBrowserColoring.Reset();
            ProjectBrowserColoring.Apply(_mainWindowHandle);
        }

        private static void ShowSaveError(System.Exception ex)
        {
            MessageBox.Show(
                UiLanguage.T("Impossible d’enregistrer les couleurs.\n\n", "Unable to Save Colors.\n\n") + ex.Message,
                "BIMaestro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static ObservableCollection<PanelColorItem> CreateItems(
            IReadOnlyDictionary<string, RibbonPanelColorScheme> colors)
        {
            return new ObservableCollection<PanelColorItem>(
                RibbonColorPreferences.PanelNames.Select(panelName =>
                {
                    RibbonPanelColorScheme scheme =
                        colors.TryGetValue(panelName, out RibbonPanelColorScheme savedScheme)
                            ? savedScheme
                            : new RibbonPanelColorScheme(
                                Colors.Transparent,
                                Colors.Transparent,
                                Colors.Black);

                    return new PanelColorItem(panelName, scheme);
                }));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(
            [CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class PresetMenuEntry
    {
        public PresetMenuEntry(string name, bool isHeader)
        {
            Name = name;
            IsHeader = isHeader;
        }

        public string Name { get; }

        public string DisplayName => UiLanguage.T(Name);

        public bool IsHeader { get; }
    }

    public sealed class LocalizedOption
    {
        public LocalizedOption(string value, string english)
        {
            Value = value;
            Label = UiLanguage.T(value, english);
        }

        public string Value { get; }

        public string Label { get; }
    }

    public sealed class PanelColorItem : INotifyPropertyChanged
    {
        private static readonly IReadOnlyList<string> AvailableBackgroundModes =
            new[]
            {
                "Uni", "Horizontal", "Vertical", "Diagonal",
                "France", "France continue", "Noël festif", "Confettis",
                "Pokéball douce", "Pokémon pixel", "Arc-en-ciel animé",
                "Bulles pastel", "Vagues pastel", "Étoiles pastel",
                "Nuages doux"
            };

        private static readonly IReadOnlyList<LocalizedOption> LocalizedBackgroundModes =
            AvailableBackgroundModes
                .Select(value => new LocalizedOption(value, UiLanguage.T(value)))
                .ToList()
                .AsReadOnly();

        private Color? _backgroundColor;
        private Color? _backgroundEndColor;
        private Color? _textColor;
        private string _backgroundMode;
        private double _patternStart;
        private double _patternEnd;

        public PanelColorItem(string panelName, RibbonPanelColorScheme scheme)
        {
            PanelName = panelName;
            ApplyScheme(scheme, false);
        }

        public string PanelName { get; }

        public string DisplayPanelName => UiLanguage.T(PanelName);

        public IReadOnlyList<LocalizedOption> BackgroundModes => LocalizedBackgroundModes;

        public Color? BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (_backgroundColor == value)
                    return;

                _backgroundColor = value;
                OnPropertyChanged();

                if (!IsGradient)
                {
                    _backgroundEndColor = value;
                    OnPropertyChanged(nameof(BackgroundEndColor));
                }

                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public Color? BackgroundEndColor
        {
            get => _backgroundEndColor;
            set
            {
                if (_backgroundEndColor == value)
                    return;

                _backgroundEndColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public Color? TextColor
        {
            get => _textColor;
            set
            {
                if (_textColor == value)
                    return;

                _textColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TextBrush));
            }
        }

        public string BackgroundMode
        {
            get => _backgroundMode;
            set
            {
                string normalized = AvailableBackgroundModes.Contains(value)
                    ? value
                    : "Uni";

                if (_backgroundMode == normalized)
                    return;

                _backgroundMode = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGradient));
                OnPropertyChanged(nameof(CanEditEndColor));
                OnPropertyChanged(nameof(BackgroundBrush));
            }
        }

        public bool IsGradient =>
            string.Equals(BackgroundMode, "Horizontal", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(BackgroundMode, "Vertical", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(BackgroundMode, "Diagonal", System.StringComparison.OrdinalIgnoreCase);

        public bool CanEditEndColor => IsGradient;

        public Brush BackgroundBrush => CreateScheme().CreateBackgroundBrush();

        public Brush TextBrush =>
            new SolidColorBrush(TextColor ?? Colors.Transparent);

        public RibbonPanelColorScheme CreateScheme()
        {
            return new RibbonPanelColorScheme(
                BackgroundColor ?? Colors.Transparent,
                BackgroundEndColor ?? BackgroundColor ?? Colors.Transparent,
                TextColor ?? Colors.Transparent,
                IsGradient,
                GetGradientDirection(BackgroundMode),
                GetBackgroundPattern(BackgroundMode),
                _patternStart,
                _patternEnd);
        }

        public void ApplyScheme(RibbonPanelColorScheme scheme)
        {
            ApplyScheme(scheme, true);
        }

        private void ApplyScheme(RibbonPanelColorScheme scheme, bool notify)
        {
            _backgroundColor = scheme.BackgroundColor;
            _backgroundEndColor = scheme.BackgroundEndColor;
            _textColor = scheme.TextColor;
            _backgroundMode = GetBackgroundMode(scheme);
            _patternStart = scheme.PatternStart;
            _patternEnd = scheme.PatternEnd;

            if (!notify)
                return;

            OnPropertyChanged(nameof(BackgroundColor));
            OnPropertyChanged(nameof(BackgroundEndColor));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(BackgroundMode));
            OnPropertyChanged(nameof(IsGradient));
            OnPropertyChanged(nameof(CanEditEndColor));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(TextBrush));
        }

        private static string GetBackgroundMode(RibbonPanelColorScheme scheme)
        {
            if (scheme.BackgroundPattern == RibbonBackgroundPattern.FrenchFlag)
                return "France";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.FrenchFlagContinuous)
                return "France continue";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.ChristmasFestive)
                return "Noël festif";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.Confetti)
                return "Confettis";

            if (scheme.BackgroundPattern == RibbonBackgroundPattern.PokeBallPixel)
                return "Pokéball douce";

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPokemonPixelContinuous)
            {
                return "Pokémon pixel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedRainbowContinuous)
            {
                return "Arc-en-ciel animé";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelBubblesContinuous)
            {
                return "Bulles pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelWavesContinuous)
            {
                return "Vagues pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedPastelStarsContinuous)
            {
                return "Étoiles pastel";
            }

            if (scheme.BackgroundPattern ==
                RibbonBackgroundPattern.AnimatedSoftCloudsContinuous)
            {
                return "Nuages doux";
            }

            return scheme.IsGradient
                ? scheme.GradientDirection.ToString()
                : "Uni";
        }

        private static RibbonBackgroundPattern GetBackgroundPattern(string mode)
        {
            if (string.Equals(mode, "France", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.FrenchFlag;

            if (string.Equals(mode, "France continue", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.FrenchFlagContinuous;

            if (string.Equals(mode, "Noël festif", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.ChristmasFestive;

            if (string.Equals(mode, "Confettis", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.Confetti;

            if (string.Equals(mode, "Pokéball douce", System.StringComparison.OrdinalIgnoreCase))
                return RibbonBackgroundPattern.PokeBallPixel;

            if (string.Equals(
                    mode,
                    "Pokémon pixel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPokemonPixelContinuous;
            }

            if (string.Equals(
                    mode,
                    "Arc-en-ciel animé",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedRainbowContinuous;
            }

            if (string.Equals(
                    mode,
                    "Bulles pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelBubblesContinuous;
            }

            if (string.Equals(
                    mode,
                    "Vagues pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelWavesContinuous;
            }

            if (string.Equals(
                    mode,
                    "Étoiles pastel",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedPastelStarsContinuous;
            }

            if (string.Equals(
                    mode,
                    "Nuages doux",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return RibbonBackgroundPattern.AnimatedSoftCloudsContinuous;
            }

            return RibbonBackgroundPattern.Standard;
        }

        private static RibbonGradientDirection GetGradientDirection(string mode)
        {
            return System.Enum.TryParse(
                mode,
                true,
                out RibbonGradientDirection direction)
                ? direction
                : RibbonGradientDirection.Horizontal;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class BrowserCategorySuggestion
    {
        public BrowserCategorySuggestion(
            string name,
            int viewCount,
            Color suggestedColor)
        {
            Name = name;
            ViewCount = viewCount;
            SuggestedColor = suggestedColor;
        }

        public string Name { get; }

        public int ViewCount { get; }

        public Color SuggestedColor { get; }

        public string CountLabel =>
            ViewCount == 1
                ? UiLanguage.T("1 vue", "1 view")
                : ViewCount + UiLanguage.T(" vues", " views");
    }
}
