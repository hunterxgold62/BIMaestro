using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMaestro.Localization;

namespace BIMaestro.PropertiesAppearance
{
    // "Rules" tab: a look shown only when a parameter value matches, written as a sentence.
    internal sealed partial class PropertiesAppearanceEditor
    {
        private sealed class ParameterChoice
        {
            internal string Key, Label;
            public override string ToString() => Label;
        }
        private readonly StackPanel _ruleList = new StackPanel(), _ruleForm = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        private readonly ComboBox _ruleParameter = new ComboBox { Margin = new Thickness(0, 0, 0, 8) }, _ruleOperator = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBox _ruleValue = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBlock _ruleValueHint = new TextBlock(), _ruleError = new TextBlock();
        private readonly List<Tuple<Button, Look>> _ruleLookButtons = new List<Tuple<Button, Look>>();
        private readonly List<Tuple<Button, string>> _ruleSwatchButtons = new List<Tuple<Button, string>>();
        private Button _newRule;
        private AppearanceRule _editingRule;
        private Look _ruleLook = Looks[0];
        private string _ruleColor = "#77521A";

        private void BuildRulesPage()
        {
            var hint = Label(UiLanguage.Get("PropertiesAppearanceWindow.RulesHint"), 12, false, true); _rulesPage.Children.Add(hint);
            _rulesPage.Children.Add(_ruleList);
            _newRule = MakeButton("+  " + UiLanguage.Get("PropertiesAppearanceWindow.NewRule"), () => OpenRule(null));
            _newRule.HorizontalAlignment = HorizontalAlignment.Stretch; _newRule.Height = double.NaN; _newRule.MinHeight = 36; _newRule.Margin = new Thickness(0, 4, 0, 8); _rulesPage.Children.Add(_newRule);

            // The form reads as a sentence: if [parameter] [condition] [value], then [look] in [color].
            _ruleForm.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.RuleIf"), 12, true));
            _ruleParameter.SelectionChanged += (_, __) => UpdateRuleValueHint(); _ruleForm.Children.Add(_ruleParameter);
            foreach (var op in RuleCondition.Operators)
                _ruleOperator.Items.Add(new ComboBoxItem { Content = UiLanguage.Get("PropertiesAppearanceWindow.Rule" + OperatorName(op)), Tag = op });
            _ruleOperator.SelectedIndex = 0; _ruleOperator.SelectionChanged += (_, __) => UpdateRuleValueHint(); _ruleForm.Children.Add(_ruleOperator);
            _ruleValue.SetResourceReference(Control.ForegroundProperty, "Ink"); _ruleValue.SetResourceReference(Control.BackgroundProperty, "Surface");
            _ruleValue.TextChanged += (_, __) => _ruleError.Text = ""; _ruleForm.Children.Add(_ruleValue);
            BindText(_ruleValueHint, 12, false, true); _ruleValueHint.Margin = new Thickness(0, 0, 0, 10); _ruleForm.Children.Add(_ruleValueHint);
            _ruleForm.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.RuleThen"), 12, true));
            _ruleForm.Children.Add(LookGallery(_ruleLookButtons, look => { _ruleLook = look; RefreshRuleForm(); }));
            _ruleForm.Children.Add(SwatchRow(_ruleSwatchButtons, color => { _ruleColor = color; RefreshRuleForm(); }, null));
            _ruleError.FontSize = 12; _ruleError.Foreground = Brush("#B42318"); _ruleError.TextWrapping = TextWrapping.Wrap; _ruleForm.Children.Add(_ruleError);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.SaveRule"), SaveRule, true));
            actions.Children.Add(MakeButton(UiLanguage.Get("PropertiesAppearanceWindow.Cancel"), CloseRule));
            _ruleForm.Children.Add(actions);
            _ruleForm.Visibility = Visibility.Collapsed; _rulesPage.Children.Add(_ruleForm);
            var multi = Label(UiLanguage.Get("PropertiesAppearanceWindow.RulesMultipleHint"), 12, false, true); multi.Margin = new Thickness(0, 14, 0, 0); _rulesPage.Children.Add(multi);
        }

