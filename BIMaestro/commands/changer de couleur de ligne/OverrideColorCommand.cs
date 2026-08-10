using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using BIMaestro.Localization;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class OverrideColorCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "OverrideColorCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("BIMaestro", UiLanguage.T("Aucun document Revit actif.", "No Active Revit Document."));
                return Result.Failed;
            }

            Document doc = uidoc.Document;
            ICollection<ElementId> selectedElementIds = uidoc.Selection.GetElementIds();

            if (selectedElementIds == null || selectedElementIds.Count == 0)
            {
                TaskDialog.Show(
                    UiLanguage.T("Surcharges vues", "View Overrides"),
                    UiLanguage.T("Sélectionne d’abord un ou plusieurs éléments Revit avant de lancer l’outil.", "Select One or More Revit Elements before Running the Tool."));
                return Result.Cancelled;
            }

            View activeView = uidoc.ActiveView;

            ExistingOverrideInfo existingOverrideInfo = TryFindExistingOverride(doc, activeView, selectedElementIds);

            bool copyExistingOverrides = false;

            if (existingOverrideInfo.HasOverride)
            {
                Element referenceElement = doc.GetElement(existingOverrideInfo.ElementId);
                string referenceName = GetReadableElementName(referenceElement, existingOverrideInfo.ElementId);

                TaskDialog dialog = new TaskDialog(UiLanguage.T("Surcharges graphiques existantes", "Existing Graphic Overrides"))
                {
                    MainInstruction = UiLanguage.T("Un des éléments sélectionnés possède déjà une surcharge graphique dans la vue active.", "One of the Selected Elements Already Has a Graphic Override in the Active View."),
                    MainContent =
                        UiLanguage.T("Élément détecté : ", "Detected Element: ") + referenceName + "\n\n" +
                        UiLanguage.T("Voulez-vous copier ses réglages graphiques sur les autres éléments sélectionnés et dans les vues choisies ?\n\nOui : copier la surcharge existante.\nNon : ouvrir les options normales de l’outil.", "Do You Want to Copy Its Graphic Settings to the Other Selected Elements and Chosen Views?\n\nYes: Copy the Existing Override.\nNo: Open the Tool's Normal Options."),
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Yes
                };

                TaskDialogResult response = dialog.Show();

                if (response == TaskDialogResult.Cancel)
                    return Result.Cancelled;

                copyExistingOverrides = response == TaskDialogResult.Yes;
            }

            GraphicOverrideWindow window = new GraphicOverrideWindow(
                data.Application,
                optionsEnabled: !copyExistingOverrides,
                copyExistingOverridesMode: copyExistingOverrides);

            bool? dialogResult = window.ShowDialog();

            if (dialogResult != true)
                return Result.Cancelled;

            List<View> targetViews = window.SelectedViews
                .Where(v => v != null && v.IsValidObject)
                .Where(v => v.AreGraphicsOverridesAllowed())
                .GroupBy(v => v.Id)
                .Select(g => g.First())
                .ToList();

            if (targetViews.Count == 0)
            {
                TaskDialog.Show(
                    UiLanguage.T("Surcharges vues", "View Overrides"),
                    UiLanguage.T("Aucune vue valide sélectionnée.\n\nNote : les feuilles ne supportent pas directement les surcharges graphiques. L’outil applique les réglages aux vues placées sur les feuilles sélectionnées.", "No Valid View Selected.\n\nNote: Sheets Do Not Directly Support Graphic Overrides. The Tool Applies Settings to Views Placed on the Selected Sheets."));
                return Result.Cancelled;
            }

            if (window.IsResetRequested)
            {
                ResetOverrides(doc, selectedElementIds, targetViews, window.UnhideElements);
                return Result.Succeeded;
            }

            if (copyExistingOverrides)
            {
                if (!existingOverrideInfo.HasOverride || existingOverrideInfo.OverrideSettings == null)
                {
                    TaskDialog.Show(
                        UiLanguage.T("Surcharges vues", "View Overrides"),
                        UiLanguage.T("Impossible de récupérer la surcharge graphique existante.", "Unable to Retrieve the Existing Graphic Override."));
                    return Result.Failed;
                }

                CopyExistingOverrides(
                    doc,
                    selectedElementIds,
                    targetViews,
                    existingOverrideInfo.OverrideSettings);

                return Result.Succeeded;
            }

            ApplyOverrides(doc, selectedElementIds, targetViews, window);

            return Result.Succeeded;
        }

        private static void ApplyOverrides(
            Document doc,
            ICollection<ElementId> selectedElementIds,
            IList<View> targetViews,
            GraphicOverrideWindow window)
        {
            using (Transaction tx = new Transaction(doc, "BIMaestro - Surcharges vues"))
            {
                tx.Start();

                foreach (View view in targetViews)
                {
                    if (view == null || !view.IsValidObject || !view.AreGraphicsOverridesAllowed())
                        continue;

                    foreach (ElementId elementId in selectedElementIds)
                    {
                        if (elementId == ElementId.InvalidElementId)
                            continue;

                        Element element = doc.GetElement(elementId);
                        if (element == null)
                            continue;

                        try
                        {
                            if (window.HideInView)
                            {
                                if (element.CanBeHidden(view))
                                {
                                    view.HideElements(new List<ElementId> { elementId });
                                }

                                continue;
                            }

                            OverrideGraphicSettings ogs = new OverrideGraphicSettings();

                            ogs.SetHalftone(window.ApplyHalftone);
                            ogs.SetSurfaceTransparency(window.SelectedTransparency);

                            view.SetElementOverrides(elementId, ogs);
                        }
                        catch
                        {
                            // Certaines vues ou certains éléments ne supportent pas l’opération.
                            // On continue pour ne pas bloquer le traitement global.
                        }
                    }
                }

                tx.Commit();
            }
        }

        private static void CopyExistingOverrides(
            Document doc,
            ICollection<ElementId> selectedElementIds,
            IList<View> targetViews,
            OverrideGraphicSettings referenceOverrides)
        {
            using (Transaction tx = new Transaction(doc, "BIMaestro - Copier surcharges vues"))
            {
                tx.Start();

                foreach (View view in targetViews)
                {
                    if (view == null || !view.IsValidObject || !view.AreGraphicsOverridesAllowed())
                        continue;

                    foreach (ElementId elementId in selectedElementIds)
                    {
                        if (elementId == ElementId.InvalidElementId)
                            continue;

                        Element element = doc.GetElement(elementId);
                        if (element == null)
                            continue;

                        try
                        {
                            view.SetElementOverrides(elementId, referenceOverrides);
                        }
                        catch
                        {
                            // Vue ou élément non compatible.
                        }
                    }
                }

                tx.Commit();
            }
        }

        private static void ResetOverrides(
            Document doc,
            ICollection<ElementId> selectedElementIds,
            IList<View> targetViews,
            bool unhideElements)
        {
            using (Transaction tx = new Transaction(doc, "BIMaestro - Réinitialiser surcharges vues"))
            {
                tx.Start();

                foreach (View view in targetViews)
                {
                    if (view == null || !view.IsValidObject || !view.AreGraphicsOverridesAllowed())
                        continue;

                    foreach (ElementId elementId in selectedElementIds)
                    {
                        if (elementId == ElementId.InvalidElementId)
                            continue;

                        try
                        {
                            view.SetElementOverrides(elementId, new OverrideGraphicSettings());
                        }
                        catch
                        {
                            // Vue ou élément non compatible.
                        }

                        if (unhideElements)
                        {
                            try
                            {
                                view.UnhideElements(new List<ElementId> { elementId });
                            }
                            catch
                            {
                                // L’élément n’était peut-être pas masqué ou la vue ne le permet pas.
                            }
                        }
                    }
                }

                tx.Commit();
            }
        }

        private static ExistingOverrideInfo TryFindExistingOverride(
            Document doc,
            View activeView,
            ICollection<ElementId> selectedElementIds)
        {
            if (doc == null ||
                activeView == null ||
                selectedElementIds == null ||
                selectedElementIds.Count == 0 ||
                !activeView.AreGraphicsOverridesAllowed())
            {
                return ExistingOverrideInfo.None;
            }

            foreach (ElementId elementId in selectedElementIds)
            {
                if (elementId == ElementId.InvalidElementId)
                    continue;

                Element element = doc.GetElement(elementId);
                if (element == null)
                    continue;

                try
                {
                    OverrideGraphicSettings ogs = activeView.GetElementOverrides(elementId);

                    if (HasAnyOverrides(ogs))
                    {
                        return new ExistingOverrideInfo
                        {
                            HasOverride = true,
                            ElementId = elementId,
                            OverrideSettings = ogs
                        };
                    }
                }
                catch
                {
                    // Certains éléments peuvent ne pas être compatibles.
                }
            }

            return ExistingOverrideInfo.None;
        }

        private static bool HasAnyOverrides(OverrideGraphicSettings ogs)
        {
            if (ogs == null || !ogs.IsValidObject)
                return false;

            if (HasColorOverride(ogs.ProjectionLineColor))
                return true;

            if (HasColorOverride(ogs.CutLineColor))
                return true;

            if (ogs.ProjectionLinePatternId != ElementId.InvalidElementId)
                return true;

            if (ogs.CutLinePatternId != ElementId.InvalidElementId)
                return true;

            if (ogs.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                return true;

            if (ogs.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber)
                return true;

            if (ogs.SurfaceBackgroundPatternId != ElementId.InvalidElementId)
                return true;

            if (ogs.SurfaceForegroundPatternId != ElementId.InvalidElementId)
                return true;

            if (ogs.CutBackgroundPatternId != ElementId.InvalidElementId)
                return true;

            if (ogs.CutForegroundPatternId != ElementId.InvalidElementId)
                return true;

            if (HasColorOverride(ogs.SurfaceBackgroundPatternColor))
                return true;

            if (HasColorOverride(ogs.SurfaceForegroundPatternColor))
                return true;

            if (HasColorOverride(ogs.CutBackgroundPatternColor))
                return true;

            if (HasColorOverride(ogs.CutForegroundPatternColor))
                return true;

            if (ogs.DetailLevel != ViewDetailLevel.Undefined)
                return true;

            if (ogs.Halftone)
                return true;

#if REVIT_2025_OR_GREATER
            if (ogs.Transparency >= 0)
                return true;
#endif

            return false;
        }

        private static bool HasColorOverride(Autodesk.Revit.DB.Color color)
        {
            return color != null && color.IsValid;
        }

        private static string GetReadableElementName(Element element, ElementId fallbackId)
        {
            if (element == null)
                return $"Id {fallbackId.IntegerValue}";

            string name = null;

            try
            {
                name = element.Name;
            }
            catch
            {
                // Certains éléments n’exposent pas toujours Name proprement.
            }

            if (!string.IsNullOrWhiteSpace(name))
                return $"{name} - Id {element.Id.IntegerValue}";

            return $"Id {element.Id.IntegerValue}";
        }

        private class ExistingOverrideInfo
        {
            public bool HasOverride { get; set; }

            public ElementId ElementId { get; set; }

            public OverrideGraphicSettings OverrideSettings { get; set; }

            public static ExistingOverrideInfo None
            {
                get
                {
                    return new ExistingOverrideInfo
                    {
                        HasOverride = false,
                        ElementId = ElementId.InvalidElementId,
                        OverrideSettings = null
                    };
                }
            }
        }
    }
}
