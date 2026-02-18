using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Microsoft.Win32;

namespace Modification
{
    public partial class ReservationAutoV3Window : Window
    {
        public enum HostTarget { Mur, Sol }
        public enum ShapeTarget { Rectangulaire, Circulaire }
        public enum ObjectType { Canalisation, Gaine, Porte, Fenetre, Autre }
        public enum PipeSource { Maquette, LienIFC, LienRVT }

        public HostTarget SelectedHost { get; private set; }
        public ShapeTarget SelectedShape { get; private set; }
        public ObjectType SelectedObject { get; private set; }
        public PipeSource SelectedPipeSource { get; private set; }
        public bool AutomatiqueEnabled { get; private set; }
        public bool MultiEnabled { get; private set; }
        public bool NormeEnabled { get; private set; }
        public bool DynamoAutoEnabled { get; private set; }

        public ReservationAutoV3Config Config { get; private set; }

        private readonly Document _doc;

        private List<LoadedTypeItem> _loadedTypes = new List<LoadedTypeItem>();

        public class LoadedTypeItem
        {
            public FamilySymbol Symbol { get; }
            public string Display { get; }
            public LoadedTypeItem(FamilySymbol s)
            {
                Symbol = s;
                Display = $"{s.Family.Name} — {s.Name}";
            }
        }

        public ReservationAutoV3Window(Document doc, ReservationAutoV3Config cfg)
        {
            InitializeComponent();

            _doc = doc;
            Config = cfg ?? new ReservationAutoV3Config();

            comboHost.SelectedIndex = 0;
            comboShape.SelectedIndex = 0;
            comboObjectType.SelectedIndex = 0;
            comboPipeSource.SelectedIndex = 0;

            cbTargetProfile.SelectedIndex = 0;

            // Defaults UI
            chkDefaultNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDefaultDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;
            tbOversize.Text = Config.OversizeMm_PipeDuct.ToString("0");
            tbDynamoPath.Text = Config.DynamoPath ?? "";

            chkNorme.IsChecked = Config.DefaultNormeEnabled;
            chkDynamo.IsChecked = Config.DefaultDynamoAutoEnabled;

            tbRfaPath.Text = Config.LastRfaPath ?? "";

            RefreshProfilesSummary();
            UpdateMappingPanels();
            OnCriteriaChanged(null, null);
        }

        private void RefreshProfilesSummary()
        {
            txtWallRect.Text = DescribeProfile(Config.WallRect, "Longueur/Hauteur/Profondeur");
            txtWallCirc.Text = DescribeProfile(Config.WallCirc, "Diamètre/Profondeur");
            txtFloorRect.Text = DescribeProfile(Config.FloorRect, "Longueur/Largeur/Profondeur");
            txtFloorCirc.Text = DescribeProfile(Config.FloorCirc, "Diamètre/Profondeur");
        }

