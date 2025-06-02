using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ShortcutManager
{
    [Transaction(TransactionMode.Manual)]
    public class LoadShortcutsCommand : IExternalCommand
    {
        // Dossier contenant tes fichiers XML.
        private readonly string shortcutsFolder = @"P:\0-Boîte à outils Revit\09-Raccourcis Clavier Revit";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // 1) Vérifier l'existence du dossier
                if (!Directory.Exists(shortcutsFolder))
                {
                    TaskDialog.Show("Gestion Raccourcis",
                        $"Le dossier spécifié n'existe pas : {shortcutsFolder}");
                    return Result.Failed;
                }

                // 2) Récupérer les fichiers .xml
                string[] xmlFiles = Directory.GetFiles(shortcutsFolder, "*.xml");
                if (xmlFiles.Length == 0)
                {
                    TaskDialog.Show("Gestion Raccourcis",
                        "Aucun fichier XML trouvé dans le dossier spécifié.");
                    return Result.Failed;
                }

                // 3) Afficher une fenêtre WPF pour choisir quel fichier copier
                var picker = new SelectShortcutWindow(xmlFiles);
                bool? dialogResult = picker.ShowDialog();

                // Si l’utilisateur annule ou ferme la fenêtre, on ne fait rien
                if (dialogResult != true)
                    return Result.Cancelled;

                // 4) Récupérer le fichier choisi
                string chosenXml = picker.SelectedFileFullPath;
                if (string.IsNullOrEmpty(chosenXml) || !File.Exists(chosenXml))
                {
                    TaskDialog.Show("Gestion Raccourcis",
                        "Le fichier sélectionné est introuvable ou invalide.");
                    return Result.Failed;
                }

                // 5) Construire le chemin complet de KeyboardShortcuts.xml pour la version Revit en cours
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string versionName = commandData.Application.Application.VersionName;
                string revitFolderName = GetRevitVersionFolder(versionName);

                string targetKeyboardShortcuts = Path.Combine(
                    appData, "Autodesk", "Revit", revitFolderName, "KeyboardShortcuts.xml");

                // Vérifier le dossier parent
                string parentDir = Path.GetDirectoryName(targetKeyboardShortcuts);
                if (!Directory.Exists(parentDir))
                {
                    TaskDialog.Show("Gestion Raccourcis",
                        $"Le dossier {parentDir} n'existe pas. Impossible de copier le fichier.");
                    return Result.Failed;
                }

                // 6) Copier le fichier sélectionné pour remplacer KeyboardShortcuts.xml
                File.Copy(chosenXml, targetKeyboardShortcuts, overwrite: true);

                // 7) Tenter de forcer la prise en compte immédiate
                // => On ouvre la boîte de dialogue "Clavier" via PostCommand
                // Dans certaines versions, aller dans cette boîte relit le XML.
                try
                {
                    var kid = RevitCommandId.LookupPostableCommandId(PostableCommand.KeyboardShortcuts);
                    commandData.Application.PostCommand(kid);
                }
                catch
                {
                    // S’il y a un souci, on ignore
                }

                // 8) Message final
                TaskDialog.Show("Gestion Raccourcis",
                    "Le fichier a été copié avec succès.\n" +
                    "Revit va peut-être relire les changements immédiatement.\n" +
                    "Dans le doute, ouvre la fenêtre de configuration des raccourcis ou redémarre Revit.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Gère la chaîne renvoyée par Revit (VersionName).
        /// Exemples : "Autodesk Revit 2023", "2023.1", etc.
        /// Évite de doubler "Autodesk Revit" si c’est déjà présent.
        /// </summary>
        private string GetRevitVersionFolder(string versionName)
        {
            if (versionName.Contains("Autodesk Revit"))
            {
                // Supprimer la partie .1 si elle existe
                string major = versionName.Split('.')[0];
                return major.Trim(); // ex. "Autodesk Revit 2023"
            }
            else
            {
                // Suppose qu’on a juste "2023" ou "2023.1"
                string major = versionName.Split('.')[0];
                return $"Autodesk Revit {major}";
            }
        }
    }


    /// <summary>
    /// Fenêtre WPF minimaliste qui liste tous les fichiers .xml passés au constructeur.
    /// L’utilisateur en sélectionne un, puis clique « Appliquer » ou « Annuler ».
    /// </summary>
    public class SelectShortcutWindow : Window
    {
        private ListBox _listBox;
        private Button _btnApply, _btnCancel;

        // Liste complète des fichiers .xml trouvés
        private readonly string[] _xmlFiles;

        // Le fichier complet que l’utilisateur a choisi
        public string SelectedFileFullPath { get; private set; }

        public SelectShortcutWindow(string[] xmlFiles)
        {
            _xmlFiles = xmlFiles;

            Title = "Sélection d’un raccourci XML";
            Width = 420;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Création de la grille
            System.Windows.Controls.Grid mainGrid = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = mainGrid;

            // Un label au-dessus
            TextBlock tb = new TextBlock
            {
                Text = "Choisissez un fichier de raccourcis à appliquer :",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.Grid.SetRow(tb, 0);
            mainGrid.Children.Add(tb);

            // Une ListBox pour lister les fichiers
            _listBox = new ListBox();
            System.Windows.Controls.Grid.SetRow(_listBox, 1);
            mainGrid.Children.Add(_listBox);

            // On y ajoute tous les noms de fichiers
            foreach (var file in xmlFiles)
            {
                _listBox.Items.Add(Path.GetFileName(file));
            }
            if (_listBox.Items.Count > 0)
                _listBox.SelectedIndex = 0;

            // Un StackPanel en bas pour les boutons Appliquer/Annuler
            StackPanel sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            System.Windows.Controls.Grid.SetRow(sp, 2);
            mainGrid.Children.Add(sp);

            _btnApply = new Button
            {
                Content = "Appliquer",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0)
            };
            _btnApply.Click += OnApply;
            sp.Children.Add(_btnApply);

            _btnCancel = new Button
            {
                Content = "Annuler",
                Width = 80
            };
            _btnCancel.Click += OnCancel;
            sp.Children.Add(_btnCancel);
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            if (_listBox.SelectedItem == null)
            {
                MessageBox.Show("Veuillez sélectionner un fichier XML.", "Attention",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fileName = _listBox.SelectedItem.ToString(); // ex. "MonRaccourciPerso.xml"
            // Retrouver son chemin complet
            string fullPath = _xmlFiles.FirstOrDefault(x =>
                Path.GetFileName(x).Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(fullPath))
            {
                MessageBox.Show("Fichier introuvable.", "Erreur",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedFileFullPath = fullPath;
            DialogResult = true; // ferme la fenêtre en indiquant OK
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false; // ferme la fenêtre, annulation
        }
    }
}
