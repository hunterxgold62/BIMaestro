using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace BIMaestro.ViewHover
{
    // Changes the existing tab's header presentation only. Header, Content,
    // native click/close handlers and the surrounding docking grid stay intact.
    internal sealed class ViewDeckTabDecorator : IDisposable
    {
        private sealed class SavedValue
        {
            internal object Local;
            internal BindingBase Binding;
            internal object Applied;
        }

        private static readonly DependencyProperty[] Properties =
        {
            HeaderedContentControl.HeaderTemplateProperty,
            FrameworkElement.HeightProperty, FrameworkElement.MinHeightProperty,
            FrameworkElement.MaxHeightProperty, FrameworkElement.WidthProperty,
            FrameworkElement.MinWidthProperty, FrameworkElement.MaxWidthProperty
        };
        private readonly TabItem _tab;
        private readonly Dictionary<DependencyProperty, SavedValue> _saved =
            new Dictionary<DependencyProperty, SavedValue>();
        private string _signature;
        private bool _disposed;

        internal static ViewDeckTabDecorator Attach(TabItem tab)
        {
            if (tab == null) return null;
            foreach (DependencyProperty property in Properties)
                if (DependencyPropertyHelper.GetValueSource(tab, property).IsExpression &&
                    BindingOperations.GetBindingBase(tab, property) == null) return null;
            return new ViewDeckTabDecorator(tab);
        }

        private ViewDeckTabDecorator(TabItem tab)
        {
            _tab = tab;
            foreach (DependencyProperty property in Properties)
                _saved.Add(property, new SavedValue
                {
                    Local = tab.ReadLocalValue(property),
                    Binding = BindingOperations.GetBindingBase(tab, property)
                });
            try
            {
                Set(FrameworkElement.MaxHeightProperty, double.PositiveInfinity);
                Set(FrameworkElement.MaxWidthProperty, double.PositiveInfinity);
                Set(FrameworkElement.MinHeightProperty, 100d);
                Set(FrameworkElement.MinWidthProperty, 166d);
                Set(FrameworkElement.HeightProperty, 100d);
                Set(FrameworkElement.WidthProperty, 166d);
            }
            catch { Dispose(); throw; }
        }

        internal void Update(string title, ImageSource image, string placeholder, string signature)
        {
            if (_disposed || _signature == signature) return;
            Set(HeaderedContentControl.HeaderTemplateProperty, CreateTemplate(title, image, placeholder));
            _signature = signature;
        }

        private void Set(DependencyProperty property, object value)
        {
            _tab.SetValue(property, value);
            _saved[property].Applied = value;
        }

        private DataTemplate CreateTemplate(string title, ImageSource image, string placeholder)
        {
            // Give the close button its own corner, outside the title/image stack.
            // The 130-DIP preview stays centred at its original size and position.
            var root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(FrameworkElement.WidthProperty, 154d);
            root.SetValue(FrameworkElement.HeightProperty, 90d);
            var content = new FrameworkElementFactory(typeof(StackPanel));
            content.SetValue(FrameworkElement.WidthProperty, 130d);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var heading = new FrameworkElementFactory(typeof(Grid));
            heading.SetValue(FrameworkElement.HeightProperty, 19d);
            var close = new FrameworkElementFactory(typeof(Button), "ViewDeckClose");
            close.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            close.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            close.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
            close.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            close.SetValue(ContentControl.ContentProperty, "×");
            close.SetValue(FrameworkElement.WidthProperty, 19d);
            close.SetValue(FrameworkElement.HeightProperty, 19d);
            close.SetValue(Control.FontSizeProperty, 15d);
            close.SetValue(Control.PaddingProperty, new Thickness(0));
            close.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            close.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            close.SetValue(Control.ForegroundProperty, Brushes.Black);
            close.SetValue(UIElement.FocusableProperty, false);
            close.SetValue(FrameworkElement.ToolTipProperty, "Fermer la vue");
            close.SetBinding(UIElement.IsEnabledProperty, new Binding("CanClose") { FallbackValue = false });
            close.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnClose));
            var label = new FrameworkElementFactory(typeof(TextBlock), "ViewDeckTitle");
            label.SetBinding(TextBlock.TextProperty, new Binding("Title") { FallbackValue = title });
            label.SetValue(TextBlock.FontSizeProperty, 11d);
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 0, 14, 4));
            heading.AppendChild(label);
            content.AppendChild(heading);

            var frame = new FrameworkElementFactory(typeof(Border), "ViewDeckPreview");
            frame.SetValue(FrameworkElement.HeightProperty, 64d);
            frame.SetValue(Border.BackgroundProperty, Brushes.White);
            frame.SetValue(Border.BorderBrushProperty, Brushes.LightGray);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            frame.SetValue(Border.PaddingProperty, new Thickness(1));
            if (image != null)
            {
                var preview = new FrameworkElementFactory(typeof(Image));
                preview.SetValue(Image.SourceProperty, image);
                preview.SetValue(Image.StretchProperty, Stretch.Uniform);
                frame.AppendChild(preview);
            }
            else
            {
                var empty = new FrameworkElementFactory(typeof(TextBlock));
                empty.SetValue(TextBlock.TextProperty, placeholder);
                empty.SetValue(TextBlock.FontSizeProperty, 10d);
                empty.SetValue(TextBlock.ForegroundProperty, Brushes.DimGray);
                empty.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                empty.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                frame.AppendChild(empty);
            }
            content.AppendChild(frame);
            root.AppendChild(content);
            root.AppendChild(close); // Painted last, with an independent click target.
            var template = new DataTemplate { VisualTree = root };
            var selected = new DataTrigger
            {
                Binding = new Binding("IsSelected")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TabItem), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.DodgerBlue, "ViewDeckPreview"));
            template.Triggers.Add(selected);
            template.Seal();
            return template;
        }

        private void OnClose(object sender, RoutedEventArgs args)
        {
            args.Handled = true;
            if (_disposed) return;
            // Use the exact native layout model and its own closing pipeline.
            // Never delete a DB.View or guess a target from its displayed name.
            object model = _tab.Header;
            try
            {
                if (!(model?.GetType().GetProperty("CanClose")?.GetValue(model, null) is bool canClose) || !canClose)
                    return;
                model.GetType().GetMethod("Close", Type.EmptyTypes)?.Invoke(model, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("BIMaestro ViewDeck close: " + ex);
                MessageBox.Show("Revit n'a pas pu fermer cette vue. Utilisez sa commande de fermeture native.",
                    "BIMaestro - Onglets", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pair in _saved)
            {
                SavedValue saved = pair.Value;
                // Preserve a newer override by Revit/another extension.
                if (saved.Applied == null || !Equals(_tab.ReadLocalValue(pair.Key), saved.Applied)) continue;
                if (saved.Binding != null) BindingOperations.SetBinding(_tab, pair.Key, saved.Binding);
                else if (saved.Local == DependencyProperty.UnsetValue) _tab.ClearValue(pair.Key);
                else _tab.SetValue(pair.Key, saved.Local);
            }
            _saved.Clear();
        }
    }
}