        private IEnumerable<AppearanceRule> ConditionalRules => _profile == null ? Enumerable.Empty<AppearanceRule>() : _profile.Rules.Where(r => r.When != null && r.Kind == "parameter");
        private void RefreshRuleList()
        {
            _ruleList.Children.Clear();
            foreach (var rule in ConditionalRules.ToList())
            {
                var r = rule;
                var color = AppearancePreferences.Parse(r.Light?.Text) ?? (Color)ColorConverter.ConvertFromString(DefaultColor);
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sentence = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                sentence.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                sentence.Inlines.Add(UiLanguage.Get("PropertiesAppearanceWindow.RuleIfShort") + " ");
                sentence.Inlines.Add(new System.Windows.Documents.Run((r.Label ?? "").Trim()) { FontWeight = FontWeights.SemiBold });
                sentence.Inlines.Add(" " + UiLanguage.Get("PropertiesAppearanceWindow.Rule" + OperatorName(r.When.Operator)).ToLowerInvariant() + (r.When.NeedsValue ? " « " + r.When.Value + " »" : ""));
                row.Children.Add(sentence);
                var chip = new Border { Background = new SolidColorBrush(AppearanceStyles.Mix(Colors.White, color, .14)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = Describe(AppearanceStyles.Parse(r.Style)), FontSize = 11, Foreground = new SolidColorBrush(color) } };
                Grid.SetColumn(chip, 1); row.Children.Add(chip);
                var delete = MakeButton("×", () => { _profile.Rules.Remove(r); if (ReferenceEquals(_editingRule, r)) CloseRule(); Changed(); RefreshRuleList(); });
                delete.MinWidth = 0; delete.MinHeight = 0; delete.Height = 28; delete.Width = 28; delete.Padding = new Thickness(6, 0, 6, 0); delete.Margin = new Thickness(0); delete.ToolTip = UiLanguage.Get("PropertiesAppearanceWindow.DeleteRule");
                Grid.SetColumn(delete, 2); row.Children.Add(delete);
                var card = MakeButton("", () => OpenRule(r)); card.Content = row; card.Height = double.NaN; card.MinHeight = 40; card.HorizontalContentAlignment = HorizontalAlignment.Stretch; card.Padding = new Thickness(10, 8, 6, 8); card.Margin = new Thickness(0, 0, 0, 6);
                if (ReferenceEquals(_editingRule, r)) { card.BorderBrush = Brush("#20845D"); card.BorderThickness = new Thickness(2); }
                _ruleList.Children.Add(card);
            }
            if (!ConditionalRules.Any()) _ruleList.Children.Add(Label(UiLanguage.Get("PropertiesAppearanceWindow.NoRuleYet"), 12, false, true));
        }
        private static Look LookOf(AppearanceRule rule)
        {
            var set = AppearanceStyles.Parse(rule.Style);
            return Looks.FirstOrDefault(l => SameLook(set, l.Set));
        }
        private void OpenRule(AppearanceRule rule)
        {
            _editingRule = rule;
            // Only editable, unambiguous parameters of the current selection can be watched.
            var choices = _context.PreviewRows.Where(r => !r.Header && !r.ReadOnly && r.ParameterKey != null)
                .GroupBy(r => r.ParameterKey).Select(g => new ParameterChoice { Key = g.Key, Label = g.First().Label.Trim() }).OrderBy(c => c.Label).ToList();
            if (rule != null && choices.All(c => c.Key != rule.TargetKey)) choices.Insert(0, new ParameterChoice { Key = rule.TargetKey, Label = (rule.Label ?? rule.TargetKey).Trim() });
            _ruleParameter.ItemsSource = choices;
            _ruleParameter.SelectedItem = choices.FirstOrDefault(c => c.Key == (rule?.TargetKey ?? _selected?.ParameterKey)) ?? choices.FirstOrDefault();
            _ruleOperator.SelectedItem = _ruleOperator.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (rule?.When?.Operator ?? "=")) ?? _ruleOperator.Items[0];
            _ruleValue.Text = rule?.When?.Value ?? "";
            // A combination made in "More options" stays as is unless a look is picked.
            _ruleLook = rule == null ? Looks[0] : LookOf(rule);
            _ruleColor = rule == null ? "#77521A" : (AppearancePreferences.Parse(rule.Light?.Text) ?? (Color)ColorConverter.ConvertFromString(DefaultColor)).ToString();
            _ruleError.Text = "";
            _ruleForm.Visibility = Visibility.Visible; _newRule.Visibility = Visibility.Collapsed;
            if (choices.Count == 0) _ruleError.Text = UiLanguage.Get("PropertiesAppearanceWindow.RuleNeedsSelection");
            RefreshRuleForm(); UpdateRuleValueHint(); RefreshRuleList();
        }
        private void CloseRule()
        {
            _editingRule = null; _ruleForm.Visibility = Visibility.Collapsed; _newRule.Visibility = Visibility.Visible; RefreshRuleList();
        }
        private void RefreshRuleForm()
        {
            var color = (Color)ColorConverter.ConvertFromString(_ruleColor);
            foreach (var b in _ruleLookButtons)
            {
                // "Title only" makes no sense for a single parameter row.
                b.Item1.Visibility = b.Item2.HeaderOnly ? Visibility.Collapsed : Visibility.Visible;
                b.Item1.Content = LookSample(b.Item2, color);
                bool on = ReferenceEquals(b.Item2, _ruleLook);
                b.Item1.BorderBrush = on ? Brush("#20845D") : (Brush)Resources["Line"]; b.Item1.BorderThickness = new Thickness(on ? 2 : 1);
            }
            foreach (var s in _ruleSwatchButtons)
            {
                bool on = AppearancePreferences.Parse(s.Item2) == color;
                s.Item1.BorderBrush = on ? (Brush)Resources["Ink"] : Brushes.Transparent; s.Item1.BorderThickness = new Thickness(on ? 3 : 1);
            }
        }
        private void UpdateRuleValueHint()
        {
            string op = (_ruleOperator.SelectedItem as ComboBoxItem)?.Tag as string ?? "=";
            _ruleValue.Visibility = op == "empty" || op == "notempty" ? Visibility.Collapsed : Visibility.Visible;
            var key = (_ruleParameter.SelectedItem as ParameterChoice)?.Key;
            var values = key == null ? null : _context.ValuesOf(key);
            if (values == null || values.Count == 0) { _ruleValueHint.Text = ""; return; }
            var distinct = values.Select(v => string.IsNullOrWhiteSpace(v) ? "—" : v).Distinct().Take(4).ToList();
            _ruleValueHint.Text = UiLanguage.Get("PropertiesAppearanceWindow.CurrentValue") + string.Join(" · ", distinct)
                + (values.Count > 1 ? "  (" + values.Count + UiLanguage.Get("PropertiesAppearanceService.Elements") + ")" : "");
        }
        private void SaveRule()
        {
            if (_profile == null) return;
            var parameter = _ruleParameter.SelectedItem as ParameterChoice;
            string op = (_ruleOperator.SelectedItem as ComboBoxItem)?.Tag as string ?? "=";
            if (parameter == null) { _ruleError.Text = UiLanguage.Get("PropertiesAppearanceWindow.RuleNeedsSelection"); return; }
            var when = new RuleCondition { Operator = op, Value = (_ruleValue.Text ?? "").Trim() };
            if (when.NeedsValue && when.Value.Length == 0) { _ruleError.Text = UiLanguage.Get("PropertiesAppearanceWindow.RuleNeedsValue"); return; }
            var rule = _editingRule ?? new AppearanceRule { Kind = "parameter" };
            rule.TargetKey = parameter.Key; rule.Label = parameter.Label; rule.When = when;
            var c = (Color)ColorConverter.ConvertFromString(_ruleColor);
            rule.Light = new PaletteColors { Text = c.ToString(), Background = AppearanceStyles.Mix(Colors.White, c, .12).ToString() };
            rule.Dark = new PaletteColors(); rule.AutoTint = true;
            if (_ruleLook != null) rule.Style = AppearanceStyles.Store(_ruleLook.Set);
            if (_editingRule == null) _profile.Rules.Add(rule);
            _loading = true; _enabled.IsChecked = true; _loading = false;
            CloseRule(); Changed();
        }
    }
}
