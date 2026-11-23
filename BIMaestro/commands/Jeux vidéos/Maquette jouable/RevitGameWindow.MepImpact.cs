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
            var heading = new TextBlock { Text = UiLanguage.Get("RevitGameWindow.MepImpact.ShutoffAnalysisIndicativeFlows"), FontSize = 22, FontWeight = FontWeights.SemiBold };
            top.Children.Add(heading);
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10), FontSize = 14 }; top.Children.Add(summary);
            var commands = new WrapPanel(); top.Children.Add(commands);
            Button AddButton(string text, Action action)
            {
                var button = new Button { Content = text, Margin = new Thickness(0,0,8,8), Padding = new Thickness(10,6,10,6), FontSize = 14 };
                button.Click += (_, __) => { if (!_mepRecalculationRunning) { try { action(); } catch (Exception error) { ShowToast(error.Message); } } };
                commands.Children.Add(button); return button;
            }
            AddButton(UiLanguage.Get("RevitGameWindow.MepImpact.SetCurrentStateAsBaseline"), () => { GameMepImpactAnalyzer.SetReference(graph, UiLanguage.Get("RevitGameWindow.MepImpact.BaselineScenarioSelected")); _refreshMepImpact?.Invoke(); });
            AddButton(UiLanguage.Get("RevitGameWindow.MepImpact.ExportReport"), () => {
                var dialog = new SaveFileDialog { FileName = "BIMaestro-analyse-coupures.txt", Filter = "Compte rendu texte|*.txt" };
                if (dialog.ShowDialog(_mepImpactWindow) == true) File.WriteAllText(dialog.FileName, GameMepImpactAnalyzer.ToText(graph), new System.Text.UTF8Encoding(true));
            });
            var equipmentOnly = new CheckBox { Content = UiLanguage.Get("RevitGameWindow.MepImpact.EquipmentOnly"), Margin = new Thickness(0,4,0,8), FontSize = 14 };
            top.Children.Add(equipmentOnly);
            var implicitEnds = new CheckBox { Content = UiLanguage.Get("RevitGameWindow.MepImpact.AllowUnspecifiedEndsAsPossibleOutlets"), Margin = new Thickness(0,4,0,8), FontSize = 14 };
            top.Children.Add(implicitEnds);
            implicitEnds.Click += (_, __) => ExecuteMepScenarioMutation(UiLanguage.Get("RevitGameWindow.MepImpact.ChangeNetworkBoundaries"), UiLanguage.Get("RevitGameWindow.MepImpact.NetworkBoundariesRecalculated"), () => graph.AllowImplicitTerminals = implicitEnds.IsChecked == true);
            var limits = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0,0,0,10) }; top.Children.Add(limits);
            var endpointRow = new WrapPanel { Margin = new Thickness(0,0,0,10) }; top.Children.Add(endpointRow);
            endpointRow.Children.Add(new TextBlock { Text = UiLanguage.Get("RevitGameWindow.MepImpact.EndOfTheSelectedElement"), VerticalAlignment = VerticalAlignment.Center });
            var endpoint = new ComboBox { Width = 180, DisplayMemberPath = "Key", Margin = new Thickness(4) }; endpointRow.Children.Add(endpoint);
            var role = new ComboBox { Width = 150, ItemsSource = new[] { UiLanguage.Get("RevitGameWindow.MepImpact.ToCheck"), "Terminal", UiLanguage.Get("RevitGameWindow.MepImpact.Return"), UiLanguage.Get("RevitGameWindow.MepImpact.ExportBoundary"), UiLanguage.Get("RevitGameWindow.MepImpact.Cap") }, Margin = new Thickness(4) }; endpointRow.Children.Add(role);
            var saveRole = new Button { Content = "Appliquer", Padding = new Thickness(8,4,8,4), Margin = new Thickness(4) }; endpointRow.Children.Add(saveRole);
            endpoint.SelectionChanged += (_, __) => { if (endpoint.SelectedItem is GameMepConnectorData c) role.SelectedIndex = (int)c.EndpointRole; };
            saveRole.Click += (_, __) => {
                if (endpoint.SelectedItem is GameMepConnectorData c && role.SelectedIndex >= 0)
                    ExecuteMepScenarioMutation(UiLanguage.Get("RevitGameWindow.MepImpact.QualifyAnEnd"), UiLanguage.Get("RevitGameWindow.MepImpact.EndQualified"), () => c.EndpointRole = (GameMepEndpointRole)role.SelectedIndex);
            };
            var tabs = new TabControl(); root.Children.Add(tabs);
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, FontSize = 14 };
            foreach (var column in new[] { new[] { UiLanguage.Get("RevitGameWindow.MepImpact.Element"), "Name" }, new[] { UiLanguage.Get("RevitGameWindow.MepImpact.Change"), "Change" }, new[] { UiLanguage.Get("RevitGameWindow.MepImpact.Before"), "Before" }, new[] { UiLanguage.Get("RevitGameWindow.MepImpact.After"), "After" } })
                grid.Columns.Add(new DataGridTextColumn { Header = column[0], Binding = new Binding(column[1]), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.MouseDoubleClick += (_, __) => { if (grid.SelectedItem is GameMepImpactItem item) NavigateMepImpact(item.ElementKey); };
            tabs.Items.Add(new TabItem { Header = UiLanguage.Get("RevitGameWindow.MepImpact.BeforeAfterDoubleClickToGoTo"), Content = grid });
            var diagnostics = new ListBox { DisplayMemberPath = "Title", FontSize = 14 };
            diagnostics.MouseDoubleClick += (_, __) => { if (diagnostics.SelectedItem is GameMepDiagnosticData item) NavigateMepImpact(item.ElementKey); };
            tabs.Items.Add(new TabItem { Header = UiLanguage.Get("RevitGameWindow.MepImpact.DiagnosticsDoubleClickToGoTo"), Content = diagnostics });
            var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14 };
            tabs.Items.Add(new TabItem { Header = UiLanguage.Get("RevitGameWindow.MepImpact.ReportAndPaths"), Content = detail });
            _refreshMepImpact = () => {
                var report = graph.ImpactReport;
                commands.IsEnabled = endpointRow.IsEnabled = implicitEnds.IsEnabled = !_mepRecalculationRunning;
                summary.Text = _mepRecalculationRunning ? UiLanguage.Get("RevitGameWindow.MepImpact.RecalculatingResultsTemporarilyUnavailable") : report == null ? UiLanguage.Get("RevitGameWindow.MepImpact.AnalysisUnavailable") :
                    report.ReferenceLabel + "\n" + report.EquipmentLostArrivalCount + UiLanguage.Get("RevitGameWindow.MepImpact.EquipmentItemsWithoutConnectionToASupply") + report.LostArrivalCount + UiLanguage.Get("RevitGameWindow.MepImpact.ElementsInTotal") + report.AlternativeCount + UiLanguage.Get("RevitGameWindow.MepImpact.AlternativePathsIdentified");
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
            _mepImpactWindow = new Window { Title = UiLanguage.Get("RevitGameWindow.MepImpact.BIMaestroShutoffAnalysis"), Owner = this, Width = 1080, Height = 720, MinWidth = 760, MinHeight = 520,
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
            UpdateCamera(foot); ShowToast(UiLanguage.Get("RevitGameWindow.MepImpact.WentToElement") + element.Name);
        }
    }
}