        private string DescribeProfile(ProfileConfig p, string expected)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.FamilyName))
                return $"(Non configuré) — attendu : {expected}";

            string type = string.IsNullOrWhiteSpace(p.TypeName) ? "(type auto)" : p.TypeName;
            return $"{p.FamilyName} — {type}";
        }

        public void OnCriteriaChanged(object sender, SelectionChangedEventArgs e)
        {
            string obj = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";
            bool isCanal = obj == "Canalisation";
            comboPipeSource.IsEnabled = isCanal;

            string shape = (comboShape.SelectedItem as ComboBoxItem)?.Content as string ?? "Rectangulaire";
            bool isRect = shape == "Rectangulaire";

            chkMulti.IsEnabled = isCanal && isRect;
            if (!chkMulti.IsEnabled) chkMulti.IsChecked = false;
        }

        private void OnBrowseRfa(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Famille Revit (*.rfa)|*.rfa",
                Title = "Sélectionner une famille de réservation (.rfa)"
            };

            if (!string.IsNullOrWhiteSpace(tbRfaPath.Text))
            {
                try
                {
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(tbRfaPath.Text);
                }
                catch { }
            }

            if (dlg.ShowDialog() == true)
            {
                tbRfaPath.Text = dlg.FileName;
                Config.LastRfaPath = dlg.FileName;
            }
        }

        private void OnLoadRfa(object sender, RoutedEventArgs e)
        {
            string path = tbRfaPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("Sélectionne un fichier .RFA valide.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Charge la famille dans le projet sans popup
                using (var t = new Transaction(_doc, "Charger famille réservation (V3)"))
                {
                    t.Start();

                    if (!_doc.LoadFamily(path, new NoPromptFamilyLoadOptions(), out var fam))
                    {
                        t.RollBack();
                        MessageBox.Show("Impossible de charger la famille (LoadFamily a échoué).", "BIMaestro",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    t.Commit();

                    // Récupère tous les types de cette famille
                    _loadedTypes = GetSymbolsFromFamily(_doc, fam)
                        .Select(s => new LoadedTypeItem(s))
                        .ToList();

                    cbLoadedType.ItemsSource = _loadedTypes;
                    cbLoadedType.SelectedIndex = _loadedTypes.Any() ? 0 : -1;

                    MessageBox.Show("Famille chargée ✅\nChoisis ensuite le profil (mur/sol + forme) et mappe les paramètres.",
                        "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur chargement famille : " + ex.Message, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static List<FamilySymbol> GetSymbolsFromFamily(Document doc, Family fam)
        {
            var list = new List<FamilySymbol>();
            if (doc == null || fam == null) return list;

            foreach (var id in fam.GetFamilySymbolIds())
            {
                if (doc.GetElement(id) is FamilySymbol fs)
                    list.Add(fs);
            }
            return list;
        }

        private void OnTargetProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMappingPanels();
        }

        private void OnLoadedTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            FillParamCombosFromSelectedSymbol();
        }

        private void UpdateMappingPanels()
        {
            panelMapRectWall.Visibility = System.Windows.Visibility.Collapsed;
            panelMapCircWall.Visibility = System.Windows.Visibility.Collapsed;
            panelMapRectFloor.Visibility = System.Windows.Visibility.Collapsed;
            panelMapCircFloor.Visibility = System.Windows.Visibility.Collapsed;


            int idx = cbTargetProfile.SelectedIndex;
            switch (idx)
            {
                case 0: panelMapRectWall.Visibility = System.Windows.Visibility.Visible; break;  // Mur Rect
                case 1: panelMapCircWall.Visibility = System.Windows.Visibility.Visible; break;  // Mur Circ
                case 2: panelMapRectFloor.Visibility = System.Windows.Visibility.Visible; break; // Sol Rect
                case 3: panelMapCircFloor.Visibility = System.Windows.Visibility.Visible; break; // Sol Circ
            }

            FillParamCombosFromSelectedSymbol();
        }

        private void FillParamCombosFromSelectedSymbol()
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;
            if (sym == null) return;

            var names = sym.Parameters
                .Cast<Parameter>()
                .Select(p => p.Definition?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            void fill(ComboBox cb)
            {
                if (cb == null) return;
                cb.ItemsSource = names;
            }

            fill(cbMapWallLen);
            fill(cbMapWallHeight);
            fill(cbMapWallDepth);
            fill(cbMapWallDiam);
            fill(cbMapWallDepth2);

            fill(cbMapFloorLen);
            fill(cbMapFloorWidth);
            fill(cbMapFloorDepth);
            fill(cbMapFloorDiam);
            fill(cbMapFloorDepth2);
        }

        private void OnApplyMapping(object sender, RoutedEventArgs e)
        {
            var it = cbLoadedType.SelectedItem as LoadedTypeItem;
            var sym = it?.Symbol;

            if (sym == null)
            {
                MessageBox.Show("Charge une famille (.RFA) et choisis un type.", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Mise à jour des options globales
            if (!double.TryParse(tbOversize.Text?.Trim(), out var ov)) ov = 50.0;
            Config.OversizeMm_PipeDuct = Math.Max(0.0, ov);
            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            int idx = cbTargetProfile.SelectedIndex;
            ProfileConfig p = idx switch
            {
                0 => Config.WallRect,
                1 => Config.WallCirc,
                2 => Config.FloorRect,
                3 => Config.FloorCirc,
                _ => Config.WallRect
            };

            p.FamilyName = sym.Family.Name;
            p.TypeName = sym.Name;

            // Mapping selon profil
            if (idx == 0)
            {
                p.ParamLength = cbMapWallLen?.Text ?? "";
                p.ParamHeight = cbMapWallHeight?.Text ?? "";
                p.ParamDepth = cbMapWallDepth?.Text ?? "";
            }
            else if (idx == 1)
            {
                p.ParamDiameter = cbMapWallDiam?.Text ?? "";
                p.ParamDepth = cbMapWallDepth2?.Text ?? "";
            }
            else if (idx == 2)
            {
                p.ParamLength = cbMapFloorLen?.Text ?? "";
                p.ParamWidth = cbMapFloorWidth?.Text ?? "";
                p.ParamDepth = cbMapFloorDepth?.Text ?? "";
            }
            else if (idx == 3)
            {
                p.ParamDiameter = cbMapFloorDiam?.Text ?? "";
                p.ParamDepth = cbMapFloorDepth2?.Text ?? "";
            }

            if (ReservationAutoV3ConfigStore.Save(Config, out var err))
            {
                RefreshProfilesSummary();
                MessageBox.Show("Profil configuré + sauvegardé ✅", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSaveOnly(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(tbOversize.Text?.Trim(), out var ov)) ov = 50.0;
            Config.OversizeMm_PipeDuct = Math.Max(0.0, ov);

            Config.DynamoPath = tbDynamoPath.Text ?? "";
            Config.DefaultNormeEnabled = chkDefaultNorme.IsChecked == true;
            Config.DefaultDynamoAutoEnabled = chkDefaultDynamo.IsChecked == true;

            if (ReservationAutoV3ConfigStore.Save(Config, out var err))
                MessageBox.Show("Configuration sauvegardée ✅", "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("Erreur sauvegarde : " + err, "BIMaestro", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            SelectedHost = ((comboHost.SelectedItem as ComboBoxItem)?.Content as string) == "Sol"
                ? HostTarget.Sol
                : HostTarget.Mur;

            SelectedShape = ((comboShape.SelectedItem as ComboBoxItem)?.Content as string) == "Circulaire"
                ? ShapeTarget.Circulaire
                : ShapeTarget.Rectangulaire;

            var obj = (comboObjectType.SelectedItem as ComboBoxItem)?.Content as string ?? "Canalisation";
            SelectedObject = obj switch
            {
                "Canalisation" => ObjectType.Canalisation,
                "Gaine" => ObjectType.Gaine,
                "Porte" => ObjectType.Porte,
                "Fenêtre" => ObjectType.Fenetre,
                _ => ObjectType.Autre
            };

            var src = (comboPipeSource.SelectedItem as ComboBoxItem)?.Content as string ?? "Maquette";
            SelectedPipeSource = src switch
            {
                "Lien IFC" => PipeSource.LienIFC,
                "Lien RVT" => PipeSource.LienRVT,
                _ => PipeSource.Maquette
            };

            AutomatiqueEnabled = chkAutomatique.IsChecked == true;
            MultiEnabled = chkMulti.IsChecked == true;

            NormeEnabled = chkNorme.IsChecked == true;
            DynamoAutoEnabled = chkDynamo.IsChecked == true;

            // persist defaults if user changed them in exec
            Config.DefaultNormeEnabled = NormeEnabled;
            Config.DefaultDynamoAutoEnabled = DynamoAutoEnabled;
            ReservationAutoV3ConfigStore.Save(Config, out _);

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private class NoPromptFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                // Important : pas de popup. On ne force pas l’écrasement des paramètres.
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                // Idem : pas de popup, pas d'écrasement.
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}
