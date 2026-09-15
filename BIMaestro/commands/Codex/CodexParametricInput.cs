using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace BIMaestro.Codex
{
    // Normalize only equivalent notations observed in tool calls. Never invent a
    // dimension, parameter value, scope, formula or permission to make a request pass.
    internal static class CodexParametricInput
    {
        internal static JObject Normalize(JObject input)
        {
            if (input == null) throw new InvalidOperationException("Description JSON attendue.");
            var root = (JObject)input.DeepClone();
            if (root["family_options"] is JObject options)
            {
                foreach (var p in (options["parameters"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    Alias(p, "type", "kind");
                    string name = (string)p["name"], kind = (string)p["kind"];
                    var legacy = (root[kind == "angle" ? "angles" : "parameters"] as JArray)?.OfType<JObject>().FirstOrDefault(v => (string)v["name"] == name);
                    if (p["scope"] != null)
                    {
                        if (p["scope"].Type != JTokenType.String || !new[] { "type", "instance" }.Contains((string)p["scope"]))
                            throw new InvalidOperationException(p.Path + ".scope : type ou instance attendu.");
                        var instance = new JValue((string)p["scope"] == "instance");
                        if (p["instance"] != null && !JToken.DeepEquals(p["instance"], instance)) throw new InvalidOperationException(p.Path + " : instance et scope contradictoires.");
                        p["instance"] = instance; p.Remove("scope");
                    }
                    Default(p, "instance", false); Default(p, "formula", "");
                    var legacyTest = legacy?[kind == "angle" ? "test_value_deg" : "test_value_mm"];
                    Default(p, "test_values", legacyTest == null ? new JArray() : new JArray(legacyTest.DeepClone())); Default(p, "group", "geometry");
                    Default(p, "description", ""); Default(p, "shared_guid", "");
                    if (p["group"].Type == JTokenType.String && string.Equals((string)p["group"], "dimensions", StringComparison.OrdinalIgnoreCase)) p["group"] = "geometry";
                    if (p["value"] == null)
                    {
                        var value = legacy?[kind == "angle" ? "value_deg" : "value_mm"];
                        if (value != null) p["value"] = value.DeepClone();
                        else if (p["formula"].Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)p["formula"]))
                            p["value"] = kind == "yesno" ? new JValue(false) : kind == "text" ? new JValue("") : new JValue(0);
                        // Formula placeholders are replaced by typed evaluation before Revit.
                        // A free parameter with no explicit value is still rejected.
                    }
                }
                foreach (var type in (options["types"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (type["values"] is JObject map)
                        type["values"] = new JArray(map.Properties().Select(v => new JObject { ["parameter"] = v.Name, ["value"] = v.Value.DeepClone() }));
                    foreach (var value in (type["values"] as JArray ?? new JArray()).OfType<JObject>()) Alias(value, "name", "parameter");
                }
                foreach (var display in (options["representations"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    Default(display, "visible_parameter", ""); Default(display, "subcategory", "");
                    foreach (var field in new[] { "coarse", "medium", "fine", "plan", "front_back", "left_right" }) Default(display, field, true);
                }
            }
            foreach (var part in (root["parts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                Coordinates(part, "minimum", new[] { "x", "y", "z" }); Coordinates(part, "maximum", new[] { "x", "y", "z" });
                if (part["profile_uv"] is JArray profile)
                {
                    if (profile.Count == 0) part["profile_uv"] = JValue.CreateNull();
                    else for (int i = 0; i < profile.Count; i++) if (profile[i] is JArray uv)
                        for (int a = 0; a < uv.Count; a++) uv[a] = Expression(uv[a]);
                }
                foreach (var hole in (part["openings"] as JArray ?? new JArray()).OfType<JObject>())
                { Coordinates(hole, "minimum_uv", new[] { "u", "v" }); Coordinates(hole, "maximum_uv", new[] { "u", "v" }); }
            }
            foreach (var array in (root["arrays"] as JArray ?? new JArray()).OfType<JObject>())
            {
                Coordinates(array, "minimum", new[] { "x", "y", "z" }); Coordinates(array, "maximum", new[] { "x", "y", "z" });
                foreach (var field in new[] { "span", "pitch" }) if (array[field] != null) array[field] = Expression(array[field]);
            }
            return root;
        }
        private static void Coordinates(JObject owner, string key, string[] axes)
        {
            if (owner[key] is JObject map)
            { CodexFamilyDesign.Keys(map, axes); owner[key] = new JArray(axes.Select(a => map[a].DeepClone())); }
            if (owner[key] is JArray values) for (int i = 0; i < values.Count; i++) values[i] = Expression(values[i]);
        }
        private static JToken Expression(JToken token)
        {
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return new JObject { ["offset_mm"] = token.DeepClone(), ["terms"] = new JArray() };
            if (token is JObject value && (value["parameter"] != null || value["factor"] != null))
            {
                CodexFamilyDesign.Keys(value, "parameter", "factor", "offset_mm");
                if (value["parameter"].Type != JTokenType.String) throw new InvalidOperationException(value.Path + ".parameter : nom attendu.");
                if ((string)value["parameter"] == "" && CodexFamilyDesign.Scalar(value["factor"], "factor", -1000, 1000) == 0)
                    return new JObject { ["offset_mm"] = value["offset_mm"].DeepClone(), ["terms"] = new JArray() };
                return new JObject { ["offset_mm"] = value["offset_mm"].DeepClone(), ["terms"] = new JArray(new JObject {
                    ["parameter"] = value["parameter"].DeepClone(), ["factor"] = value["factor"].DeepClone() }) };
            }
            return token.DeepClone();
        }
        private static void Default(JObject item, string key, JToken value) { if (item[key] == null) item[key] = value; }
        private static void Alias(JObject item, string oldName, string name)
        {
            if (item[oldName] == null) return;
            if (item[name] != null && !JToken.DeepEquals(item[name], item[oldName])) throw new InvalidOperationException(item.Path + " : " + name + " et " + oldName + " contradictoires.");
            item[name] = item[oldName].DeepClone(); item.Remove(oldName);
        }
    }
}
