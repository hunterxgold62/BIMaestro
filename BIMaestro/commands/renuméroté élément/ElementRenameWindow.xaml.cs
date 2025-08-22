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
        protected void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private string _prefix;
        public string Prefix { get => _prefix; set { if (_prefix != value) { _prefix = value; OnPropertyChanged(nameof(Prefix)); } } }

        private string _suffix;
        public string Suffix { get => _suffix; set { if (_suffix != value) { _suffix = value; OnPropertyChanged(nameof(Suffix)); } } }

        private string _startNumber;
        public string StartNumber { get => _startNumber; set { if (_startNumber != value) { _startNumber = value; OnPropertyChanged(nameof(StartNumber)); } } }

        private string _selectedNumberFormat;
        public string SelectedNumberFormat
        {
            get => _selectedNumberFormat;
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
        public List<string> NumberFormats { get => _numberFormats; set { if (_numberFormats != value) { _numberFormats = value; OnPropertyChanged(nameof(NumberFormats)); } } }

        private string _bandHeight;
        public string BandHeight { get => _bandHeight; set { if (_bandHeight != value) { _bandHeight = value; OnPropertyChanged(nameof(BandHeight)); } } }

        private bool _isSortByLevelEnabled;
        public bool IsSortByLevelEnabled { get => _isSortByLevelEnabled; set { if (_isSortByLevelEnabled != value) { _isSortByLevelEnabled = value; OnPropertyChanged(nameof(IsSortByLevelEnabled)); } } }

        private List<string> _availableParameters;
        public List<string> AvailableParameters { get => _availableParameters; set { if (_availableParameters != value) { _availableParameters = value; OnPropertyChanged(nameof(AvailableParameters)); } } }

        private string _selectedParameter;
        public string SelectedParameter { get => _selectedParameter; set { if (_selectedParameter != value) { _selectedParameter = value; OnPropertyChanged(nameof(SelectedParameter)); } } }

        public bool IsReset { get; private set; }
        public bool IsNumberingEnabled { get; internal set; }

        public ElementRenamerWindow(List<string> parameters)
        {
            InitializeComponent();
            DataContext = this;

            AvailableParameters = parameters ?? new List<string>();
            if (AvailableParameters.Any())
                SelectedParameter = AvailableParameters.First();

            NumberFormats = new List<string> { "1,2,3...", "A,B,C...", "001,002,003...", "0001,0002,0003..." };
            IsSortByLevelEnabled = false;
            SelectedNumberFormat = NumberFormats[0]; // "1,2,3..."
            StartNumber = "1";
            BandHeight = "1.0"; // sera converti en unités internes
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            IsReset = true;
            DialogResult = true;
            Close();
        }

        private void UpdateStartNumberBasedOnFormat()
        {
            if (SelectedNumberFormat == "1,2,3...")
                StartNumber = "1";
            else if (SelectedNumberFormat == "001,002,003...")
                StartNumber = "001";
            else if (SelectedNumberFormat == "0001,0002,0003...")
                StartNumber = "0001";
            else if (SelectedNumberFormat == "A,B,C...")
                StartNumber = "A";
        }
    }
}
