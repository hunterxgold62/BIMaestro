using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BIMaestro.Localization;

namespace Modification
{
    public partial class PhaseQuickEditWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/modification?outil=phases-rapides";

        public PhaseQuickEditWindow(IEnumerable<Phase> phases, int selectedElementCount)
        {
            InitializeComponent();

            SelectionCountText.Text = UiLanguage.T(
                selectedElementCount + " objet(s) selectionne(s)",
                selectedElementCount + " selected object(s)");

            List<PhaseOption> projectPhases = phases
                .Select(phase => new PhaseOption(phase.Name, phase.Id))
                .ToList();

            var createdOptions = new List<PhaseOption>();
            createdOptions.Add(PhaseOption.NoChange());
            createdOptions.AddRange(projectPhases);

            var demolishedOptions = new List<PhaseOption>();
            demolishedOptions.Add(PhaseOption.NoChange());
            demolishedOptions.Add(PhaseOption.None());
            demolishedOptions.AddRange(projectPhases);

            CreatedPhaseCombo.ItemsSource = createdOptions;
            DemolishedPhaseCombo.ItemsSource = demolishedOptions;
            CreatedPhaseCombo.SelectedIndex = 0;
            DemolishedPhaseCombo.SelectedIndex = 0;
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"),
                    "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public bool ChangeCreatedPhase
        {
            get
            {
                PhaseOption option = CreatedPhaseCombo.SelectedItem as PhaseOption;
                return option != null && !option.IsNoChange;
            }
        }

        public bool ChangeDemolishedPhase
        {
            get
            {
                PhaseOption option = DemolishedPhaseCombo.SelectedItem as PhaseOption;
                return option != null && !option.IsNoChange;
            }
        }

        public ElementId SelectedCreatedPhaseId
        {
            get
            {
                PhaseOption option = CreatedPhaseCombo.SelectedItem as PhaseOption;
                return option == null ? ElementId.InvalidElementId : option.PhaseId;
            }
        }

        public ElementId SelectedDemolishedPhaseId
        {
            get
            {
                PhaseOption option = DemolishedPhaseCombo.SelectedItem as PhaseOption;
                return option == null ? ElementId.InvalidElementId : option.PhaseId;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private class PhaseOption
        {
            public PhaseOption(string name, ElementId phaseId)
            {
                Name = name;
                PhaseId = phaseId;
            }

            public string Name { get; private set; }

            public ElementId PhaseId { get; private set; }

            public bool IsNoChange { get; private set; }

            public static PhaseOption NoChange()
            {
                return new PhaseOption(UiLanguage.T("Ne pas modifier", "Do not change"), ElementId.InvalidElementId)
                {
                    IsNoChange = true
                };
            }

            public static PhaseOption None()
            {
                return new PhaseOption(UiLanguage.T("Aucune demolition", "None"), ElementId.InvalidElementId);
            }
        }
    }
}
