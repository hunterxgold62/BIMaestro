using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Interop;
using Newtonsoft.Json.Linq;
using Licensing;      

namespace IA
{
    [Transaction(TransactionMode.Manual)]
    public class TextCorrectionCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "TextCorrectionCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            // 1) Licence → JWT
            string licenseKey = Environment.UserName;
            string machineId = LicenseManager.ComputeMachineId();
            string jwt = LicenseManager.Validate(licenseKey, machineId);

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 2) Sélection des TextNote
            var refs = uidoc.Selection.PickObjects(
                Autodesk.Revit.UI.Selection.ObjectType.Element,
                "Sélectionnez des TextNote à corriger"
            );
            if (refs == null || refs.Count == 0)
                return Result.Cancelled;

            foreach (var r in refs)
            {
                if (!(doc.GetElement(r) is TextNote tn))
                    continue;

                string original = tn.Text;
                string baseline = "";

                // 3) Correction de base via OpenAI
                try
                {
                    var json = AiClient.SendOpenAI(
                        jwt,
                        "gpt-4o-mini",
                        $"Corrige sans ajouter de nouvelles informations : {original}"
                    );

                    baseline = json["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim() ?? "";
                }
                catch
                {
                    continue; // skip si erreur
                }

                // 4) Ouvre la fenêtre de correction
                var window = new CorrectionWindow(original, baseline, jwt);
                IntPtr handle = commandData.Application.MainWindowHandle;
                if (handle != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = handle;

                bool? dr = window.ShowDialog();
                if (window.CorrectionResult != CorrectionWindow.CorrectionDialogResult.OK)
                    continue;

                // 5) Applique la correction
                using (var t = new Transaction(doc, "Appliquer Correction"))
                {
                    t.Start();
                    tn.Text = window.CorrectedText;
                    t.Commit();
                }
            }

            return Result.Succeeded;
        }
    }
}
