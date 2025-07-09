using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Autodesk.Revit.UI;    // pour TaskDialog si besoin
using Newtonsoft.Json;

namespace YourNamespace
{
    public partial class ParameterSettingsWindow : Window
    {
        // Chemin relatif sous Mes Documents → OneDrive
        const string SubFolder = "RevitLogs\\SauvegardePréférence";
        const string FileName = "preferenceunité.json";

        // Collection pour le DataGrid
        public ObservableCollection<PreferenceViewModel> Preferences { get; set; }
        // Options de regroupement
        public ObservableCollection<string> GroupingOptions { get; }
        public string SelectedGrouping { get; set; }

        public ParameterSettingsWindow()
        {
            InitializeComponent();

            // Prépare dossier & fichier
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = Path.Combine(docs, SubFolder);
            Directory.CreateDirectory(folder);
            var fullPath = Path.Combine(folder, FileName);

            // Charge JSON ou valeurs par défaut
            if (File.Exists(fullPath))
            {
                var json = File.ReadAllText(fullPath);
                Preferences = JsonConvert
                    .DeserializeObject<ObservableCollection<PreferenceViewModel>>(json);
            }
            else
            {
                Preferences = GetDefaultPreferences();
            }

            // Initialise le ComboBox de regroupement
            GroupingOptions = new ObservableCollection<string>
            {
                "123456789.00",
                "123 456 789.00",
                "123 456 789.00",
                "123,456,789.00",
                "123'456'789.00"
            };
            SelectedGrouping = GroupingOptions[1];

            DataContext = this;
        }

        private ObservableCollection<PreferenceViewModel> GetDefaultPreferences()
        {
            return new ObservableCollection<PreferenceViewModel>
            {
                new PreferenceViewModel("Angle",               new[]{ "°" },                                   "°",    2.00),
                new PreferenceViewModel("Angle de rotation",   new[]{ "°" },                                   "°",    2.00),
                new PreferenceViewModel("Inclinaison",         new[]{ "°" },                                   "°",    2.00),
                new PreferenceViewModel("Distance",            new[]{ "m","cm","ft" },                         "m",    2.00),
                new PreferenceViewModel("Longueur",            new[]{ "m","cm","ft","ft-in" },                  "m",    3.00),
                new PreferenceViewModel("Surface",             new[]{ "m²","ft²" },                            "m²",   0.00),
                new PreferenceViewModel("Volume",              new[]{ "m³","ft³" },                            "m³",   2.00),
                new PreferenceViewModel("Densité de la masse", new[]{ "kg/m³","lb/ft³" },                      "kg/m³",2.00),
                new PreferenceViewModel("Vitesse",             new[]{ "km/h","mph" },                          "km/h", 1.00),
                new PreferenceViewModel("Temps",               new[]{ "h","s" },                               "h",    2.00),
                new PreferenceViewModel("Coût par surface",    new[]{ "$/m²","€/m²" },                         "$/m²", 0.00),
                new PreferenceViewModel("Devise",              new[]{ "$","€" },                               "$",    2.00)
            };
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            // Sérialise vos préférences (unités + décimales)
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var folder = Path.Combine(docs, SubFolder);
                var fullPath = Path.Combine(folder, FileName);
                var json = JsonConvert.SerializeObject(Preferences, Formatting.Indented);
                File.WriteAllText(fullPath, json);

                MessageBox.Show($"Préférences enregistrées :\n{fullPath}",
                                "Enregistré", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de l’enregistrement :\n{ex.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnHelpClick(object sender, RoutedEventArgs e)
        {
            // Affiche l’aide Revit native
            TaskDialog.Show("Aide", "Ouvrez l’aide Revit pour plus d’informations sur les unités.");
        }
    }

    /// <summary>
    /// Modèle unique partagé par les deux commandes
    /// </summary>
    public class PreferenceViewModel
    {
        public string SpecType { get; set; }
        public string[] UnitOptions { get; set; }
        public string SelectedUnit { get; set; }
        public double Accuracy { get; set; }

        [Newtonsoft.Json.JsonConstructor]
        public PreferenceViewModel(string specType, string[] unitOptions, string selectedUnit, double accuracy)
        {
            SpecType = specType;
            UnitOptions = unitOptions;
            SelectedUnit = selectedUnit;
            Accuracy = accuracy;
        }
    }
}
