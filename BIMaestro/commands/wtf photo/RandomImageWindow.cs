using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;

namespace RandomImageAddin
{
    // Classe pour gérer l'enregistrement d'une hotkey globale via une fenêtre cachée.
    public static class HotKeyManager
    {
        // ID de la hotkey
        private const int HOTKEY_ID = 0x1000;
        // Valeurs de modificateurs : aucun
        private const uint MOD_NONE = 0x0000;
        // Code virtuel pour Escape
        private const uint VK_ESCAPE = 0x1B;

        private static HwndSource _source;

        // Enregistre la hotkey Escape sur une fenêtre cachée.
        public static void RegisterHotKey(Action callback)
        {
            // Création d'une fenêtre invisible pour recevoir les messages
            HwndSourceParameters parameters = new HwndSourceParameters("HotKeyHook");
            parameters.WindowStyle = 0x800000; // WS_POPUP
            parameters.Width = 0;
            parameters.Height = 0;
            _source = new HwndSource(parameters);
            _source.AddHook(HwndHook);

            // Enregistrement de la hotkey : Escape sans modificateur
            if (!RegisterHotKey(_source.Handle, HOTKEY_ID, MOD_NONE, VK_ESCAPE))
            {
                MessageBox.Show("Impossible d'enregistrer le raccourci clavier.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Stocke le callback dans le tag de la source pour y accéder dans le hook
            _source.RootVisual?.SetValue(CallbackProperty, callback);
            // Sinon, on peut stocker le callback dans une variable statique si besoin.
            _callback = callback;
        }

        // Désenregistre la hotkey et détruit la fenêtre cachée.
        public static void UnregisterHotKey()
        {
            if (_source != null)
            {
                UnregisterHotKey(_source.Handle, HOTKEY_ID);
                _source.RemoveHook(HwndHook);
                _source.Dispose();
                _source = null;
            }
        }

        // Propriété de dépendance pour stocker le callback (optionnel)
        public static readonly DependencyProperty CallbackProperty =
            DependencyProperty.RegisterAttached("Callback", typeof(Action), typeof(HotKeyManager), new PropertyMetadata(null));

        private static Action _callback;

        // La fonction callback du hook pour traiter les messages Windows.
        private static IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    // Appel du callback pour stopper les pop-ups
                    _callback?.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // Importation des fonctions natives
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    // Classe représentant une pop-up affichant une image.
    // La fenêtre s'adapte à la taille de l'image et se positionne aléatoirement.
    public class PopUpImageWindow : Window
    {
        public PopUpImageWindow(string imagePath)
        {
            try
            {
                // Chargement de l'image
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                // Création du contrôle Image sans redimensionnement
                Image imgControl = new Image
                {
                    Source = bitmap,
                    Stretch = System.Windows.Media.Stretch.None
                };

                // Affectation du contenu de la fenêtre
                this.Content = imgControl;

                // Dimensionnement de la fenêtre à la taille de l'image
                this.Width = bitmap.PixelWidth;
                this.Height = bitmap.PixelHeight;

                // Paramétrage de la fenêtre
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true;
                this.WindowStartupLocation = WindowStartupLocation.Manual;

                // Positionnement aléatoire dans la zone de travail
                Random rnd = new Random();
                Rect workArea = SystemParameters.WorkArea;
                this.Left = workArea.Left + rnd.NextDouble() * Math.Max(0, workArea.Width - this.Width);
                this.Top = workArea.Top + rnd.NextDouble() * Math.Max(0, workArea.Height - this.Height);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la création de la pop-up : {ex.Message}",
                                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Commande qui lance le timer et fait apparaître les pop-ups de type "virus"
    [Transaction(TransactionMode.Manual)]
    public class ShowVirusPopupsCommand : IExternalCommand
    {
        // Timer et liste des pop-ups pour pouvoir les fermer ensuite
        private static DispatcherTimer _popupTimer;
        private static List<string> _imagePaths;
        private static Random _random = new Random();
        private static List<Window> _openPopups = new List<Window>();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string folderPath = @"C:\Users\plemert\Downloads\Fond d'écran";

            if (!Directory.Exists(folderPath))
            {
                message = "Le dossier spécifié n'existe pas : " + folderPath;
                return Result.Failed;
            }

            // Récupère les fichiers .jpg et .png du dossier
            _imagePaths = Directory.GetFiles(folderPath, "*.jpg").ToList();
            _imagePaths.AddRange(Directory.GetFiles(folderPath, "*.png"));

            if (_imagePaths.Count == 0)
            {
                message = "Aucune image trouvée dans le dossier.";
                return Result.Failed;
            }

            // Démarre le timer pour créer une pop-up toutes les 0.5 secondes
            _popupTimer = new DispatcherTimer();
            _popupTimer.Interval = TimeSpan.FromSeconds(0.1);
            _popupTimer.Tick += PopupTimer_Tick;
            _popupTimer.Start();

            // Enregistre la hotkey globale Escape pour stopper les pop-ups
            HotKeyManager.RegisterHotKey(StopPopups);

            return Result.Succeeded;
        }

        private void PopupTimer_Tick(object sender, EventArgs e)
        {
            // Sélection aléatoire d'une image
            int index = _random.Next(_imagePaths.Count);
            string imagePath = _imagePaths[index];

            // Création et affichage de la pop-up
            PopUpImageWindow popup = new PopUpImageWindow(imagePath);
            _openPopups.Add(popup);
            popup.Show();
        }

        // Méthode statique permettant d'arrêter le timer et de fermer toutes les pop-ups ouvertes
        public static void StopPopups()
        {
            if (_popupTimer != null && _popupTimer.IsEnabled)
            {
                _popupTimer.Stop();
            }

            foreach (var popup in _openPopups)
            {
                if (popup.IsVisible)
                    popup.Close();
            }
            _openPopups.Clear();

            // Désenregistre le hotkey
            HotKeyManager.UnregisterHotKey();
        }
    }
}
