using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilyParameterEdit
    {
        internal string Name, Kind, Group, Mode, Formula, SharedGuid;
        internal bool Instance;
        internal JToken Value;
    }
    internal sealed class FamilyBindingEdit { internal string Element, Property, FamilyParameter; }
    internal sealed class FamilyConfigurationEdit
    {
        internal string DocumentKey; internal bool ReplaceAssociations;
        internal string[] TypeNames;
        internal List<FamilyParameterEdit> Parameters = new List<FamilyParameterEdit>();
        internal List<FamilyBindingEdit> Bindings = new List<FamilyBindingEdit>();
        internal static JObject Properties()
        {
            JObject Text(int max) => new JObject { ["type"] = "string", ["maxLength"] = max };
            JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
            JObject Obj(JObject p) => new JObject { ["type"] = "object", ["properties"] = p, ["required"] = new JArray(p.Properties().Select(x => x.Name)), ["additionalProperties"] = false };
            JObject Array(JObject item, int max) => new JObject { ["type"] = "array", ["maxItems"] = max, ["items"] = item };
            return new JObject {
                ["document_key"] = Text(100), ["replace_associations"] = new JObject { ["type"] = "boolean" },
                ["type_names"] = Array(Text(100), 64),
                ["parameters"] = Array(Obj(new JObject {
                    ["name"] = Text(100), ["mode"] = Enum("add", "reuse", "update"),
                    ["kind"] = Enum("length", "angle", "integer", "number", "yesno", "text", "material"),
                    ["instance"] = new JObject { ["type"] = "boolean" },
                    ["group"] = Enum("visibility", "geometry", "constraints", "identity", "data", "materials"),
                    ["shared_guid"] = Text(36),
                    ["formula"] = new JObject { ["anyOf"] = new JArray(Text(2000), new JObject { ["type"] = "null" }) },
                    ["value"] = new JObject { ["anyOf"] = new JArray(Text(2000), new JObject { ["type"] = "number" }, new JObject { ["type"] = "boolean" }, new JObject { ["type"] = "null" }) }
                }), 64),
                ["bindings"] = Array(Obj(new JObject { ["element_unique_id"] = Text(100), ["property"] = Text(100), ["family_parameter"] = Text(100) }), 750) };
        }
        internal static FamilyConfigurationEdit Parse(JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "replace_associations", "type_names", "parameters", "bindings");
            if (args["replace_associations"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("replace_associations doit être un booléen.");
            var result = new FamilyConfigurationEdit { DocumentKey = CodexFamilyDesign.String(args, "document_key", 100), ReplaceAssociations = (bool)args["replace_associations"],
                TypeNames = CodexFamilyDesign.Items(args, "type_names", 0, 64).Select(t => CodexFamilyDesign.String(new JObject { ["name"] = t.DeepClone() }, "name", 100)).ToArray() };
            if (result.TypeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.TypeNames.Length) throw new InvalidOperationException("Types dupliqués.");
            foreach (JToken token in CodexFamilyDesign.Items(args, "parameters", 0, 64))
            {
                var p = token as JObject; CodexFamilyDesign.Keys(p, "name", "mode", "kind", "instance", "group", "shared_guid", "formula", "value");
                if (p["instance"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("instance doit être un booléen.");
                var spec = new FamilyParameterEdit { Name = CodexFamilyDesign.String(p, "name", 100), Kind = CodexFamilyDesign.String(p, "kind", 20),
                    Mode = CodexFamilyDesign.String(p, "mode", 10), Group = CodexFamilyDesign.String(p, "group", 20), Instance = (bool)p["instance"],
                    SharedGuid = Text(p["shared_guid"], 36), Formula = p["formula"]?.Type == JTokenType.Null ? null : Text(p["formula"], 2000), Value = p["value"] };
                if (!new[] { "length", "angle", "integer", "number", "yesno", "text", "material" }.Contains(spec.Kind) ||
                    !new[] { "add", "reuse", "update" }.Contains(spec.Mode) || !new[] { "visibility", "geometry", "constraints", "identity", "data", "materials" }.Contains(spec.Group))
                    throw new InvalidOperationException("Type, mode ou groupe inconnu : " + spec.Name);
                if (spec.SharedGuid.Length != 0 && (!Guid.TryParseExact(spec.SharedGuid, "D", out var guid) || guid == Guid.Empty)) throw new InvalidOperationException("GUID partagé invalide.");
                if (spec.Value == null) throw new InvalidOperationException("value est requis ; null conserve la valeur.");
                if (spec.Mode == "reuse" && (spec.Formula != null || spec.Value.Type != JTokenType.Null)) throw new InvalidOperationException("reuse conserve les valeurs et formules : fournir null pour les deux.");
                if (!string.IsNullOrEmpty(spec.Formula) && spec.Value.Type != JTokenType.Null) throw new InvalidOperationException("Une formule exige value=null.");
                if (spec.Mode == "add" && string.IsNullOrEmpty(spec.Formula) && spec.Value.Type == JTokenType.Null) throw new InvalidOperationException("Un nouveau paramètre exige une valeur initiale ou une formule.");
                if (spec.Value.Type != JTokenType.Null)
                {
                    if (spec.Kind == "yesno") { if (spec.Value.Type != JTokenType.Boolean) throw new InvalidOperationException("Valeur Oui/Non attendue."); }
                    else if (spec.Kind == "text" || spec.Kind == "material") Text(spec.Value, 2000);
                    else
                    {
                        double n = CodexFamilyDesign.Scalar(spec.Value, spec.Name, -1000000000, 1000000000);
                        if (spec.Kind == "integer" && n != Math.Truncate(n)) throw new InvalidOperationException("Valeur entière attendue.");
                        if (spec.Kind == "length" && Math.Abs(n) > 100000 || spec.Kind == "angle" && (n < 0 || n > 180)) throw new InvalidOperationException("Valeur hors limites.");
                    }
                }
                if (result.Parameters.Any(x => x.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Paramètre dupliqué : " + spec.Name);
                result.Parameters.Add(spec);
            }
            foreach (var token in CodexFamilyDesign.Items(args, "bindings", 0, 750))
            {
                var b = token as JObject; CodexFamilyDesign.Keys(b, "element_unique_id", "property", "family_parameter");
                var spec = new FamilyBindingEdit { Element = CodexFamilyDesign.String(b, "element_unique_id", 100), Property = CodexFamilyDesign.String(b, "property", 100), FamilyParameter = Text(b["family_parameter"], 100).Trim() };
                if (spec.FamilyParameter.Length == 0 && !result.ReplaceAssociations) throw new InvalidOperationException("Dissocier exige replace_associations=true.");
                if (result.Bindings.Any(x => x.Element == spec.Element && x.Property == spec.Property)) throw new InvalidOperationException("Association dupliquée.");
                result.Bindings.Add(spec);
            }
            if (result.Parameters.Count + result.Bindings.Count + result.TypeNames.Length == 0) throw new InvalidOperationException("Configuration vide.");
            return result;
        }
        private static string Text(JToken token, int max)
        {
            if (token?.Type != JTokenType.String || ((string)token).Length > max) throw new InvalidOperationException("Texte absent ou trop long.");
            return (string)token;
        }
    }
    // Requests are validated independently of Revit, before any transaction.
    internal sealed class FamilyRepresentationEdit
    {
        internal string DocumentKey, Group, Action;
        internal FamilyRepresentationSpec Representation;
        internal static JObject Properties() => new JObject {
            ["document_key"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["group_name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
            ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("add", "replace", "remove") },
            ["representation_2d"] = FamilyRepresentationSpec.Schema() };
        internal static FamilyRepresentationEdit Parse(JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "group_name", "action", "representation_2d");
            var result = new FamilyRepresentationEdit {
                DocumentKey = CodexFamilyDesign.String(args, "document_key", 100),
                Group = CodexFamilyDesign.String(args, "group_name", 70),
                Action = CodexFamilyDesign.String(args, "action", 10),
                Representation = FamilyRepresentationSpec.Parse(args["representation_2d"]) };
            if (result.Action != "add" && result.Action != "replace" && result.Action != "remove")
                throw new InvalidOperationException("Action de représentation inconnue.");
            if ((result.Action == "remove") != (result.Representation == null))
                throw new InvalidOperationException("representation_2d doit être null uniquement pour remove.");
            if (result.Representation != null && result.Representation.HideModelIn.Length != 0)
                throw new InvalidOperationException("En édition, hide_model_in doit être [] : la visibilité 3D existante est conservée.");
            return result;
        }
    }
    internal sealed class FamilyExtrusionEdit
    {
        internal string DocumentKey, UniqueId;
        internal double Start, End;
        internal static JObject Properties() => new JObject {
            ["document_key"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["element_unique_id"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
            ["start_mm"] = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 },
            ["end_mm"] = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 } };
        internal static FamilyExtrusionEdit Parse(JObject args)
        {
            CodexFamilyDesign.Keys(args, "document_key", "element_unique_id", "start_mm", "end_mm");
            var result = new FamilyExtrusionEdit {
                DocumentKey = CodexFamilyDesign.String(args, "document_key", 100),
                UniqueId = CodexFamilyDesign.String(args, "element_unique_id", 100),
                Start = CodexFamilyDesign.Scalar(args["start_mm"], "start_mm", -100000, 100000),
                End = CodexFamilyDesign.Scalar(args["end_mm"], "end_mm", -100000, 100000) };
            if (result.End - result.Start < 1) throw new InvalidOperationException("La profondeur d'extrusion doit être au moins égale à 1 mm.");
            return result;
        }
    }
}
