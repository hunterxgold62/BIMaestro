using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Input;
using BIMaestro.Welcome;
using BIMaestro.Localization;
using BIMaestro.UI;

namespace BIMaestro.RibbonLayout
{
    public partial class RibbonLayoutWindow : Window, INotifyPropertyChanged
    {
        private const string HelpUrl = "https://www.bimaestro.fr/information?outil=options";
        private PanelViewModel? _selectedPanel;
        private WelcomeState _welcomeState;
        private string _email = string.Empty;
        private string _firstName = string.Empty;
        private string _lastName = string.Empty;
        private UiLanguageOption _selectedLanguage;
        private RadialHotkeyPreference _radialHotkey;
        private string _radialHotkeyText;

        public RibbonLayoutWindow(IEnumerable<RibbonPanelDefinition> definitions, RibbonLayoutConfig layout)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            Panels = new ObservableCollection<PanelViewModel>(layout.Panels
                .Select(panel => CreatePanelViewModel(panel, definitions.First(d => d.Name == panel.Name))));

            _welcomeState = WelcomeStorage.LoadOrCreate();
            Email = _welcomeState.Email ?? string.Empty;
            FirstName = _welcomeState.FirstName ?? string.Empty;
            LastName = _welcomeState.LastName ?? string.Empty;
            LanguageOptions = UiLanguage.Options;
            SelectedLanguage = LanguageOptions.First(option => option.Value == UiLanguage.Choice);
            _radialHotkey = RadialButtonsPreferencesManager.Load().Hotkey;
            _radialHotkeyText = FormatHotkey(_radialHotkey);

            DataContext = this;
            SelectedPanel = Panels.FirstOrDefault();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<PanelViewModel> Panels { get; }
        public IReadOnlyList<UiLanguageOption> LanguageOptions { get; }

