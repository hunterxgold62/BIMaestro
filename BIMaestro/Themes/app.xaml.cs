using System;
using System.Windows;

namespace BIMaestro
{
    public partial class App : Application
    {
        public static string CurrentTheme { get; private set; } = "Light";

        public static void ApplyTheme(string theme)
        {
            CurrentTheme = theme;
            var dictionaries = Current.Resources.MergedDictionaries;

            // Retire les anciens thèmes
            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                var src = dictionaries[i].Source?.OriginalString;
                if (src != null && (src.Contains("Theme.Light.xaml") || src.Contains("Theme.Dark.xaml")))
                {
                    dictionaries.RemoveAt(i);
                }
            }

            // Ajoute le nouveau thème
            var themeDict = new ResourceDictionary { Source = new Uri($"/BIMaestro;component/Themes/Theme.{theme}.xaml", UriKind.Relative) };
            dictionaries.Add(themeDict);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplyTheme(CurrentTheme);
        }
    }
}