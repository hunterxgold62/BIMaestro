using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Modification
{
    public partial class PhaseQuickEditWindow : Window
    {
        public PhaseQuickEditWindow(IEnumerable<Phase> phases, int selectedElementCount)
        {
            InitializeComponent();

            SelectionCountText.Text = selectedElementCount + " objet(s) selectionne(s)";

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
                return new PhaseOption("Ne pas modifier", ElementId.InvalidElementId)
                {
                    IsNoChange = true
                };
            }

            public static PhaseOption None()
            {
                return new PhaseOption("Aucune demolition", ElementId.InvalidElementId);
            }
        }
    }
}
