using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AnalysePoidsPlugin
{
    public partial class ResultWindow : Window
    {
        private readonly ExternalEvent _evt;
        private readonly SelectionRequestHandler _handler;

        public ResultWindow(List<ElementInfo> elements,
                            double totalMo,
                            ExternalCommandData cmdData)
        {
            InitializeComponent();

            ElementDataGrid.ItemsSource = elements;

            // Calcul des totaux
            double familyTotal = elements
                .Where(e => e.Type == "Famille")
                .Sum(e => e.TailleEnMo);

            double importTotal = elements
                .Where(e => e.Type != "Famille")
                .Sum(e => e.TailleEnMo);

            // Affectation aux TextBlocks
            FamilyTotalText.Text = $"Total Familles : {familyTotal:N2} Mo";
            ImportTotalText.Text = $"Total Imports (PDF/DWG/etc.) : {importTotal:N2} Mo";
            GrandTotalText.Text = $"Total Général : {(familyTotal + importTotal):N2} Mo";

            // Lier la fenêtre à Revit
            new WindowInteropHelper(this).Owner =
                cmdData.Application.MainWindowHandle;

            _handler = new SelectionRequestHandler();
            _evt = ExternalEvent.Create(_handler);

            ElementDataGrid.MouseDoubleClick += OnRowDoubleClick;
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ElementDataGrid.SelectedItem is ElementInfo info
                && info.ElementIds != null
                && info.ElementIds.Count > 0)
            {
                _handler.ElementIds = info.ElementIds;
                _evt.Raise();
            }
        }
    }
}
