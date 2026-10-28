using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryReconstruction
    {
        private static readonly string[] NativePrimaryLevelParameters = {
            "FAMILY_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM", "RBS_START_LEVEL_PARAM", "FAMILY_BASE_LEVEL_PARAM"
        };

        private static bool NativeReferenceMatches(Document doc, string expected, Element actual)
        {
            return actual != null && !string.IsNullOrEmpty(expected)
                && (actual.UniqueId == expected || ElementHistoryRestoration.GetOrigins(actual)?.Contains(expected) == true);
        }

        private static string NativePrimaryLevelSource(Document doc, HistoryRecipe recipe)
        {
            var captured = recipe.HistoricalPlacement?.LevelSourceUniqueId ?? recipe.Level;
            if (!string.IsNullOrEmpty(captured)) return captured;
            // Only an actual primary-level builtin is evidence. A schedule-only
            // level, name, nearest altitude or capsule fallback level is not.
            foreach (var name in NativePrimaryLevelParameters)
            {
                if (!Enum.TryParse(name, out BuiltInParameter builtIn)) continue;
                var values = (recipe.Parameters ?? new List<HistoryParameter>())
                    .Where(value => value.BuiltIn == (int)builtIn && value.Storage == (int)StorageType.ElementId
                        && !string.IsNullOrEmpty(value.Reference)).ToList();
                if (values.Count > 1 && values.Select(value => value.Reference).Distinct().Count() != 1)
                    throw new InvalidOperationException("Plusieurs niveaux historiques contradictoires : " + name + ".");
                if (values.Count > 0) return values[0].Reference;
            }
            return null;
        }

        private static void RestoreNativePrimaryLevel(Document doc, Element element, HistoryRecipe recipe, string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                if (FindLevel(element) != null)
                    throw new InvalidOperationException("Le niveau principal historique n’est pas conservé dans cette ancienne capsule ; aucun niveau de remplacement ne sera choisi.");
                return;
            }
            var expected = FindOriginal(doc, source) as Level;
            if (expected == null) throw new InvalidOperationException("Niveau historique absent ou non résolu : " + source + ".");
            if (NativeReferenceMatches(doc, source, FindLevel(element))) return;
            foreach (var name in NativePrimaryLevelParameters)
            {
                if (!Enum.TryParse(name, out BuiltInParameter builtIn)) continue;
                var parameter = element.get_Parameter(builtIn);
                if (parameter == null || parameter.StorageType != StorageType.ElementId || parameter.IsReadOnly) continue;
                if (parameter.AsElementId() != expected.Id && !parameter.Set(expected.Id))
                    throw new InvalidOperationException("Le niveau historique est refusé par " + name + ".");
                doc.Regenerate();
                if (NativeReferenceMatches(doc, source, FindLevel(element))) return;
            }
            throw new InvalidOperationException("Le niveau principal de l’objet natif ne peut pas être rétabli exactement : " + source + ".");
        }

        private static List<HistoryParameter> NativeMetadataParameters(Element element, HistoryRecipe recipe)
        {
            var values = recipe.Parameters ?? new List<HistoryParameter>();
            if (element is FamilyInstance family && family.Symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveDrivenStructural
                && SavedHistoricalPlacement(recipe)?.Curve != null)
                return values.Where(value => value.BuiltIn != (int)BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION
                    && value.BuiltIn != (int)BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION).ToList();
            return values;
        }

        private static bool NativeMetadataValueMatches(Document doc, Element element, HistoryParameter value)
        {
            var parameter = HistoricalParameter(doc, element, value);
            if (parameter == null || parameter.StorageType != (StorageType)value.Storage) return false;
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return !double.IsNaN(parameter.AsDouble()) && !double.IsInfinity(parameter.AsDouble())
                        && Math.Abs(parameter.AsDouble() - value.Number) <= 1e-7;
                case StorageType.Integer: return parameter.AsInteger() == (int)value.Number;
                case StorageType.String: return string.Equals(parameter.AsString(), value.Text, StringComparison.Ordinal);
                case StorageType.ElementId:
                    return string.IsNullOrEmpty(value.Reference)
                        ? parameter.AsElementId().GetIdLongValue() == (long)value.Number
                        : NativeReferenceMatches(doc, value.Reference, doc.GetElement(parameter.AsElementId()));
                default: return false;
            }
        }

        internal static bool VerifyNativeMetadata(Document doc, Element element, HistoryRecipe recipe, out string failure)
        {
            failure = null;
            if (element == null || !element.IsValidObject) { failure = "L’objet natif restauré est absent."; return false; }
            var primary = NativePrimaryLevelSource(doc, recipe);
            if (!(element is Group) && (FindLevel(element) != null || !string.IsNullOrEmpty(primary)) && !NativeReferenceMatches(doc, primary, FindLevel(element)))
            { failure = "Le niveau principal historique de l’objet natif n’a pas été conservé."; return false; }
            var host = recipe.HistoricalPlacement?.HostSourceUniqueId ?? recipe.Host;
            if (!string.IsNullOrEmpty(host) && (!(element is FamilyInstance family) || !NativeReferenceMatches(doc, host, family.Host)))
            { failure = "L’hôte historique de l’objet natif n’a pas été conservé : " + host + "."; return false; }
            foreach (var value in NativeMetadataParameters(element, recipe))
                if (!NativeMetadataValueMatches(doc, element, value))
                {
                    var parameter = HistoricalParameter(doc, element, value);
                    failure = "Paramètre natif historique non conservé après régénération : "
                        + (value.Name ?? parameter?.Definition?.Name ?? value.Reference ?? value.Shared)
                        + " [builtIn=" + value.BuiltIn + ", storage=" + (StorageType)value.Storage + "].";
                    return false;
                }
            return VerifyHistoricalPlacement(element, recipe, out failure, includeBoundingBox: !(element is Wall));
        }

        // Only the creation branch may call this. The caller owns the transaction
        // group and must never pass a retained original neighbor or edit the recipe.
        internal static void RestoreNativeMetadata(Document doc, Element element, HistoryRecipe recipe)
        {
            if (doc == null || !doc.IsModifiable) throw new InvalidOperationException("Les métadonnées natives nécessitent une transaction.");
            if (element == null || !element.IsValidObject || recipe == null) throw new InvalidOperationException("Objet natif ou recette historique absent.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var primary = NativePrimaryLevelSource(doc, recipe);
            var values = NativeMetadataParameters(element, recipe);
            try
            {
                if (!(element is Group)) RestoreNativePrimaryLevel(doc, element, recipe, primary);
                // Level references precede offsets and metadata. ApplyParameters
                // preserves captured IFC/Comments values when the API allows it;
                // the final readback also rejects a divergent read-only value.
                var levelValues = values.Where(value => value.Storage == (int)StorageType.ElementId
                    && !string.IsNullOrEmpty(value.Reference) && FindOriginal(doc, value.Reference) is Level).ToList();
                ApplyParameters(doc, element, levelValues);
                doc.Regenerate();
                ApplyParameters(doc, element, values.Except(levelValues).ToList());
                doc.Regenerate();
                RestoreHistoricalPlacement(doc, element, recipe);
                if (!VerifyNativeMetadata(doc, element, recipe, out var failure)) throw new InvalidOperationException(failure);
                HistoryPerformanceLog.RecordDetails(doc, "native_metadata", timer.Elapsed.TotalMilliseconds, 1, 0, true,
                    new { ElementId = element.Id.GetIdLongValue(), ElementClass = element.GetType().Name, PrimaryLevelSource = primary,
                        ActualLevel = FindLevel(element)?.UniqueId, ParameterCount = values.Count, BoundingBoxChecked = !(element is Wall) });
            }
            catch (Exception ex)
            {
                HistoryPerformanceLog.RecordDetails(doc, "native_metadata", timer.Elapsed.TotalMilliseconds, 1, 0, false,
                    new { ElementId = element.Id.GetIdLongValue(), ElementClass = element.GetType().Name, PrimaryLevelSource = primary,
                        ActualLevel = FindLevel(element)?.UniqueId, ParameterCount = values.Count, Failure = ex.Message });
                throw;
            }
        }
    }
}