        public UiLanguageOption SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (_selectedLanguage == value) return;
                _selectedLanguage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguage)));
            }
        }

        public string Email
        {
            get => _email;
            set
            {
                if (_email == value) return;
                _email = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Email)));
            }
        }

        public string FirstName
        {
            get => _firstName;
            set
            {
                if (_firstName == value) return;
                _firstName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirstName)));
            }
        }

        public string LastName
        {
            get => _lastName;
            set
            {
                if (_lastName == value) return;
                _lastName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastName)));
            }
        }

        public string RadialHotkeyText
        {
            get => string.IsNullOrWhiteSpace(_radialHotkeyText) ? "Aucun raccourci" : _radialHotkeyText;
            private set
            {
                _radialHotkeyText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadialHotkeyText)));
            }
        }

        public PanelViewModel? SelectedPanel
        {
            get => _selectedPanel;
            set
            {
                _selectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPanel)));
            }
        }

        public RibbonLayoutConfig GetUpdatedLayout()
        {
            return new RibbonLayoutConfig
            {
                Panels = Panels
                    .Select(panel => new RibbonPanelConfig
                    {
                        Name = panel.Name,
                        Buttons = panel.Buttons.Select(b => b.Id).ToList()
                    })
                    .ToList()
            };
        }

        private static PanelViewModel CreatePanelViewModel(RibbonPanelConfig panelConfig, RibbonPanelDefinition definition)
        {
            var displayLookup = definition.Items.ToDictionary(i => i.Id, i => i.DisplayName);
            var orderedButtons = panelConfig.Buttons
                .Where(id => displayLookup.ContainsKey(id))
                .Select(id => new ButtonViewModel(id, displayLookup[id]))
                .ToList();

            foreach (var missing in definition.Items.Where(d => orderedButtons.All(b => b.Id != d.Id)))
            {
                orderedButtons.Add(new ButtonViewModel(missing.Id, missing.DisplayName));
            }

            return new PanelViewModel(panelConfig.Name, orderedButtons);
        }

        private void MovePanelUp(object sender, RoutedEventArgs e)
        {
            if (SelectedPanel == null) return;
            var index = Panels.IndexOf(SelectedPanel);
            if (index <= 0) return;
            Panels.Move(index, index - 1);
        }

        private void MovePanelDown(object sender, RoutedEventArgs e)
        {
            if (SelectedPanel == null) return;
            var index = Panels.IndexOf(SelectedPanel);
            if (index < 0 || index >= Panels.Count - 1) return;
            Panels.Move(index, index + 1);
        }

        private void MoveButtonUp(object sender, RoutedEventArgs e)
        {
            if (SelectedPanel?.SelectedButton == null) return;
            var index = SelectedPanel.Buttons.IndexOf(SelectedPanel.SelectedButton);
            if (index <= 0) return;
            SelectedPanel.Buttons.Move(index, index - 1);
        }

        private void MoveButtonDown(object sender, RoutedEventArgs e)
        {
            if (SelectedPanel?.SelectedButton == null) return;
            var index = SelectedPanel.Buttons.IndexOf(SelectedPanel.SelectedButton);
            if (index < 0 || index >= SelectedPanel.Buttons.Count - 1) return;
            SelectedPanel.Buttons.Move(index, index + 1);
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            var previousPreferences = RadialButtonsPreferencesManager.Load();
            if (!RadialGlobalHotkeyService.TryRegister(_radialHotkey, out string hotkeyError))
            {
                RadialGlobalHotkeyService.TryRegister(previousPreferences.Hotkey, out _);
                MessageBox.Show(hotkeyError, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            previousPreferences.Hotkey = _radialHotkey;
            if (!RadialButtonsPreferencesManager.Save(previousPreferences))
            {
                MessageBox.Show("Impossible d’enregistrer le raccourci Rosace Boutons.", "BIMaestro",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool languageChanged = SelectedLanguage != null &&
                SelectedLanguage.Value != UiLanguage.Choice;
            bool languageSaved = true;
            if (SelectedLanguage != null)
                languageSaved = UiLanguage.SetChoice(SelectedLanguage.Value);
            SaveWelcomeProfile();

            if (!languageSaved)
            {
                MessageBox.Show(
                    UiLanguage.T(
                        "Impossible d’enregistrer le choix de langue dans Documents\\RevitLogs\\SauvegardePréférence.",
                        "Unable to save the language choice in Documents\\RevitLogs\\SauvegardePréférence."),
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (languageChanged)
            {
                MessageBox.Show(
                    UiLanguage.T(
                        "Le choix de langue a bien été enregistré. Pour appliquer la nouvelle langue partout dans BIMaestro, il est préférable de sauvegarder votre travail puis de redémarrer Revit.",
                        "Your language choice has been saved. To apply the new language everywhere in BIMaestro, we recommend saving your work and restarting Revit."),
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }

        private void Cancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RadialHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Delete || key == Key.Back)
            {
                SetRadialHotkey(null);
                return;
            }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;

            ModifierKeys modifiers = Keyboard.Modifiers;
            int nativeModifiers = 0;
            if ((modifiers & ModifierKeys.Alt) != 0) nativeModifiers |= 0x0001;
            if ((modifiers & ModifierKeys.Control) != 0) nativeModifiers |= 0x0002;
            if ((modifiers & ModifierKeys.Shift) != 0) nativeModifiers |= 0x0004;
            if ((modifiers & ModifierKeys.Windows) != 0) nativeModifiers |= 0x0008;
            if (nativeModifiers == 0)
            {
                MessageBox.Show("Ajoutez Ctrl, Alt, Maj ou Windows à la touche choisie.", "BIMaestro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetRadialHotkey(new RadialHotkeyPreference
            {
                Modifiers = nativeModifiers,
                VirtualKey = KeyInterop.VirtualKeyFromKey(key)
            });
        }

        private void ClearRadialHotkey_Click(object sender, RoutedEventArgs e) => SetRadialHotkey(null);

        private void SetRadialHotkey(RadialHotkeyPreference hotkey)
        {
            _radialHotkey = hotkey;
            RadialHotkeyText = FormatHotkey(hotkey);
        }

        private static string FormatHotkey(RadialHotkeyPreference hotkey)
        {
            if (hotkey == null) return "Aucun raccourci";
            var parts = new List<string>();
            if ((hotkey.Modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((hotkey.Modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((hotkey.Modifiers & 0x0004) != 0) parts.Add("Maj");
            if ((hotkey.Modifiers & 0x0008) != 0) parts.Add("Windows");
            parts.Add(KeyInterop.KeyFromVirtualKey(hotkey.VirtualKey).ToString());
            return string.Join(" + ", parts);
        }

        private void SaveWelcomeProfile()
        {
            WelcomeManager.UpdateProfileFromSettings(Email, FirstName, LastName);
            _welcomeState = WelcomeStorage.LoadOrCreate();
        }
        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void OpenTerms_Click(object sender, RoutedEventArgs e)
        {
            OpenBundledDocument("ConditionsUtilisation.txt");
        }

        private void OpenPrivacyPolicy_Click(object sender, RoutedEventArgs e)
        {
            OpenBundledDocument("PolitiqueConfidentialite.html");
        }

        private void OpenBundledDocument(string fileName)
        {
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var documentPath = Path.Combine(assemblyDirectory ?? string.Empty, fileName);
                if (!File.Exists(documentPath))
                {
                    MessageBox.Show(
                        $"Le document est introuvable : {documentPath}",
                        "BIMaestro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo(documentPath) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Impossible d’ouvrir le document : {ex.Message}",
                    "BIMaestro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    public class PanelViewModel : INotifyPropertyChanged
    {
        private ButtonViewModel? _selectedButton;

        public PanelViewModel(string name, IEnumerable<ButtonViewModel> buttons)
        {
            Name = name;
            Buttons = new ObservableCollection<ButtonViewModel>(buttons);
        }

        public string Name { get; }

        public string DisplayName => UiLanguage.T(Name);

        public ObservableCollection<ButtonViewModel> Buttons { get; }

        public ButtonViewModel? SelectedButton
        {
            get => _selectedButton;
            set
            {
                _selectedButton = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedButton)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ButtonViewModel
    {
        public ButtonViewModel(string id, string displayName)
        {
            Id = id;
            DisplayName = UiLanguage.T(displayName);
        }

        public string Id { get; }
        public string DisplayName { get; }
    }
}
