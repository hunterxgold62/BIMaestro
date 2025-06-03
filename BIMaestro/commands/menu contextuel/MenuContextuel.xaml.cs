// MenuControl.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Threading.Tasks;

namespace TonNamespace
{
    public class MenuButtonCommand
    {
        public string Label { get; set; }
        public Func<Task> CommandAction { get; set; }
    }

    public partial class MenuControl : UserControl
    {
        private UIApplication _uiApp;
        public event Action RequestClose;

        // Toutes les commandes Revit disponibles
        private static readonly Dictionary<string, PostableCommand> AvailableCommands = new Dictionary<string, PostableCommand>()
        {
            { "Déplacer",    PostableCommand.Move },
            { "Copier",      PostableCommand.Copy },
            { "Rotation",    PostableCommand.Rotate },
            { "Aligner",     PostableCommand.Align },
            { "Décaler",     PostableCommand.Offset },
            { "Miroir",      PostableCommand.MirrorDrawAxis },
            { "Array",       PostableCommand.Array },
            { "Tronquer",    PostableCommand.TrimOrExtendToCorner },
            { "Allonger",    PostableCommand.TrimOrExtendMultipleElements },
            { "Joindre",     PostableCommand.JoinGeometry },
            { "Séparer",     PostableCommand.UnjoinGeometry },
            { "Épingler",    PostableCommand.Pin },
            { "Désépingler", PostableCommand.Unpin },
            { "Supprimer",   PostableCommand.Delete },
            { "Coter",       PostableCommand.AlignedDimension },
            { "Grouper",     PostableCommand.CreateGroup },
            { "Masquer",     PostableCommand.HideElements },
            { "Afficher",    PostableCommand.HideBoundaryOpenEnds }
        };

        // Glyphes Unicode pour chaque commande
        private static readonly Dictionary<string, string> IconGlyphMap = new Dictionary<string, string>()
        {
            { "Déplacer",    "↔" },
            { "Copier",      "⎘" },
            { "Rotation",    "⟳" },
            { "Aligner",     "↱" },
            { "Décaler",     "⇥" },
            { "Miroir",      "⇋" },
            { "Array",       "⋯" },
            { "Tronquer",    "⊥" },
            { "Allonger",    "⇪" },
            { "Joindre",     "∪" },
            { "Séparer",     "∖" },
            { "Épingler",    "📌" },
            { "Désépingler", "📍" },
            { "Supprimer",   "✕" },
            { "Coter",       "↔|" },
            { "Grouper",     "⑈" },
            { "Masquer",     "🙈" },
            { "Afficher",    "👁" }
        };

        private Dictionary<string, ExternalEvent> _externalEventMap;
        private List<string> _configuredCommands;
        private string ConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs", "SauvegardePréférence", "Menu contexte.json");

        // Pour drag & drop
        private Button _draggedBtn;

        public MenuControl(UIApplication uiApp)
        {
            InitializeComponent();
            _uiApp = uiApp;

            // Créer un ExternalEvent pour chaque commande
            _externalEventMap = new Dictionary<string, ExternalEvent>();
            foreach (var kvp in AvailableCommands)
                _externalEventMap[kvp.Key] = ExternalEvent.Create(new ExternalEventHandlerGeneric(kvp.Value));

            LoadConfiguration();
            BuildCommandButtons();
        }

        private void LoadConfiguration()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    _configuredCommands = JsonSerializer.Deserialize<List<string>>(json);
                }
                else
                {
                    _configuredCommands = new List<string> { "Déplacer", "Copier", "Rotation", "Aligner", "Décaler" };
                    SaveConfigurationDelayed();
                }
            }
            catch
            {
                _configuredCommands = new List<string> { "Déplacer", "Copier", "Rotation", "Aligner", "Décaler" };
            }
        }

        private void SaveConfigurationDelayed()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(_configuredCommands);
                    File.WriteAllText(ConfigFilePath, json);
                }
                catch { }
            }), DispatcherPriority.Background);
        }

        private void BuildCommandButtons()
        {
            buttonContainer.Children.Clear();

            foreach (var label in _configuredCommands)
            {
                if (!AvailableCommands.TryGetValue(label, out var cmd)) continue;

                var externalEvent = _externalEventMap[label];
                var menuCmd = new MenuButtonCommand
                {
                    Label = label,
                    CommandAction = () =>
                    {
                        externalEvent.Raise();
                        return Task.CompletedTask;
                    }
                };

                // Contenu : glyphe + texte
                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                if (IconGlyphMap.TryGetValue(label, out var glyph))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = glyph,
                        FontSize = 16,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                stack.Children.Add(new TextBlock
                {
                    Text = menuCmd.Label,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var btn = new Button
                {
                    Content = stack,
                    Style = (Style)FindResource("MenuButtonStyle"),
                    Tag = menuCmd.Label,
                    AllowDrop = true
                };

                // Clic : fermer le popup et exécuter
                btn.Click += async (s, e) =>
                {
                    RequestClose?.Invoke();
                    await menuCmd.CommandAction();
                };

                // Menu contextuel « Retirer »
                var ctx = new ContextMenu();
                var mi = new MenuItem { Header = "Retirer" };
                mi.Click += (s, e) =>
                {
                    _configuredCommands.Remove(menuCmd.Label);
                    SaveConfigurationDelayed();
                    BuildCommandButtons();
                };
                ctx.Items.Add(mi);
                btn.ContextMenu = ctx;

                // Drag & drop
                btn.PreviewMouseLeftButtonDown += (s, e) => _draggedBtn = btn;
                btn.MouseMove += Btn_MouseMove;
                btn.Drop += Btn_Drop;

                buttonContainer.Children.Add(btn);
            }
        }

        private void Btn_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedBtn != null)
            {
                DragDrop.DoDragDrop(_draggedBtn, _draggedBtn.Tag, DragDropEffects.Move);
            }
        }

        private void Btn_Drop(object sender, DragEventArgs e)
        {
            if (!(sender is Button target) || _draggedBtn == null) return;
            var from = _draggedBtn.Tag as string;
            var to = target.Tag as string;
            if (from == to) return;

            int oldIdx = _configuredCommands.IndexOf(from);
            int newIdx = _configuredCommands.IndexOf(to);
            _configuredCommands.RemoveAt(oldIdx);
            _configuredCommands.Insert(newIdx, from);

            SaveConfigurationDelayed();
            BuildCommandButtons();
        }

        private void MenuControl_Loaded(object sender, RoutedEventArgs e)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            this.BeginAnimation(UserControl.OpacityProperty, fadeIn);
        }

        private void btnAddCommand_Click(object sender, RoutedEventArgs e)
        {
            // Fermer les menus contextuels ouverts
            foreach (var child in buttonContainer.Children)
                if (child is Button b && b.ContextMenu?.IsOpen == true)
                    b.ContextMenu.IsOpen = false;

            RequestClose?.Invoke();

            var available = new List<string>();
            foreach (var kvp in AvailableCommands)
                if (!_configuredCommands.Contains(kvp.Key))
                    available.Add(kvp.Key);

            if (available.Count == 0)
            {
                MessageBox.Show("Toutes les commandes disponibles ont été ajoutées.");
                return;
            }

            var win = new CommandSelectionWindow(available);
            var helper = new System.Windows.Interop.WindowInteropHelper(win)
            { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.SelectedCommand))
            {
                _configuredCommands.Add(win.SelectedCommand);
                SaveConfigurationDelayed();
                BuildCommandButtons();
            }
        }
    }
}
