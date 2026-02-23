using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;

namespace Analyse
{
    public partial class PipeNetworkInteractionWindow : Window
    {
        private readonly Action<HashSet<ElementId>> _selectInRevit;
        public ObservableCollection<PipeNetworkDisplayItem> Networks { get; }

        public PipeNetworkInteractionWindow(IEnumerable<PipeNetworkDisplayItem> networks, Action<HashSet<ElementId>> selectInRevit)
        {
            ThemeManager.EnsureThemeLoaded();
            InitializeComponent();
            _selectInRevit = selectInRevit;
            Networks = new ObservableCollection<PipeNetworkDisplayItem>(networks);
            DataContext = this;
        }

        private void NetworksList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (NetworksList.SelectedItem is PipeNetworkDisplayItem item)
            {
                _selectInRevit?.Invoke(item.ElementIds);
            }
        }
    }
}