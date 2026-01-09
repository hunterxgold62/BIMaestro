using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using BIMaestro.Welcome;

namespace BIMaestro.RibbonLayout
{
    public partial class RibbonLayoutWindow : Window, INotifyPropertyChanged
    {
        private PanelViewModel? _selectedPanel;
        private WelcomeState _welcomeState;
        private string _email = string.Empty;
        private string _firstName = string.Empty;
        private string _lastName = string.Empty;

        public RibbonLayoutWindow(IEnumerable<RibbonPanelDefinition> definitions, RibbonLayoutConfig layout)
        {
            InitializeComponent();
            Panels = new ObservableCollection<PanelViewModel>(layout.Panels
                .Select(panel => CreatePanelViewModel(panel, definitions.First(d => d.Name == panel.Name))));

            _welcomeState = WelcomeStorage.LoadOrCreate();
            Email = _welcomeState.Email ?? string.Empty;
            FirstName = _welcomeState.FirstName ?? string.Empty;
            LastName = _welcomeState.LastName ?? string.Empty;

            DataContext = this;
            SelectedPanel = Panels.FirstOrDefault();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<PanelViewModel> Panels { get; }

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
            SaveWelcomeProfile();
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

        public string DisplayName => Name;

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
            DisplayName = displayName;
        }

        public string Id { get; }
        public string DisplayName { get; }
    }
}