using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using BIMaestro.Localization;

namespace Modification
{
    public partial class ElementRenamerWindow : Window, INotifyPropertyChanged
    {
        private const string HelpUrl = "https://www.bimaestro.fr/modification?outil=organisateur";
        public event PropertyChangedEventHandler PropertyChanged;

        // Méthode pour notifier les changements de propriété
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Propriétés avec implémentation de notification
        private string _prefix;
        public string Prefix
        {
            get { return _prefix; }
            set
            {
                if (_prefix != value)
                {
                    _prefix = value;
                    OnPropertyChanged(nameof(Prefix));
                }
            }
        }

        private string _suffix;
        public string Suffix
        {
            get { return _suffix; }
            set
            {
                if (_suffix != value)
                {
                    _suffix = value;
                    OnPropertyChanged(nameof(Suffix));
                }
            }
        }

        private string _startNumber;
        public string StartNumber
        {
            get { return _startNumber; }
            set
            {
                if (_startNumber != value)
                {
                    _startNumber = value;
                    OnPropertyChanged(nameof(StartNumber));
                }
            }
        }

        private string _selectedNumberFormat;
        public string SelectedNumberFormat
        {
            get { return _selectedNumberFormat; }
            set
            {
                if (_selectedNumberFormat != value)
                {
                    _selectedNumberFormat = value;
                    OnPropertyChanged(nameof(SelectedNumberFormat));
                    UpdateStartNumberBasedOnFormat();
                }
            }
        }

        private List<string> _numberFormats;
        public List<string> NumberFormats
        {
            get { return _numberFormats; }
            set
            {
                if (_numberFormats != value)
                {
                    _numberFormats = value;
                    OnPropertyChanged(nameof(NumberFormats));
                }
            }
        }

        private string _bandHeight;
        public string BandHeight
        {
            get { return _bandHeight; }
            set
            {
                if (_bandHeight != value)
                {
                    _bandHeight = value;
                    OnPropertyChanged(nameof(BandHeight));
                }
            }
        }


        private bool _isSortByLevelEnabled;
        public bool IsSortByLevelEnabled
        {
            get { return _isSortByLevelEnabled; }
            set
            {
                if (_isSortByLevelEnabled != value)
                {
                    _isSortByLevelEnabled = value;
                    OnPropertyChanged(nameof(IsSortByLevelEnabled));
                }
            }
        }

        private List<string> _availableParameters;
        public List<string> AvailableParameters
        {
            get { return _availableParameters; }
            set
            {
                if (_availableParameters != value)
                {
                    _availableParameters = value;
                    OnPropertyChanged(nameof(AvailableParameters));
                }
            }
        }

        private string _selectedParameter;
        public string SelectedParameter
        {
            get { return _selectedParameter; }
            set
            {
                if (_selectedParameter != value)
                {
                    _selectedParameter = value;
                    OnPropertyChanged(nameof(SelectedParameter));
                }
            }
        }

        public bool IsViewportMode { get; }
        public bool CanReset => !IsViewportMode;
        public Visibility LevelSortVisibility => IsViewportMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        public string WindowSubtitle => IsViewportMode
            ? UiLanguage.T("Numérotez les pastilles ou renommez les vues selon leur position sur la feuille.", "Number View Tags or Rename Views Based on Their Position on the Sheet.")
            : UiLanguage.T("Configurez le paramètre cible, la structure de nommage et lancez le renommage en un clic.", "Configure the Target Parameter and Naming Structure, Then Rename in One Click.");
        public string BandHeightLabel => IsViewportMode
            ? UiLanguage.T("Tolérance de ligne (mm) :", "Row Tolerance (mm):")
            : UiLanguage.T("Hauteur de bande (m) :", "Band Height (m):");
        public string BandHeightToolTip => IsViewportMode
            ? UiLanguage.T("Deux fenêtres de vue dont les centres sont proches verticalement sont considérées sur la même ligne, puis triées de gauche à droite.", "Two Viewports with Vertically Close Centers Are Treated as Being on the Same Row, Then Sorted Left to Right.")
            : UiLanguage.T("Les éléments sont regroupés par bandes horizontales de cette hauteur et triés de gauche à droite.", "Elements Are Grouped into Horizontal Bands of This Height and Sorted Left to Right.");

        public bool IsReset { get; private set; } // Propriété pour savoir si l'utilisateur veut réinitialiser
        public bool IsNumberingEnabled { get; internal set; }

        public ElementRenamerWindow(List<string> parameters, bool isViewportMode = false)
        {
            IsViewportMode = isViewportMode;
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            this.DataContext = this;

            AvailableParameters = parameters ?? new List<string>();
            if (AvailableParameters.Any())
            {
                SelectedParameter = AvailableParameters.First();
            }

            // Initialiser la liste des formats de numérotation
            NumberFormats = new List<string> { "1,2,3...", "A,B,C...", "001,002,003...", "0001,0002,0003..." };
            // Valeurs par défaut
            IsSortByLevelEnabled = false;
            SelectedNumberFormat = NumberFormats[0]; // "1,2,3..." par défaut
            StartNumber = "1";
            BandHeight = IsViewportMode ? "20" : "1.0";
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            // Lorsque l'utilisateur clique sur le bouton Renommer, fermer la fenêtre
            this.DialogResult = true;
            this.Close();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            // Définir la propriété IsReset à true pour indiquer que l'utilisateur a demandé une réinitialisation
            IsReset = true;
            this.DialogResult = true;
            this.Close();
        }

        private void UpdateStartNumberBasedOnFormat()
        {
            if (SelectedNumberFormat == "1,2,3...")
            {
                StartNumber = "1";
            }
            else if (SelectedNumberFormat == "001,002,003...")
            {
                StartNumber = "001";

            }

            else if (SelectedNumberFormat == "0001,0002,0003...")
            {
                StartNumber = "0001";
            }

            else if (SelectedNumberFormat == "A,B,C...")
            {
                StartNumber = "A";
            }
        }
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiLanguage.T("Impossible d’ouvrir la page d’aide : ", "Unable to Open the Help Page: ") + ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
