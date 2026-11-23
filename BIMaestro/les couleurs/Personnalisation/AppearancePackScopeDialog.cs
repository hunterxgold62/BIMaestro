using System.Windows;
using System.Windows.Controls;
using BIMaestro.Localization;

namespace Couleur
{
    internal sealed class AppearancePackScopeDialog : Window
    {
        private readonly CheckBox _ribbon;
        private readonly CheckBox _browser;
        private readonly CheckBox _properties;
        private readonly CheckBox _home;
        public AppearancePackScopeDialog(AppearancePack pack = null)
        {
            bool importing = pack != null;
            Title = importing ? UiLanguage.Get("AppearancePackScopeDialog.WhichSettingsDoYouWantToRestore") : UiLanguage.Get("AppearancePackScopeDialog.WhichSettingsDoYouWantToBackUp");
            Width = 500; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/" + typeof(AppearancePackScopeDialog).Assembly.GetName().Name + ";component/Themes/BIMaestroTheme.xaml", System.UriKind.Relative) });
            SetResourceReference(BackgroundProperty, "App.Background");
            var layout = new StackPanel { Margin = new Thickness(20) }; Content = layout;
            var title = new TextBlock { Text = importing ? UiLanguage.Get("AppearancePackScopeDialog.SelectThePackItemsToRestore") : UiLanguage.Get("AppearancePackScopeDialog.SelectWhatToIncludeInTheBackup"), TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(StyleProperty, "H2"); layout.Children.Add(title);
            _ribbon = AddChoice(layout, UiLanguage.Get("AppearancePackScopeDialog.BIMaestroPanels"), pack == null || pack.IncludesRibbon);
            _browser = AddChoice(layout, UiLanguage.Get("AppearancePackScopeDialog.ProjectBrowserAndIcons"), pack == null || pack.IncludesBrowser);
            _properties = AddChoice(layout, UiLanguage.Get("AppearancePackScopeDialog.PropertiesColorsProfilesOrderAndCustomGroups"), pack == null || pack.IncludesProperties);
            _home = AddChoice(layout, UiLanguage.Get("AppearancePackScopeDialog.RevitHomeLayoutAndJournalCatalog"), pack == null || pack.IncludesHome);
            var hint = new TextBlock { Text = importing ? UiLanguage.Get("AppearancePackScopeDialog.ItemsNotIncludedInThePackAreGrayed") : UiLanguage.Get("AppearancePackScopeDialog.YouCanSelectSeveralItemsTheFileAlso"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            hint.SetResourceReference(StyleProperty, "Hint"); layout.Children.Add(hint);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = UiLanguage.Get("AppearancePackScopeDialog.Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.SetResourceReference(StyleProperty, "SecondaryButton");
            var confirm = new Button { Content = importing ? UiLanguage.Get("AppearancePackScopeDialog.LoadSelection") : UiLanguage.Get("AppearancePackScopeDialog.BackUp"), IsDefault = true }; confirm.SetResourceReference(StyleProperty, "PrimaryButton");
            System.Action refresh = () => confirm.IsEnabled = IncludesRibbon || IncludesBrowser || IncludesProperties || IncludesHome;
            foreach (var choice in new[] { _ribbon, _browser, _properties, _home }) { choice.Checked += (_, __) => refresh(); choice.Unchecked += (_, __) => refresh(); }
            confirm.Click += (_, __) => DialogResult = true;
            refresh(); actions.Children.Add(cancel); actions.Children.Add(confirm); layout.Children.Add(actions);
        }
        public bool IncludesRibbon => _ribbon.IsChecked == true;
        public bool IncludesBrowser => _browser.IsChecked == true;
        public bool IncludesProperties => _properties.IsChecked == true;
        public bool IncludesHome => _home.IsChecked == true;
        private static CheckBox AddChoice(Panel parent, string text, bool available)
        {
            var choice = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = available, IsEnabled = available, Margin = new Thickness(0, 6, 0, 8), FontSize = 13 };
            choice.SetResourceReference(ForegroundProperty, "Text.Primary"); parent.Children.Add(choice); return choice;
        }
    }

}
