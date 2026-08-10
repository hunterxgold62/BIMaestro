using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BIMaestro.Localization;

namespace Couleur
{
    public partial class RevitColorPreferencesWindow :
        Window,
        INotifyPropertyChanged
    {
        private static RevitColorPreferencesWindow _openWindow;

        private readonly IntPtr _mainWindowHandle;
        private readonly DispatcherTimer _autoSaveTimer;
        private RevitRibbonTabColorItem _selectedTab;
        private bool _isGlobalColoringEnabled;
        private bool _hasPendingChanges;
        private string _autoSaveStatus;

        public static void ShowModeless(IntPtr mainWindowHandle)
        {
            if (_openWindow != null && _openWindow.IsVisible)
            {
                _openWindow.Activate();
                return;
            }

            _openWindow = new RevitColorPreferencesWindow(mainWindowHandle);
            new WindowInteropHelper(_openWindow)
            {
                Owner = mainWindowHandle
            };
            _openWindow.Closed += (_, __) => _openWindow = null;
            _openWindow.Show();
            _openWindow.Activate();
        }

        public RevitColorPreferencesWindow(IntPtr mainWindowHandle)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();

            _mainWindowHandle = mainWindowHandle;
            IReadOnlyList<RevitRibbonTabDescriptor> discoveredTabs =
                RevitRibbonCatalog.Discover();

            Tabs = new ObservableCollection<RevitRibbonTabColorItem>(
                discoveredTabs.Select(CreateTabItem));
            _selectedTab = Tabs.FirstOrDefault();
            _isGlobalColoringEnabled =
                RevitRibbonColorPreferences.IsGlobalColoringEnabled;
            PreferenceFilePath = UiLanguage.T("Sauvegarde : ", "Saved at: ") +
                RevitRibbonColorPreferences.PreferenceFilePath;
            DiscoveryMessage = Tabs.Count == 0
                ? UiLanguage.T("Aucun onglet n’a pu être détecté. Fermez cette fenêtre et réessayez une fois le ruban Revit entièrement chargé.", "No Tab Could Be Detected. Close This Window and Try Again Once the Revit Ribbon Is Fully Loaded.")
                : Tabs.Count + UiLanguage.T(" onglet(s) détecté(s). Une case décochée limite la couleur au bandeau de titre.", " tab(s) detected. An Unchecked Box Limits the Color to the Title Bar.");
            _autoSaveStatus =
                UiLanguage.T("Les modifications sont enregistrées automatiquement.", "Changes Are Saved Automatically.");

            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;

            foreach (RevitRibbonPanelColorItem panel in
                     Tabs.SelectMany(tab => tab.Panels))
            {
                panel.PropertyChanged += Preference_PropertyChanged;
                panel.Palette.PropertyChanged += (_, __) =>
                    PalettePreferenceChanged(panel);
            }

            DataContext = this;
        }

        public ObservableCollection<RevitRibbonTabColorItem> Tabs { get; }

        public string PreferenceFilePath { get; }

        public string DiscoveryMessage { get; }

        public string AutoSaveStatus
        {
            get => _autoSaveStatus;
            private set
            {
                if (_autoSaveStatus == value)
                    return;

                _autoSaveStatus = value;
                OnPropertyChanged();
            }
        }

        public bool IsGlobalColoringEnabled
        {
            get => _isGlobalColoringEnabled;
            set
            {
                if (_isGlobalColoringEnabled == value)
                    return;

                _isGlobalColoringEnabled = value;
                OnPropertyChanged();
                ScheduleAutoSave();
            }
        }

        public RevitRibbonTabColorItem SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (ReferenceEquals(_selectedTab, value))
                    return;

