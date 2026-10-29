using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using BIMaestro.Localization;

namespace BIMaestro.VideoGames
{
    public partial class RevitGameWindow
    {
        private Window _mepImpactWindow;
        private Action _refreshMepImpact;

        private void MepImpactButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mepImpactWindow != null) { _refreshMepImpact?.Invoke(); _mepImpactWindow.Activate(); return; }
            var graph = _scene.MepGraph;
            var root = new DockPanel { Margin = new Thickness(18) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            var heading = new TextBlock { Text = UiLanguage.T("Analyse des coupures — flux indicatifs", "Shutoff Analysis — Indicative Flows"), FontSize = 22, FontWeight = FontWeights.SemiBold };
            top.Children.Add(heading);
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10), FontSize = 14 }; top.Children.Add(summary);
            var commands = new WrapPanel(); top.Children.Add(commands);
            Button AddButton(string text, Action action)
            {
                var button = new Button { Content = text, Margin = new Thickness(0,0,8,8), Padding = new Thickness(10,6,10,6), FontSize = 14 };
                button.Click += (_, __) => { if (!_mepRecalculationRunning) { try { action(); } catch (Exception error) { ShowToast(error.Message); } } };
                commands.Children.Add(button); return button;
            }
            AddButton(UiLanguage.T("Définir l'état actuel comme référence", "Set Current State as Baseline"), () => { GameMepImpactAnalyzer.SetReference(graph, UiLanguage.T("Scénario de référence choisi", "Baseline scenario selected")); _refreshMepImpact?.Invoke(); });
            AddButton(UiLanguage.T("Exporter le compte rendu", "Export Report"), () => {
                var dialog = new SaveFileDialog { FileName = "BIMaestro-analyse-coupures.txt", Filter = "Compte rendu texte|*.txt" };
                if (dialog.ShowDialog(_mepImpactWindow) == true) File.WriteAllText(dialog.FileName, GameMepImpactAnalyzer.ToText(graph), new System.Text.UTF8Encoding(true));
            });
            var equipmentOnly = new CheckBox { Content = UiLanguage.T("Équipements uniquement", "Equipment only"), Margin = new Thickness(0,4,0,8), FontSize = 14 };
            top.Children.Add(equipmentOnly);
            var implicitEnds = new CheckBox { Content = UiLanguage.T("Autoriser les extrémités non renseignées comme débouchés possibles", "Allow unspecified ends as possible outlets"), Margin = new Thickness(0,4,0,8), FontSize = 14 };
            top.Children.Add(implicitEnds);
            implicitEnds.Click += (_, __) => ExecuteMepScenarioMutation(UiLanguage.T("Modifier les limites du réseau", "Change network boundaries"), UiLanguage.T("Limites du réseau recalculées", "Network boundaries recalculated"), () => graph.AllowImplicitTerminals = implicitEnds.IsChecked == true);
            var limits = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0,0,0,10) }; top.Children.Add(limits);
            var endpointRow = new WrapPanel { Margin = new Thickness(0,0,0,10) }; top.Children.Add(endpointRow);
            endpointRow.Children.Add(new TextBlock { Text = UiLanguage.T("Extrémité de l'élément sélectionné : ", "End of the selected element: "), VerticalAlignment = VerticalAlignment.Center });
            var endpoint = new ComboBox { Width = 180, DisplayMemberPath = "Key", Margin = new Thickness(4) }; endpointRow.Children.Add(endpoint);
            var role = new ComboBox { Width = 150, ItemsSource = new[] { UiLanguage.T("À vérifier", "To check"), "Terminal", UiLanguage.T("Retour", "Return"), UiLanguage.T("Limite d'export", "Export boundary"), UiLanguage.T("Bouchon", "Cap") }, Margin = new Thickness(4) }; endpointRow.Children.Add(role);
            var saveRole = new Button { Content = "Appliquer", Padding = new Thickness(8,4,8,4), Margin = new Thickness(4) }; endpointRow.Children.Add(saveRole);
            endpoint.SelectionChanged += (_, __) => { if (endpoint.SelectedItem is GameMepConnectorData c) role.SelectedIndex = (int)c.EndpointRole; };
            saveRole.Click += (_, __) => {
                if (endpoint.SelectedItem is GameMepConnectorData c && role.SelectedIndex >= 0)
                    ExecuteMepScenarioMutation(UiLanguage.T("Qualifier une extrémité", "Qualify an end"), UiLanguage.T("Extrémité qualifiée", "End qualified"), () => c.EndpointRole = (GameMepEndpointRole)role.SelectedIndex);
            };
            var tabs = new TabControl(); root.Children.Add(tabs);
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, FontSize = 14 };
            foreach (var column in new[] { new[] { UiLanguage.T("Élément", "Element"), "Name" }, new[] { UiLanguage.T("Changement", "Change"), "Change" }, new[] { UiLanguage.T("Avant", "Before"), "Before" }, new[] { UiLanguage.T("Après", "After"), "After" } })
                grid.Columns.Add(new DataGridTextColumn { Header = column[0], Binding = new Binding(column[1]), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.MouseDoubleClick += (_, __) => { if (grid.SelectedItem is GameMepImpactItem item) NavigateMepImpact(item.ElementKey); };
            tabs.Items.Add(new TabItem { Header = UiLanguage.T("Avant / après — double-cliquer pour rejoindre", "Before / After — double-click to go to"), Content = grid });
            var diagnostics = new ListBox { DisplayMemberPath = "Title", FontSize = 14 };
            diagnostics.MouseDoubleClick += (_, __) => { if (diagnostics.SelectedItem is GameMepDiagnosticData item) NavigateMepImpact(item.ElementKey); };
            tabs.Items.Add(new TabItem { Header = UiLanguage.T("Diagnostics — double-cliquer pour rejoindre", "Diagnostics — double-click to go to"), Content = diagnostics });
            var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14 };
            tabs.Items.Add(new TabItem { Header = UiLanguage.T("Compte rendu et chemins", "Report and Paths"), Content = detail });
            _refreshMepImpact = () => {
                var report = graph.ImpactReport;
                commands.IsEnabled = endpointRow.IsEnabled = implicitEnds.IsEnabled = !_mepRecalculationRunning;
                summary.Text = _mepRecalculationRunning ? UiLanguage.T("Recalcul en cours — résultats temporairement indisponibles", "Recalculating — results temporarily unavailable") : report == null ? UiLanguage.T("Analyse indisponible", "Analysis unavailable") :
                    report.ReferenceLabel + "\n" + report.EquipmentLostArrivalCount + UiLanguage.T(" équipements sans connexion à une arrivée ; ", " equipment items without connection to a supply; ") + report.LostArrivalCount + UiLanguage.T(" éléments au total ; ", " elements in total; ") + report.AlternativeCount + UiLanguage.T(" chemins alternatifs identifiés.", " alternative paths identified.");
                grid.ItemsSource = _mepRecalculationRunning ? null : report?.Items.Where(i => equipmentOnly.IsChecked != true || i.IsEquipment).ToList();
                diagnostics.ItemsSource = _mepRecalculationRunning ? null : graph.Diagnostics.Where(d => !d.IsAggregate).ToList();
                detail.Text = _mepRecalculationRunning ? "" : GameMepImpactAnalyzer.ToText(graph);
                limits.Text = report == null ? "" : string.Join("\n", report.Assumptions);
                implicitEnds.IsChecked = graph.AllowImplicitTerminals;
                string selectedKey = _currentSelectedElement.FirstOrDefault()?.Element.Key;
                var choices = graph.Connectors.Where(c => c.ElementKey == selectedKey && (!c.IsConnected || graph.FindElement(selectedKey)?.ConnectorIndices.Count == 1)).ToList();
                endpoint.ItemsSource = choices; if (choices.Count > 0) endpoint.SelectedIndex = 0;
            };
            equipmentOnly.Click += (_, __) => _refreshMepImpact?.Invoke();
            _mepImpactWindow = new Window { Title = UiLanguage.T("BIMaestro — Analyse des coupures", "BIMaestro — Shutoff Analysis"), Owner = this, Width = 1080, Height = 720, MinWidth = 760, MinHeight = 520,
                Background = Brushes.White, Foreground = Brushes.Black, Content = root, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            _mepImpactWindow.Closed += (_, __) => { _mepImpactWindow = null; _refreshMepImpact = null; };
            _refreshMepImpact(); _mepImpactWindow.Show();
        }

        private void NavigateMepImpact(string key)
        {
            var element = _scene.MepGraph.FindElement(key);
            if (element == null || element.ConnectorIndices.Count == 0) return;
            int index = element.ConnectorIndices[0];
            if (index < 0 || index >= _scene.MepGraph.Connectors.Count) return;
            var target = _scene.MepGraph.Connectors[index].Position;
            if (!TryCreateDiagnosticFlightViewpoint(target, out var foot)) return;
            HighlightMepDiagnosticElement(key);
            var visual = _scene.Elements.FirstOrDefault(e => e.Key == key); if (visual != null) AddSelectedElement(visual);
            _footPosition = _previousFootPosition = _renderFootPosition = foot;
            _verticalVelocity = 0; _grounded = false; _flyMode = true; _isCrouching = false; _currentEyeHeight = EyeHeight;
            Vector3D direction = target - new Point3D(foot.X, foot.Y, foot.Z + EyeHeight);
            if (direction.LengthSquared > 1e-8) { _yaw = Math.Atan2(direction.Y, direction.X); _pitch = Math.Atan2(direction.Z, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y)); }
            UpdateCamera(foot); ShowToast(UiLanguage.T("Élément rejoint : ", "Went to element: ") + element.Name);
        }
    }
}
