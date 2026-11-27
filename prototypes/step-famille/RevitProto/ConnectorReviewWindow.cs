using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BIMaestro.ProtoStep
{
    public sealed class ConnectorRow
    {
        public bool Keep { get; set; }
        public string Name { get; set; }
        private string label, kind;
        // Réseau ; choisir « Fumées » bascule le connecteur en gaine
        public string Label { get => label; set { label = value; if (value == "Fumées") kind = "Gaine"; } }
        // Type de connecteur Revit : canalisation (tuyauterie) ou gaine (ronde)
        public string Kind { get => kind ?? (label == "Fumées" ? "Gaine" : "Canalisation"); set => kind = value; }
        internal static readonly string[] Kinds = { "Canalisation", "Gaine" };
        public int Diameter { get; set; }
        public string Part { get; set; }
        public string Why { get; set; }
        public double Score { get; set; }
        public double[] P { get; set; }
        public double[] Dir { get; set; }
    }

    // Validation des raccordements détectés : cocher, renommer, choisir le réseau et le DN.
    internal sealed class ConnectorReviewWindow : Window
    {
        internal static readonly string[] Labels = { "Départ", "Retour", "Eau", "Gaz", "Fumées", "Soupape (échappement)", "Évent", "Vidange", "À qualifier" };
        private readonly ObservableCollection<ConnectorRow> rows;

        internal ConnectorReviewWindow(IEnumerable<ConnectorRow> candidates, string title, string introText = null)
        {
            rows = new ObservableCollection<ConnectorRow>(candidates);
            Title = "Raccordements détectés — " + title;
            Width = 1100; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var root = new DockPanel { Margin = new Thickness(12) };
            var intro = new TextBlock
            {
                Text = introText ?? ("Extrémités de tuyauterie libres trouvées dans le STEP (triées par pertinence). Cochez celles qui deviennent des connecteurs Revit, "
                     + "puis corrigez le nom, le réseau et le DN si besoin. Les cases pré-cochées sont des brides dégagées."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "Créer les familles", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            ok.Click += (_, __) => { DialogResult = true; };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Garder", Binding = new Binding("Keep") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            grid.Columns.Add(new DataGridTextColumn { Header = "Nom du connecteur", Binding = new Binding("Name"), Width = 220 });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Réseau", ItemsSource = Labels, SelectedItemBinding = new Binding("Label"), Width = 150 });
            grid.Columns.Add(new DataGridTextColumn { Header = "DN", Binding = new Binding("Diameter"), Width = 55 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Pièce", Binding = new Binding("Part"), IsReadOnly = true, Width = 260 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Indices", Binding = new Binding("Why"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            root.Children.Add(grid);
            Content = root;
        }

        internal List<ConnectorRow> Accepted => rows.Where(r => r.Keep).ToList();
    }
}
