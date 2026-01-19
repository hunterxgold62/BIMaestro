using System;
using System.Windows;
using System.Windows.Interop;

namespace Famille.PreviewReview
{
    public partial class PreviewReviewWindow : Window
    {
        public PreviewReviewWindow(IntPtr revitMainWindowHandle, PreviewReviewViewModel vm)
        {
            InitializeComponent();

            DataContext = vm ?? throw new ArgumentNullException(nameof(vm));

            // Owner = Revit (évite fenêtre derrière)
            try
            {
                if (revitMainWindowHandle != IntPtr.Zero)
                {
                    var helper = new WindowInteropHelper(this);
                    helper.Owner = revitMainWindowHandle;
                }
            }
            catch { }

            vm.RequestClose += Vm_RequestClose;
        }

        private void Vm_RequestClose(object sender, RequestCloseEventArgs e)
        {
            try
            {
                if (e.DialogResult.HasValue)
                    DialogResult = e.DialogResult.Value;

                Close();
            }
            catch
            {
                try { Close(); } catch { }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (DataContext is PreviewReviewViewModel vm)
                vm.RequestClose -= Vm_RequestClose;
        }
    }
}
