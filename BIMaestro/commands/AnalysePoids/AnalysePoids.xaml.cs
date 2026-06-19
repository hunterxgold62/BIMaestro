using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Analyse
{
    public partial class ResultWindow : Window
    {
        private const string HelpUrl = "https://www.bimaestro.fr/analyse?outil=analyse-poids";
        private readonly ObservableCollection<ElementInfo> _elements;
        private readonly SelectionRequestHandler _selectionHandler;
        private readonly ExternalEvent _selectionEvent;
        private readonly DeleteFamilyRequestHandler _deleteHandler;
        private readonly ExternalEvent _deleteEvent;

        public ResultWindow(List<ElementInfo> elements,
                            double totalMo,
                            ExternalCommandData cmdData)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _ = totalMo;

            _elements = new ObservableCollection<ElementInfo>(elements ?? new List<ElementInfo>());
            ElementDataGrid.ItemsSource = _elements;

            UpdateTotals();

            new WindowInteropHelper(this).Owner =
                cmdData.Application.MainWindowHandle;

            _selectionHandler = new SelectionRequestHandler();
            _selectionEvent = ExternalEvent.Create(_selectionHandler);

            _deleteHandler = new DeleteFamilyRequestHandler();
            _deleteEvent = ExternalEvent.Create(_deleteHandler);

            ElementDataGrid.MouseDoubleClick += OnRowDoubleClick;
            ElementDataGrid.LoadingRow += OnLoadingRow;
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HelpUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d’ouvrir la page d’aide : {ex.Message}", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnLoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.ContextMenu == null)
            {
                e.Row.ContextMenu = CreateRowContextMenu();
            }
        }

        private ContextMenu CreateRowContextMenu()
        {
            var contextMenu = new ContextMenu();
            var deleteMenuItem = new MenuItem
            {
                Header = "Supprimer du projet"
            };
            deleteMenuItem.Click += OnDeleteFamilyClick;

            contextMenu.Items.Add(deleteMenuItem);

            contextMenu.Opened += (s, args) =>
            {
                if (s is ContextMenu menu && menu.PlacementTarget is FrameworkElement element)
                {
                    if (element.DataContext is ElementInfo info)
                    {
                        deleteMenuItem.DataContext = info;
                        deleteMenuItem.Visibility = info.IsFamily ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else
                    {
                        deleteMenuItem.DataContext = null;
                        deleteMenuItem.Visibility = Visibility.Collapsed;
                    }
                }
            };

            return contextMenu;
        }

        private void UpdateTotals()
        {
            double familyTotal = _elements
                .Where(e => e.IsFamily)
                .Sum(e => e.TailleEnMo);

            double importTotal = _elements
                .Where(e => !e.IsFamily)
                .Sum(e => e.TailleEnMo);

            FamilyTotalText.Text = $"Total Familles : {familyTotal:N2} Mo";
            ImportTotalText.Text = $"Total Imports (PDF/DWG/etc.) : {importTotal:N2} Mo";
            GrandTotalText.Text = $"Total Général : {(familyTotal + importTotal):N2} Mo";
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ElementDataGrid.SelectedItem is ElementInfo info
                && info.ElementIds != null
                && info.ElementIds.Count > 0)
            {
                _selectionHandler.ElementIds = info.ElementIds;
                _selectionEvent.Raise();
            }
        }

        private void OnDeleteFamilyClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem &&
                menuItem.DataContext is ElementInfo info &&
                info.IsFamily)
            {
                if (info.PrimaryId == null)
                {
                    MessageBox.Show(this,
                        "Impossible de déterminer l'identifiant de la famille à supprimer.",
                        "Suppression impossible",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var confirmation = MessageBox.Show(this,
                    $"Supprimer définitivement la famille \"{info.Nom}\" du projet ?\nToutes ses instances seront également supprimées.",
                    "Supprimer la famille",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);

                if (confirmation != MessageBoxResult.Yes)
                {
                    return;
                }

                _deleteHandler.FamilyId = info.PrimaryId;
                _deleteHandler.OnCompleted = success =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (success)
                        {
                            _elements.Remove(info);
                            UpdateTotals();
                        }
                    });
                };

                _deleteEvent.Raise();
            }
        }
    }
}
