using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;

namespace Modification
{
    [Transaction(TransactionMode.Manual)]
    public class OverrideColorCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "OverrideColorCommand";

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            var selIds = uidoc.Selection.GetElementIds();

            if (selIds.Count == 0)
            {
                TaskDialog.Show("Erreur", "Veuillez sélectionner au moins un élément avant d’appliquer une opération.");
                return Result.Failed;
            }

            var activeView = uidoc.ActiveView;

            // Détecte s’il existe déjà des overrides sur l’un des éléments sélectionnés
            var (hasExistingOverrides, referenceOverrides, referenceElementId) = TryGetExistingOverrides(activeView, selIds);

            bool useExistingOverrides = false;
            if (hasExistingOverrides)
            {
                var referenceElement = doc.GetElement(referenceElementId);
                var elementName = referenceElement?.Name ?? referenceElementId.IntegerValue.ToString();

                var dialog = new TaskDialog("Copier les graphismes existants")
                {
                    MainInstruction = "Certains éléments sélectionnés possèdent déjà des graphismes spécifiques à la vue active.",
                    MainContent = $"Souhaitez-vous copier les paramètres de \"{elementName}\" sur les autres éléments sélectionnés ?\n" +
                                  "(Les options de modification seront désactivées si vous choisissez Oui.)",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Yes
                };

                var response = dialog.Show();
                if (response == TaskDialogResult.Cancel) return Result.Cancelled;
                if (response == TaskDialogResult.Yes) useExistingOverrides = true;
            }

            var picker = new ColorPickerWindow(uiapp, allowOverrideEditing: !useExistingOverrides);
            bool? ok = picker.ShowDialog();
            if (ok != true) return Result.Cancelled;
            if (picker.IsResetRequested) return Result.Succeeded;

            var views = picker.SelectedViews;
            if (views.Count == 0)
            {
                TaskDialog.Show("Erreur", "Aucune vue sélectionnée.");
                return Result.Cancelled;
            }

            var ogs = useExistingOverrides && referenceOverrides != null
                ? referenceOverrides
                : picker.GetOverrideGraphicSettings();

            bool hideElements = picker.HideInView && !useExistingOverrides;

            using (var tx = new Transaction(doc, "Override Color and Patterns"))
            {
                tx.Start();

                foreach (var view in views)
                {
                    // Ignore les vues qui ne supportent pas les VG overrides (ex. feuilles, légendes… selon cas)
                    if (view == null || !view.AreGraphicsOverridesAllowed())
                        continue;

                    foreach (var id in selIds)
                    {
                        try
                        {
                            if (hideElements)
                            {
                                view.HideElements(new List<ElementId> { id });
                            }
                            else
                            {
                                view.SetElementOverrides(id, ogs);
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                        {
                            // ignore vues non supportées
                        }
                        catch
                        {
                            // ignore autres erreurs
                        }
                    }
                }

                tx.Commit();
            }

            return Result.Succeeded;
        }

        private static (bool hasOverrides, OverrideGraphicSettings overrides, ElementId elementId)
            TryGetExistingOverrides(View view, ICollection<ElementId> elementIds)
        {
            if (view == null || elementIds == null || elementIds.Count == 0 || !view.AreGraphicsOverridesAllowed())
                return (false, null, ElementId.InvalidElementId);

            foreach (var id in elementIds)
            {
                var ogs = view.GetElementOverrides(id);
                if (HasAnyOverrides(ogs))
                    return (true, ogs, id);
            }

            return (false, null, ElementId.InvalidElementId);
        }

        /// <summary>
        /// Détermine si un OverrideGraphicSettings contient au moins une surcharge effective.
        /// Compatible Revit 2023+ (on ne dépend pas de méthodes absentes).
        /// </summary>
        private static bool HasAnyOverrides(OverrideGraphicSettings ogs)
        {
            if (ogs == null || !ogs.IsValidObject) return false;

            // Lignes (projection/coupe)
            if (HasColorOverride(ogs.ProjectionLineColor)) return true;
            if (HasColorOverride(ogs.CutLineColor)) return true;
            if (ogs.ProjectionLinePatternId != ElementId.InvalidElementId) return true;
            if (ogs.CutLinePatternId != ElementId.InvalidElementId) return true;
            if (ogs.ProjectionLineWeight != OverrideGraphicSettings.InvalidPenNumber) return true;
            if (ogs.CutLineWeight != OverrideGraphicSettings.InvalidPenNumber) return true;

            // Motifs / couleurs (surface & coupe)
            if (ogs.SurfaceBackgroundPatternId != ElementId.InvalidElementId) return true;
            if (ogs.SurfaceForegroundPatternId != ElementId.InvalidElementId) return true;
            if (ogs.CutBackgroundPatternId != ElementId.InvalidElementId) return true;
            if (ogs.CutForegroundPatternId != ElementId.InvalidElementId) return true;
            if (HasColorOverride(ogs.SurfaceBackgroundPatternColor)) return true;
            if (HasColorOverride(ogs.SurfaceForegroundPatternColor)) return true;
            if (HasColorOverride(ogs.CutBackgroundPatternColor)) return true;
            if (HasColorOverride(ogs.CutForegroundPatternColor)) return true;

            // Détail & trame
            if (ogs.DetailLevel != ViewDetailLevel.Undefined) return true;

            // Halftone: on considère qu’uniquement TRUE révèle un override (FALSE est indistinguable du défaut).
            if (ogs.Halftone) return true;

            // Transparence :
            // - En 2025+, propriété getter 'Transparency' existe (renvoie -1 si pas d’override).
            // - En 2023/2024, pas de getter public fiable -> on n’y touche pas pour rester compatible.
#if REVIT_2025_OR_GREATER
            if (ogs.Transparency >= 0) return true;
#endif

            return false;
        }

        private static bool HasColorOverride(Color color)
        {
            return color != null && color.IsValid;
        }
    }
}
