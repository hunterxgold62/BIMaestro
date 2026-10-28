using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        // Called only inside the temporary transaction of an isolated capsule.
        // Normalize a transported name only when the original destination type
        // still has exactly the same proven parameter definitions and values.
        private static void PrepareNativeMepTypeReuse(Document source, Document destination)
        {
            if (source == null || destination == null) throw new ArgumentNullException(source == null ? nameof(source) : nameof(destination));
            if (!source.IsModifiable) throw new InvalidOperationException("La préparation des types MEP exige la transaction temporaire de la copie isolée.");
            var sourceTypes = new FilteredElementCollector(source).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>().ToList();
            var references = new Dictionary<string, string>(StringComparer.Ordinal);

            bool IsUniqueId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length > 37
                && value[36] == '-' && Guid.TryParse(value.Substring(0, 36), out _)
                && value.Substring(37).All(Uri.IsHexDigit);

            string OriginalReference(Element original)
            {
                if (original == null || !original.IsValidObject) return null;
                if (references.TryGetValue(original.UniqueId, out var cached)) return cached;
                Element actual = null;
                var declared = ReadHistoricalDefinitionSource(original);
                if (IsUniqueId(declared))
                {
                    actual = ElementHistoryReconstruction.FindOriginal(destination, declared);
                    // A declared frozen identity must never be replaced with a
                    // different alias just because that alias still exists.
                    if (actual == null || actual.GetType() != original.GetType())
                    { references[original.UniqueId] = null; return null; }
                }
                else
                {
                    actual = ElementHistoryReconstruction.FindOriginal(destination, original.UniqueId);
                    if (actual != null && actual.GetType() != original.GetType()) actual = null;
                    if (actual == null)
                    {
                        var matches = (ElementHistoryRestoration.GetOrigins(original) ?? new List<string>())
                            .Where(IsUniqueId).Distinct(StringComparer.Ordinal)
                            .Select(uid => ElementHistoryReconstruction.FindOriginal(destination, uid))
                            .Where(element => element != null && element.GetType() == original.GetType())
                            .GroupBy(element => element.UniqueId, StringComparer.Ordinal).Select(group => group.First()).ToList();
                        if (matches.Count == 1) actual = matches[0];
                    }
                }
                return references[original.UniqueId] = actual?.UniqueId;
            }

            string ParameterKey(Document document, Parameter parameter, bool historical)
            {
                long id = parameter.Id.GetIdLongValue();
                if (id < 0) return "builtin:" + id.ToString(CultureInfo.InvariantCulture);
                if (parameter.IsShared)
                    return parameter.GUID == Guid.Empty ? null : "shared:" + parameter.GUID.ToString("D");
                var declaration = document.GetElement(parameter.Id) as ParameterElement;
                if (declaration == null || declaration.GetDefinition() == null || parameter.Definition == null
                    || declaration.GetDefinition().Name != parameter.Definition.Name
                    || declaration.GetDefinition().GetDataType().TypeId != parameter.Definition.GetDataType().TypeId) return null;
                var identity = historical ? OriginalReference(declaration) : declaration.UniqueId;
                return identity == null ? null : "project:" + identity;
            }

            Dictionary<string, Parameter> Parameters(Document document, Element type, bool historical)
            {
                var result = new Dictionary<string, Parameter>(StringComparer.Ordinal);
                foreach (Parameter parameter in type.Parameters)
                {
                    long id = parameter.Id.GetIdLongValue();
                    // ALL_MODEL_TYPE_NAME and SYMBOL_NAME_PARAM are aliases in
                    // supported versions; exclude the builtin, never a name match.
                    if (id == (long)BuiltInParameter.ALL_MODEL_TYPE_NAME || id == (long)BuiltInParameter.SYMBOL_NAME_PARAM) continue;
                    var key = ParameterKey(document, parameter, historical);
                    if (key == null || result.ContainsKey(key)) return null;
                    result.Add(key, parameter);
                }
                return result;
            }

            string ReferenceValue(Document document, Parameter parameter, bool historical)
            {
                var id = parameter.AsElementId();
                if (id == null) return null;
                long number = id.GetIdLongValue();
                if (number < 0) return "builtin-reference:" + number.ToString(CultureInfo.InvariantCulture);
                var element = document.GetElement(id);
                if (element == null) return null;
                var identity = historical ? OriginalReference(element) : element.UniqueId;
                return identity == null ? null : "element:" + identity;
            }

            bool Equal(Parameter historical, Parameter actual)
            {
                if (historical.Definition == null || actual.Definition == null
                    || historical.StorageType != actual.StorageType || historical.HasValue != actual.HasValue
                    || historical.IsReadOnly != actual.IsReadOnly
                    || !string.Equals(historical.Definition.GetDataType().TypeId, actual.Definition.GetDataType().TypeId, StringComparison.Ordinal)) return false;
                if (!historical.HasValue) return true;
                switch (historical.StorageType)
                {
                    case StorageType.String: return string.Equals(historical.AsString(), actual.AsString(), StringComparison.Ordinal);
                    case StorageType.Integer: return historical.AsInteger() == actual.AsInteger();
                    case StorageType.Double:
                        double before = historical.AsDouble(), after = actual.AsDouble();
                        return !double.IsNaN(before) && !double.IsInfinity(before) && !double.IsNaN(after) && !double.IsInfinity(after) && before == after;
                    case StorageType.ElementId:
                        var expected = ReferenceValue(source, historical, true);
                        return expected != null && expected == ReferenceValue(destination, actual, false);
                    default: return false;
                }
            }

            foreach (var historical in sourceTypes)
            {
                try
                {
                    var identity = ReadHistoricalDefinitionSource(historical);
                    if (!IsUniqueId(identity) || !historical.CanBeRenamed) continue;
                    var actual = ElementHistoryReconstruction.FindOriginal(destination, identity) as MEPSystemType;
                    if (actual == null || actual.GetType() != historical.GetType() || actual.SystemClassification != historical.SystemClassification) continue;
                    if (sourceTypes.Any(type => type.Id != historical.Id && string.Equals(type.Name, actual.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var before = Parameters(source, historical, true);
                    var after = Parameters(destination, actual, false);
                    if (before == null || after == null || before.Count != after.Count
                        || before.Any(pair => !after.TryGetValue(pair.Key, out var value) || !Equal(pair.Value, value))) continue;
                    if (!string.Equals(historical.Name, actual.Name, StringComparison.Ordinal)) historical.Name = actual.Name;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception failure)
                {
                    // Unknown metadata or an API refusal means no normalization.
                    // Only the isolated source can have been touched; its caller
                    // always rolls back the complete temporary transaction group.
                    System.Diagnostics.Trace.WriteLine("Native MEP type reuse skipped: " + failure.Message);
                }
            }
        }
    }
}
