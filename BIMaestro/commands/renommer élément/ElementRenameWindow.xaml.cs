using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace Modification
{
    public partial class ElementRenamerWindow : Window, INotifyPropertyChanged
    {
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

        private bool _isNumberingEnabled;
        public bool IsNumberingEnabled
        {
            get { return _isNumberingEnabled; }
            set
            {
                if (_isNumberingEnabled != value)
                {
                    _isNumberingEnabled = value;
                    OnPropertyChanged(nameof(IsNumberingEnabled));
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

        public bool IsReset { get; private set; } // Propriété pour savoir si l'utilisateur veut réinitialiser

        public ElementRenamerWindow(List<string> parameters)
        {
            InitializeComponent();
            this.DataContext = this;

            AvailableParameters = parameters ?? new List<string>();
            if (AvailableParameters.Any())
            {
                SelectedParameter = AvailableParameters.First();
            }

            // Initialiser la liste des formats de numérotation
            NumberFormats = new List<string> { "1,2,3...", "A,B,C...", "001,002,003..." };

            // Valeurs par défaut
            IsNumberingEnabled = true;
            IsSortByLevelEnabled = false;
            SelectedNumberFormat = NumberFormats[0]; // "1,2,3..." par défaut
            StartNumber = "1";
            BandHeight = "1.0"; // Valeur par défaut pour la hauteur de bande
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
            else if (SelectedNumberFormat == "A,B,C...")
            {
                StartNumber = "A";
            }
        }
    }
}
