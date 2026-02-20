using System;
using System.Linq;
using System.Windows;

namespace Modification
{
    internal static class ThemeManager
    {
        private static bool _loaded;

        /// <summary>
        /// Charge le thème BIMaestro dans Application.Current.Resources (une seule fois).
        /// À appeler avant d'afficher une fenêtre WPF.
        /// </summary>
        public static void EnsureThemeLoaded()
        {
            if (_loaded) return;

            // Revit héberge l'app WPF (Application.Current existe normalement)
            if (Application.Current == null)
                return;

            // Si déjà mergé, on ne fait rien
            bool alreadyMerged = Application.Current.Resources.MergedDictionaries
                .Any(d => d.Source != null && d.Source.OriginalString.Contains("BIMaestroTheme.xaml"));

            if (alreadyMerged)
            {
                _loaded = true;
                return;
            }

            try
            {
                // Pack URI vers ton ResourceDictionary
                // Remplace YOUR_ASSEMBLY_NAME par le nom exact de l'assembly où se trouve Themes/BIMaestroTheme.xaml
                var uri = new Uri("pack://application:,,,Themes/BIMaestroTheme.xaml", UriKind.Absolute);

                var dict = new ResourceDictionary { Source = uri };
                Application.Current.Resources.MergedDictionaries.Add(dict);

                _loaded = true;
            }
            catch
            {
                // En dernier recours, on marque comme chargé pour éviter de spammer
                // mais idéalement tu loggues l'erreur dans ton système de logs.
                _loaded = true;
            }
        }
    }
}