                _selectedTab = value;
                OnPropertyChanged();
            }
        }

        private RevitRibbonTabColorItem CreateTabItem(
            RevitRibbonTabDescriptor descriptor)
        {
            var panels = descriptor.PanelTitles.Select(panelTitle =>
            {
                RevitRibbonPanelPreference preference =
                    RevitRibbonColorPreferences.GetPanelPreference(
                        descriptor.Title,
                        panelTitle);

                return new RevitRibbonPanelColorItem(
                    descriptor.Title,
                    panelTitle,
                    preference.IsCustomized,
                    preference.IsFullPanel,
                    preference.Scheme);
            });

            return new RevitRibbonTabColorItem(descriptor.Title, panels);
        }

        private void ResetSelectedTabButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SelectedTab == null)
                return;

            foreach (RevitRibbonPanelColorItem panel in SelectedTab.Panels)
                panel.Reset();

            ScheduleAutoSave();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(
            object sender,
            CancelEventArgs e)
        {
            _autoSaveTimer.Stop();
            if (_hasPendingChanges)
                SaveNow(true);
        }

        private void Preference_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            ScheduleAutoSave();
        }

        private void PalettePreferenceChanged(
            RevitRibbonPanelColorItem panel)
        {
            panel.MarkCustomized();
            ScheduleAutoSave();
        }

        private void ScheduleAutoSave()
        {
            if (_autoSaveTimer == null)
                return;

            _hasPendingChanges = true;
            AutoSaveStatus = UiLanguage.T("Modification en cours…", "Applying Changes…");
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        private void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            _autoSaveTimer.Stop();
            SaveNow(false);
        }

        private void SaveNow(bool showError)
        {
            try
            {
                IEnumerable<RevitRibbonPanelPreference> preferences =
                    Tabs.SelectMany(tab => tab.Panels)
                        .Select(panel => panel.CreatePreference());

                RevitRibbonColorPreferences.Save(
                    IsGlobalColoringEnabled,
                    preferences);
                RevitRibbonGlobalColoring.Apply(_mainWindowHandle);
                _hasPendingChanges = false;
                AutoSaveStatus = UiLanguage.T("Modifications enregistrées automatiquement.", "Changes Saved Automatically.");
            }
            catch (Exception ex)
            {
                AutoSaveStatus = UiLanguage.T("Échec de l’enregistrement : ", "Save Failed: ") + ex.Message;
                if (showError)
                {
                    MessageBox.Show(
                        UiLanguage.T("Impossible d’enregistrer les couleurs de Revit.\n\n", "Unable to Save Revit Colors.\n\n") + ex.Message,
                        "BIMaestro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
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

    public sealed class RevitRibbonTabColorItem
    {
        public RevitRibbonTabColorItem(
            string title,
            IEnumerable<RevitRibbonPanelColorItem> panels)
        {
            Title = title;
            Panels = new ObservableCollection<RevitRibbonPanelColorItem>(panels);
        }

        public string Title { get; }

        public ObservableCollection<RevitRibbonPanelColorItem> Panels { get; }
    }

    public sealed class RevitRibbonPanelColorItem : INotifyPropertyChanged
    {
        private bool _isCustomized;
        private bool _isFullPanel;

        public RevitRibbonPanelColorItem(
            string tabTitle,
            string panelTitle,
            bool isCustomized,
            bool isFullPanel,
            RibbonPanelColorScheme scheme)
        {
            TabTitle = tabTitle;
            Palette = new PanelColorItem(panelTitle, scheme);
            _isCustomized = isCustomized;
            _isFullPanel = isFullPanel;
        }

        public string TabTitle { get; }

        public PanelColorItem Palette { get; }

        public bool IsFullPanel
        {
            get => _isFullPanel;
            set
            {
                if (_isFullPanel == value)
                    return;

                _isFullPanel = value;
                _isCustomized = true;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsFullPanel)));
            }
        }

        public bool IsCustomized => _isCustomized;

        public void MarkCustomized()
        {
            _isCustomized = true;
        }

        public RevitRibbonPanelPreference CreatePreference()
        {
            return new RevitRibbonPanelPreference(
                TabTitle,
                Palette.PanelName,
                IsCustomized,
                IsFullPanel,
                Palette.CreateScheme());
        }

        public void Reset()
        {
            Palette.ApplyScheme(
                RevitRibbonColorPreferences.CreateDefaultScheme());
            _isFullPanel = false;
            _isCustomized = false;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsFullPanel)));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
