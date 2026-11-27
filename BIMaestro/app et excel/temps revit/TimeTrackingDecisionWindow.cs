using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMaestro.Localization;

namespace BIMaestro.Dashboard
{
    internal sealed class TimeTrackingDecisionWindow : Window
    {
        internal enum Decision { Later, Continue, NewProject }
        internal sealed class ProjectChoice
        {
            public string ProjectId { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public double Hours { get; set; }
            public override string ToString() => Name + " · " + Duration(Hours);
        }
        internal Decision Choice { get; private set; } = Decision.Later;
        internal string SelectedProjectId => (_projects.SelectedItem as ProjectChoice)?.ProjectId;
        private readonly ComboBox _projects = new ComboBox();
        private readonly TextBlock _recognized = Text("", 17, true), _oldPath = Text("", 12), _continueHint = Text("", 13), _existingTime = Text("", 23, true);
        private readonly double _pendingHours;
        internal TimeTrackingDecisionWindow(IReadOnlyList<ProjectChoice> projects, string openedPath, double pendingHours)
        {
            if (projects == null || projects.Count == 0) throw new ArgumentException("At least one project is required.", nameof(projects));
            _pendingHours = pendingHours;
            ThemeManager.EnsureThemeLoaded();
            Title = T("WindowTitle"); Width = 600; MinWidth = 500; SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80); ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BIMaestro;component/Themes/BIMaestroTheme.xaml", UriKind.Relative) });
            SetResourceReference(BackgroundProperty, "App.Background"); SetResourceReference(ForegroundProperty, "Text.Primary");
            var root = new StackPanel { Margin = new Thickness(24) };
            var brand = Text("BIMaestro · " + T("TimeTracking"), 12, true); brand.SetResourceReference(TextBlock.ForegroundProperty, "Brand"); root.Children.Add(brand);
            root.Children.Add(Text(T("Heading"), 24, true, 10));
            var explanation = Text(T("Explanation"), 13, false, 6); explanation.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); root.Children.Add(explanation);
            var facts = new StackPanel();
            facts.Children.Add(Text(T("RecognizedProject"), 12));
            _projects.ItemsSource = projects; _projects.Margin = new Thickness(0, 4, 0, 4);
            _projects.Visibility = projects.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            facts.Children.Add(_projects); facts.Children.Add(_recognized);
            var metrics = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
            var existing = new StackPanel(); existing.Children.Add(Text(T("ExistingTime"), 12)); existing.Children.Add(_existingTime); metrics.Children.Add(existing);
            var pending = new StackPanel(); pending.Children.Add(Text(T("TimeToAdd"), 12));
            var duration = Text(Duration(pendingHours), 23, true); duration.SetResourceReference(TextBlock.ForegroundProperty, "Brand"); pending.Children.Add(duration);
            Grid.SetColumn(pending, 1); metrics.Children.Add(pending); facts.Children.Add(metrics);
            var card = new Border { Child = facts, Padding = new Thickness(16), Margin = new Thickness(0, 18, 0, 4), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Divider"); root.Children.Add(card);
            root.Children.Add(ActionButton(T("Continue"), _continueHint, () => Complete(Decision.Continue), true));
            root.Children.Add(ActionButton(T("NewProject"), Text(UiLanguage.Format("TimeTracking.NewTotal", Duration(pendingHours)), 13), () => Complete(Decision.NewProject), false));
            var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            details.Children.Add(_oldPath); details.Children.Add(Text(T("OpenedFile"), 12, true, 10));
            var current = Text(openedPath, 12); current.ToolTip = openedPath; details.Children.Add(current);
            details.Children.Add(Text(T("LaterExplanation"), 12, false, 12));
            root.Children.Add(new Expander { Header = T("Details"), Content = details, IsExpanded = false, Margin = new Thickness(0, 16, 0, 0) });
            var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = true };
            var later = new Button { Content = T("Later"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(14, 8, 14, 8), MinHeight = 36, ToolTip = T("LaterExplanation") };
            later.SetResourceReference(StyleProperty, "SecondaryButton"); later.Click += (_, __) => Complete(Decision.Later);
            DockPanel.SetDock(later, Dock.Right); footer.Children.Add(later);
            var remembered = Text(T("Remembered"), 12); remembered.VerticalAlignment = VerticalAlignment.Center; remembered.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary"); footer.Children.Add(remembered); root.Children.Add(footer);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _projects.SelectionChanged += (_, __) => UpdateProject(); _projects.SelectedIndex = 0;
        }
        private void UpdateProject()
        {
            if (!(_projects.SelectedItem is ProjectChoice project)) return;
            _recognized.Text = project.Name; _existingTime.Text = Duration(project.Hours);
            _oldPath.Text = T("PreviousFile") + "\n" + project.Path; _oldPath.ToolTip = project.Path;
            _continueHint.Text = UiLanguage.Format("TimeTracking.CombinedTotal", Duration(project.Hours + _pendingHours));
        }
        private void Complete(Decision decision) { Choice = decision; DialogResult = decision != Decision.Later; }
        private static Button ActionButton(string title, TextBlock hint, Action choose, bool accent)
        {
            var content = new StackPanel(); var heading = Text(title, 15, true);
            if (accent) heading.SetResourceReference(TextBlock.ForegroundProperty, "Brand");
            content.Children.Add(heading); content.Children.Add(hint);
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 10, 0, 0), MinHeight = 66, Height = double.NaN };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            if (accent) button.SetResourceReference(BorderBrushProperty, "Brand");
            button.Click += (_, __) => choose(); return button;
        }
        private static TextBlock Text(string value, double size, bool bold = false, double top = 0)
        {
            var result = new TextBlock { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            result.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary"); return result;
        }
        private static string T(string key) => UiLanguage.Get("TimeTracking." + key);
        private static string Duration(double hours)
        {
            long minutes = (long)Math.Round(Math.Max(0, hours) * 60, MidpointRounding.AwayFromZero);
            return minutes / 60 + " h " + (minutes % 60).ToString("00");
        }
    }
}
