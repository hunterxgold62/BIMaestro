using Autodesk.Revit.UI;
using System;
using BIMaestro.Localization;
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
        private readonly DeleteElementRequestHandler _deleteHandler;
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

            _deleteHandler = new DeleteElementRequestHandler();
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
                MessageBox.Show(UiLanguage.T($"Impossible d’ouvrir la page d’aide : {ex.Message}", $"Unable to open the help page: {ex.Message}"), "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnLoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.ContextMenu == null)
            {
                e.Row.ContextMenu = CreateRowContextMenu();
            }

            // Le DataGrid recycle ses lignes. Préparer le menu avant son affichage
            // évite qu'un menu vide soit mesuré par Revit 2023 au clic droit.
            e.Row.ContextMenuOpening -= OnRowContextMenuOpening;
            e.Row.ContextMenuOpening += OnRowContextMenuOpening;
        }

        private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var row = sender as DataGridRow;
            if (row == null || row.ContextMenu == null)
            {
                return;
            }

            row.ContextMenu.DataContext = row.DataContext;
            row.ContextMenu.PlacementTarget = row;
        }

        private ContextMenu CreateRowContextMenu()
        {
            var contextMenu = new ContextMenu();
            var deleteMenuItem = new MenuItem
            {
                Header = UiLanguage.T("Supprimer du projet", "Delete from Project"),
                MinWidth = 190,
                MinHeight = 28,
                Padding = new Thickness(12, 5, 12, 5)
            };
            deleteMenuItem.Click += OnDeleteFamilyClick;

            contextMenu.Items.Add(deleteMenuItem);

            deleteMenuItem.SetBinding(FrameworkElement.DataContextProperty,
                new System.Windows.Data.Binding());

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

            FamilyTotalText.Text = UiLanguage.T($"Total Familles : {familyTotal:N2} Mo", $"Family Total: {familyTotal:N2} MB");
            ImportTotalText.Text = UiLanguage.T($"Total Imports (PDF/DWG/etc.) : {importTotal:N2} Mo", $"Import Total (PDF/DWG/etc.): {importTotal:N2} MB");
            GrandTotalText.Text = UiLanguage.T($"Total Général : {(familyTotal + importTotal):N2} Mo", $"Grand Total: {(familyTotal + importTotal):N2} MB");
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
                menuItem.DataContext is ElementInfo info)
            {
                var elementIds = info.IsFamily
                    ? new[] { info.PrimaryId }
                    : (info.ElementIds ?? new List<Autodesk.Revit.DB.ElementId>()).ToArray();

                if (elementIds.Length == 0 || elementIds.Any(id => id == null))
                {
                    MessageBox.Show(this,
                        UiLanguage.T("Impossible de déterminer l'identifiant de l'élément à supprimer.", "Unable to determine the ID of the element to delete."),
                        UiLanguage.T("Suppression impossible", "Cannot Delete"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var details = info.IsFamily
                    ? UiLanguage.T("Toutes ses instances seront également supprimées.", "All its instances will also be deleted.")
                    : elementIds.Length > 1
                        ? UiLanguage.T($"Les {elementIds.Length} éléments correspondants seront supprimés.", $"The {elementIds.Length} corresponding elements will be deleted.")
                        : UiLanguage.T("L'élément sera supprimé du projet.", "The element will be deleted from the project.");
                var confirmation = MessageBox.Show(this,
                    UiLanguage.T($"Supprimer définitivement \"{info.Nom}\" du projet ?\n{details}", $"Permanently delete \"{info.Nom}\" from the project?\n{details}"),
                    UiLanguage.T("Supprimer l'élément", "Delete Element"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);

                if (confirmation != MessageBoxResult.Yes)
                {
                    return;
                }

                _deleteHandler.ElementIds = elementIds;
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
