using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        public sealed class TypeParameter
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public string DataType { get; set; }
            public string Storage { get; set; }
            public double? Number { get; set; }
            public int? Integer { get; set; }
            public string Text { get; set; }
            public long? ElementId { get; set; }
            public string ReferenceUid { get; set; }
            public string ExclusionReason { get; set; }
            public string Error { get; set; }
            public bool? IsReadOnly { get; set; }
            public bool? HasValue { get; set; }
        }
        public sealed class TypeDefinition
        {
            public string Uid { get; set; }
            public string Class { get; set; }
            public long? CategoryId { get; set; }
            public string Name { get; set; }
            public string FamilyUid { get; set; }
            public string FamilyClass { get; set; }
            public string FamilyName { get; set; }
            public List<TypeParameter> Parameters { get; set; } = new();
            public List<string> Errors { get; set; } = new();
        }
        sealed class DurableTypeReceipt
        {
            public string SourceUid { get; set; }
            public string RootSourceUid { get; set; }
            public string PayloadId { get; set; }
            public string TypeVersion { get; set; }
        }
        readonly Dictionary<string, TypeDefinition> sourceTypeDefinitions = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeDefinition> currentTypeDefinitions = new(StringComparer.Ordinal);
        readonly List<DurableTypeReceipt> durableTypeReceipts = new();

        TypeDefinition ReadTypeDefinition(Element type)
        {
            if (type == null) return null;
            var result = new TypeDefinition { Uid = type.UniqueId, Class = type.GetType().Name,
                CategoryId = type.Category?.Id.Value, Name = Safe(() => type.Name) };
            if (type is FamilySymbol symbol)
            {
                result.FamilyUid = symbol.Family?.UniqueId; result.FamilyClass = symbol.Family?.GetType().Name;
                result.FamilyName = symbol.FamilyName;
            }
            // Capsule family/type labels may be suffixed to keep historical type
            // definitions separate. Preserve these fields in the evidence, while
            // comparing the family identity/provenance and all other parameters.
            var identityNames = new HashSet<long>();
            foreach (var name in new[] { "SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME", "SYMBOL_FAMILY_NAME_PARAM", "ALL_MODEL_FAMILY_NAME", "ALL_MODEL_FAMILY_AND_TYPE_NAMES" })
                if (Enum.TryParse<BuiltInParameter>(name, out var builtIn)) identityNames.Add((long)builtIn);
            try
            {
                foreach (Parameter parameter in type.Parameters)
                {
                    var item = new TypeParameter(); result.Parameters.Add(item);
                    try
                    {
                        item.IsReadOnly = parameter.IsReadOnly; item.HasValue = parameter.HasValue; item.Name = parameter.Definition?.Name; item.DataType = parameter.Definition?.GetDataType()?.TypeId;
                        item.Storage = parameter.StorageType.ToString();
                        item.Key = parameter.Id.Value < 0 ? "builtin:" + parameter.Id.Value
                            : parameter.IsShared ? "shared:" + parameter.GUID.ToString("D")
                            : "name:" + item.Name + "|" + item.DataType + "|" + item.Storage;
                        if (!config.RapidModifiedDelete && identityNames.Contains(parameter.Id.Value)) item.ExclusionReason = "capsule-version-display-name; identity/provenance checked separately";
                        switch (parameter.StorageType)
                        {
                            case StorageType.Double:
                                double value = parameter.AsDouble();
                                if (!double.IsFinite(value)) throw new InvalidOperationException("Non-finite type parameter.");
                                item.Number = value; break;
                            case StorageType.Integer: item.Integer = parameter.AsInteger(); break;
                            case StorageType.String: item.Text = parameter.AsString(); break;
                            case StorageType.ElementId:
                                var id = parameter.AsElementId(); item.ElementId = id.Value;
                                item.ReferenceUid = type.Document.GetElement(id)?.UniqueId; break;
                        }
                    }
                    catch (Exception ex) { item.Error = ex.GetType().Name + ":" + ex.Message; result.Errors.Add(item.Error); }
                }
            }
            catch (Exception ex) { result.Errors.Add("type-parameters:" + ex.GetType().Name + ":" + ex.Message); }
            return result;
        }

        void CaptureSourceTypeDefinitions(Document document)
        {
            sourceTypeDefinitions.Clear();
            foreach (var uid in specs.Values.SelectMany(spec => new[] { spec.TypeUid, spec.SystemTypeUid }
                .Concat(spec.Ports.Select(port => port.SystemTypeUid)).Concat(spec.Ports.SelectMany(port => port.Peers).Select(peer => peer.SystemTypeUid)))
                .Where(uid => uid != null).Distinct())
                sourceTypeDefinitions[uid] = ReadTypeDefinition(document.GetElement(uid));
        }

        object CompareHistoricalSystem(Document document, Spec owner, string sourceUid, string actualUid)
        {
            if (sourceUid == null || actualUid == null)
                return new { SourceTypeUid = sourceUid, ActualTypeUid = actualUid, TypeUidMatchesSource = sourceUid == actualUid,
                    SourceUndefined = sourceUid == null, Passed = sourceUid == actualUid,
                    Note = "An undefined historical system must remain undefined; version provenance never permits assigning one." };
            return CompareTypeDefinition(document, new Spec { Uid = owner.Uid, RootUid = owner.RootUid, TypeUid = sourceUid }, new Spec { TypeUid = actualUid });
        }

        bool DefinitionReferenceMatches(Document document, TypeParameter expected, TypeParameter actual)
        {
            if (expected.ReferenceUid != null)
            {
                if (expected.ReferenceUid == actual.ReferenceUid) return true;
                var target = actual.ReferenceUid == null ? null : document.GetElement(actual.ReferenceUid);
                return !config.RapidModifiedDelete && target != null && Origins(target).Contains(expected.ReferenceUid);
            }
            return actual.ReferenceUid == null && expected.ElementId == actual.ElementId;
        }

        object CompareTypeDefinition(Document document, Spec expected, Spec actual)
        {
            sourceTypeDefinitions.TryGetValue(expected.TypeUid ?? "", out var source);
            var actualType = actual.TypeUid == null ? null : document.GetElement(actual.TypeUid);
            if (actualType != null && !currentTypeDefinitions.ContainsKey(actual.TypeUid))
                currentTypeDefinitions[actual.TypeUid] = ReadTypeDefinition(actualType);
            var current = actual.TypeUid != null && currentTypeDefinitions.TryGetValue(actual.TypeUid, out var found) ? found : null;
            bool uidMatches = expected.TypeUid == actual.TypeUid;
            bool sourceAvailable = expected.TypeUid == null || source != null;
            bool sameDefinitionClass = sourceAvailable && (source == null && current == null
                || source != null && current != null && source.Class == current.Class && source.CategoryId == current.CategoryId
                    && source.FamilyClass == current.FamilyClass);
            bool familyIdentity = source?.FamilyUid == current?.FamilyUid;
            if (!config.RapidModifiedDelete && !familyIdentity && current?.FamilyUid != null && source?.FamilyUid != null)
            {
                var family = document.GetElement(current.FamilyUid);
                familyIdentity = family != null && Origins(family).Contains(source.FamilyUid);
            }
            var differences = new List<string>();
            bool EqualParameter(TypeParameter first, TypeParameter second)
            {
                if (first.Storage != second.Storage || first.DataType != second.DataType || first.Error != null || second.Error != null) return false;
                return first.Storage switch
                {
                    "Double" => first.Number.HasValue && second.Number.HasValue && Math.Abs(first.Number.Value - second.Number.Value) <= 1e-9,
                    "Integer" => first.Integer == second.Integer,
                    "String" => first.Text == second.Text,
                    "ElementId" => DefinitionReferenceMatches(document, first, second),
                    "None" => true,
                    _ => false
                };
            }
            var firstParameters = source?.Parameters.Where(parameter => parameter.ExclusionReason == null).ToList() ?? new List<TypeParameter>();
            var secondParameters = current?.Parameters.Where(parameter => parameter.ExclusionReason == null).ToList() ?? new List<TypeParameter>();
            foreach (var group in firstParameters.GroupBy(parameter => parameter.Key))
            {
                var remaining = secondParameters.Where(parameter => parameter.Key == group.Key).ToList();
                foreach (var parameter in group)
                {
                    int matched = remaining.FindIndex(candidate => EqualParameter(parameter, candidate));
                    if (matched < 0) differences.Add("missing-or-different:" + group.Key);
                    else remaining.RemoveAt(matched);
                }
                if (remaining.Count > 0) differences.Add("extra-parameters:" + group.Key + ":" + remaining.Count);
            }
            foreach (var parameter in secondParameters.Where(parameter => !firstParameters.Any(sourceParameter => sourceParameter.Key == parameter.Key)))
                differences.Add("extra-parameter:" + parameter.Key);
            bool parametersMatch = sourceAvailable && sameDefinitionClass && familyIdentity && differences.Count == 0
                && (source?.Errors.Count ?? 0) == 0 && (current?.Errors.Count ?? 0) == 0;
            if (config.RapidModifiedDelete)
            {
                bool familyUidMatches = source?.FamilyUid == current?.FamilyUid;
                bool exactDefinitions = uidMatches && familyUidMatches && parametersMatch
                    && source?.Name == current?.Name && source?.FamilyName == current?.FamilyName
                    && (expected.TypeUid == null || actualType != null);
                return new
                {
                    UsesExistingModelDefinitions = true, ExistingModelDefinitionsUsed = exactDefinitions,
                    TypeUidMatchesSource = uidMatches, FamilyUidMatchesSource = familyUidMatches,
                    TypeDefinitionParametersMatch = parametersMatch, TypeClassAndCategoryMatch = sameDefinitionClass,
                    SourceDefinitionAvailable = sourceAvailable, ParameterDifferences = differences,
                    SourceTypeUid = source?.Uid, ActualTypeUid = current?.Uid,
                    SourceTypeName = source?.Name, ActualTypeName = current?.Name,
                    SourceFamilyUid = source?.FamilyUid, ActualFamilyUid = current?.FamilyUid,
                    SourceFamilyName = source?.FamilyName, ActualFamilyName = current?.FamilyName,
                    Passed = exactDefinitions,
                    Note = "Existing project definitions require the exact original type and family UIDs, unchanged names and parameters. No historical marker or imported definition can substitute for a missing original definition."
                };
            }
            string marker = null, markerError = null;
            try { marker = actualType == null ? null : Call(native, "ReadHistoricalTypeVersion", actualType)?.ToString(); }
            catch (Exception ex) { markerError = ex.GetType().Name + ":" + ex.Message; }
            bool origin = actualType != null && expected.TypeUid != null && Origins(actualType).Contains(expected.TypeUid);
            var receipts = durableTypeReceipts.Where(receipt => receipt.SourceUid == expected.Uid || receipt.SourceUid == expected.RootUid
                || receipt.RootSourceUid == expected.RootUid && expected.RootUid != null).ToArray();
            bool markerToken = marker?.Length == 64 && marker.All(Uri.IsHexDigit)
                || Guid.TryParse(marker, out var guid) && guid != Guid.Empty;
            bool validVersion = markerToken && receipts.Any(receipt => string.Equals(receipt.TypeVersion, marker, StringComparison.OrdinalIgnoreCase)
                && receipt.PayloadId?.Length == 64 && receipt.PayloadId.All(Uri.IsHexDigit));
            bool historicalProvenance = origin && validVersion && familyIdentity && sameDefinitionClass;
            return new
            {
                TypeUidMatchesSource = uidMatches, HistoricalTypeProvenance = historicalProvenance,
                TypeDefinitionParametersMatch = parametersMatch, HistoricalTypeVersion = marker,
                TypeVersionMatchesResolvedPayload = validVersion, SourceTypeOriginPresent = origin,
                FamilyIdentityOrProvenanceMatches = familyIdentity, TypeClassAndCategoryMatch = sameDefinitionClass,
                SourceDefinitionAvailable = sourceAvailable, ParameterDifferences = differences,
                TypeVersionProbeError = markerError,
                SourceTypeUid = source?.Uid, ActualTypeUid = current?.Uid,
                SourceTypeName = source?.Name, ActualTypeName = current?.Name,
                SourceFamilyUid = source?.FamilyUid, ActualFamilyUid = current?.FamilyUid,
                SourceFamilyName = source?.FamilyName, ActualFamilyName = current?.FamilyName,
                ResolvedReceipts = receipts,
                Passed = markerError == null && parametersMatch && (uidMatches || historicalProvenance),
                Note = "Versioned types require both source provenance and a version matching a payload resolved by the real production loader. Display labels are retained but excluded from parameter comparison; all other unknown/mismatching type data fails."
            };
        }
    }
}
