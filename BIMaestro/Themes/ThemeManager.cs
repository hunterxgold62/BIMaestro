using Famille;
using BIMaestro.Localization;
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
    /// Active l'injection du thème BIMaestro sur les fenêtres BIMaestro uniquement.
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

            try
            {
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnWindowLoaded));

                _loaded = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeManager] Impossible d'activer le chargement du thème '{ThemeDictionaryPath}': {ex}");
            }
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var wnd = sender as Window;
        if (wnd == null)
        {
            return;
        }

        // Ne pas toucher aux fenêtres hôtes Revit : on scope le thème à l'assembly BIMaestro.
        if (wnd.GetType().Assembly != typeof(ThemeManager).Assembly)
        {
            return;
        }

        bool alreadyMerged = wnd.Resources.MergedDictionaries
            .Any(d => d.Source != null
                      && d.Source.OriginalString.IndexOf("BIMaestroTheme.xaml", StringComparison.OrdinalIgnoreCase) >= 0);

        if (alreadyMerged)
        {
            UiLanguage.LocalizeWindow(wnd);
            return;
        }


        try
        {
            var uri = new Uri(ThemeDictionaryPath, UriKind.Relative);
            var dictionary = new ResourceDictionary { Source = uri };
            wnd.Resources.MergedDictionaries.Add(dictionary);
            UiLanguage.LocalizeWindow(wnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Impossible d'injecter le thème sur '{wnd.GetType().FullName}': {ex}");
        }
    }
}
