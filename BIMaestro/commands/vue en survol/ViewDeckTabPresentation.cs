using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace BIMaestro.ViewHover
{
    // Hover lives as long as the native tab. ON/OFF controls only the inline image.
    internal sealed class ViewDeckTabPresentation : IDisposable
    {
        private readonly TabItem _tab;
        private ViewDeckTabDecorator _decorator;
        private bool _disposed;
        internal ViewDeckHoverPreview Hover { get; }
        internal bool IsExpanded => _decorator != null;

        internal ViewDeckTabPresentation(TabItem tab)
        {
            _tab = tab;
            Hover = new ViewDeckHoverPreview(tab);
        }

        internal void SetExpanded(bool expanded)
        {
            if (_disposed || expanded == IsExpanded) return;
            Hover.Close();
            if (expanded) _decorator = ViewDeckTabDecorator.Attach(_tab);
            else { _decorator?.Dispose(); _decorator = null; }
        }

        internal void Update(bool expanded, string title, ImageSource image, string placeholder, string signature,
            ViewDeckChangeCounts changes = null)
        {
            if (_disposed) return;
            SetExpanded(expanded);
            Hover.Update(title, image, placeholder, changes);
            _decorator?.Update(title, image, placeholder, signature);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Hover.Dispose();
            _decorator?.Dispose();
            _decorator = null;
        }
    }
}
