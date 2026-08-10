using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using BIMaestro.Welcome;
using BIMaestro.Localization;

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
