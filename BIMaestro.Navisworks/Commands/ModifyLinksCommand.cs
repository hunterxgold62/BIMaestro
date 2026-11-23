using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using BIMaestro.Navisworks.Services;
using BIMaestro.Navisworks.UI;
using NavApplication = Autodesk.Navisworks.Api.Application;

namespace BIMaestro.Navisworks.Commands
{
    [Plugin("BIMaestro.Navisworks", "BIMA", DisplayName = "BIMaestro")]
    [RibbonLayout("BIMaestroRibbon.xaml")]
    [RibbonTab("BIMA_TAB", DisplayName = "BIMaestro")]
    [Command("BIMA_MODIFY_LINKS", DisplayName = "Modifier les liens")]
    [Command("BIMA_PUBLISH_WEB", DisplayName = "Publier sur le web")]
    public sealed class ModifyLinksCommand : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string name, params string[] parameters)
        {
            if (name == "BIMA_PUBLISH_WEB") return PublishWeb();
            if (name != "BIMA_MODIFY_LINKS") return 0;
            try
            {
                if (!LinkService.IsNwf(NavApplication.ActiveDocument))
                    throw new InvalidOperationException("Ouvrez un NWF enregistré pour modifier ses liens.");
                using (var window = new ModifyLinksWindow(new LinkService(NavApplication.ActiveDocument)))
                    window.ShowDialog(NavApplication.Gui.MainWindow);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(NavApplication.Gui.MainWindow, ex.Message, "BIMaestro — Modifier les liens",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int PublishWeb()
        {
            var document = NavApplication.ActiveDocument;
            if (document == null || document.IsClear || string.IsNullOrWhiteSpace(document.FileName))
            {
                MessageBox.Show(NavApplication.Gui.MainWindow, "Ouvrez un document Navisworks enregistré.",
                    "BIMaestro — Publier sur le web", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            using (var window = new PublishWebWindow(document))
                window.ShowDialog(NavApplication.Gui.MainWindow);
            return 0;
        }
    }
}
