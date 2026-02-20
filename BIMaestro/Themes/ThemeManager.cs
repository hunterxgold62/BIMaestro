using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;



    internal static class ThemeManager
    {
        private const string ThemeDictionaryPath = "/BIMaestro;component/Themes/BIMaestroTheme.xaml";
        private static readonly object SyncRoot = new object();
        private static bool _loaded;

        /// <summary>
        /// Charge le thème BIMaestro dans Application.Current.Resources (une seule fois).
        /// À appeler avant d'afficher une fenêtre WPF.
        /// </summary>
        public static void EnsureThemeLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (_loaded)
                {
                    return;
                }

                if (Application.Current == null)
                {
                    return;
                }

                bool alreadyMerged = Application.Current.Resources.MergedDictionaries
                    .Any(d => d.Source != null
                              && d.Source.OriginalString.IndexOf("BIMaestroTheme.xaml", StringComparison.OrdinalIgnoreCase) >= 0);

                if (alreadyMerged)
                {
                    _loaded = true;
                    return;
                }

                try
                {
                    var uri = new Uri(ThemeDictionaryPath, UriKind.Relative);
                    var dictionary = new ResourceDictionary { Source = uri };
                    Application.Current.Resources.MergedDictionaries.Add(dictionary);

                    _loaded = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ThemeManager] Impossible de charger le thème '{ThemeDictionaryPath}': {ex}");
                }
            }
        }
    }
