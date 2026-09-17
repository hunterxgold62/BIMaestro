using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIMaestro.Codex
{
    internal sealed class FamilyParameterSpec
    {
        internal string Name, Kind, Group, Description, SharedGuid;
        internal bool Instance; internal FamilyValue Value; internal FamilyFormula Formula;
        internal readonly List<FamilyValue> Tests = new List<FamilyValue>();
        internal FamilyValue Read(JToken token)
        {
            if (Kind == "yesno") { if (token?.Type != JTokenType.Boolean) throw new InvalidOperationException(Name + " exige true/false."); return FamilyValue.Bool((bool)token); }
            if (Kind == "text") { if (token?.Type != JTokenType.String || ((string)token).Length > 500) throw new InvalidOperationException(Name + " exige un texte de 500 caractères maximum."); return FamilyValue.String((string)token); }
            double n = CodexFamilyDesign.Scalar(token, Name, -100000, 100000);
            if (Kind == "integer" && n != Math.Truncate(n)) throw new InvalidOperationException(Name + " exige un entier.");
            return FamilyValue.Numeric(n, Kind == "length" ? 1 : 0, Kind == "angle" ? 1 : 0);
        }
        internal void Validate(FamilyValue value)
        {
            bool valid = Kind == "yesno" ? value.Kind == "yesno" : Kind == "text" ? value.Kind == "text" :
                value.Kind == "numeric" && value.LengthPower == (Kind == "length" ? 1 : 0) && value.AnglePower == (Kind == "angle" ? 1 : 0);
            if (!valid || Kind == "integer" && value.Number != Math.Truncate(value.Number)) throw new InvalidOperationException("Type ou unité de formule incorrect pour " + Name);
        }
    }
    internal sealed class FamilyTypeSpec
    {
        internal string Name;
        internal Dictionary<string, FamilyValue> Overrides = new Dictionary<string, FamilyValue>();
    }
    internal sealed class FamilyDisplaySpec
    {
        internal string Component, VisibleParameter, Subcategory;
        internal bool Coarse = true, Medium = true, Fine = true, Plan = true, Front = true, Side = true;
    }
    internal sealed class FamilyCase
    {
        internal string Name, TypeName;
        internal Dictionary<string, FamilyValue> Values;
        internal Dictionary<string, double> Numeric => Values.Where(p => p.Value.Kind != "text").ToDictionary(p => p.Key, p => p.Value.Kind == "yesno" ? (p.Value.Boolean ? 1d : 0d) : p.Value.Number);
    }
    internal sealed class CodexFamilyParameters
    {
        internal readonly List<FamilyParameterSpec> Parameters = new List<FamilyParameterSpec>();
        internal readonly List<FamilyTypeSpec> Types = new List<FamilyTypeSpec>();
        internal readonly List<FamilyDisplaySpec> Displays = new List<FamilyDisplaySpec>();
        internal IEnumerable<FamilyParameterSpec> Lengths => Parameters.Where(p => p.Kind == "length");
        internal IEnumerable<FamilyParameterSpec> Angles => Parameters.Where(p => p.Kind == "angle");
        internal Dictionary<string, FamilyValue> Initial => Evaluate(Types[0].Overrides);
        internal FamilyParameterSpec Find(string name) => Parameters.SingleOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("Paramètre inconnu : " + name);
        internal static JObject Schema()
        {
            JObject Text(int max) => new JObject { ["type"] = "string", ["maxLength"] = max };
            JObject Bool() => new JObject { ["type"] = "boolean" };
            JObject List(JObject item, int max) => new JObject { ["type"] = "array", ["items"] = item, ["maxItems"] = max };
            JObject Obj(JObject properties) => new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(properties.Properties().Select(p => p.Name)), ["additionalProperties"] = false };
            var value = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "number" }, Bool(), Text(500)) };
            var parameter = Obj(new JObject { ["name"] = Text(40), ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("length", "angle", "integer", "number", "yesno", "text") },
                ["instance"] = Bool(), ["value"] = value.DeepClone(), ["test_values"] = List((JObject)value.DeepClone(), 8), ["formula"] = Text(2000),
                ["group"] = new JObject { ["type"] = "string", ["enum"] = new JArray("geometry", "constraints", "visibility", "identity", "data") }, ["description"] = Text(250), ["shared_guid"] = Text(36) });
            parameter["description"] = "Réglage supplémentaire OU définition enrichie d'une longueur/angle existant (même nom et unité). Valeurs en mm/degrés. formula vide pour un réglage libre, sinon expression native typée : + - * /, comparaisons, if/and/or/not, arrondis, trigonométrie. Noms ASCII ; littéraux mm ou deg. test_values explicites, notamment de part et d'autre des seuils. shared_guid vide sauf GUID fourni par l'utilisateur pour étiquettes/nomenclatures.";
            var display = Obj(new JObject { ["component"] = Text(70), ["visible_parameter"] = Text(40), ["subcategory"] = Text(70),
                ["coarse"] = Bool(), ["medium"] = Bool(), ["fine"] = Bool(), ["plan"] = Bool(), ["front_back"] = Bool(), ["left_right"] = Bool() });
            var type = Obj(new JObject { ["name"] = Text(70), ["values"] = List(Obj(new JObject { ["parameter"] = Text(40), ["value"] = value.DeepClone() }), 64) });
            return Obj(new JObject { ["parameters"] = List(parameter, 64), ["types"] = List(type, 16), ["representations"] = List(display, 100) });
        }
        internal static CodexFamilyParameters Parse(JObject source)
        {
            if (source["family_options"] == null || source["family_options"].Type == JTokenType.Null) return null;
            var options = source["family_options"] as JObject;
            CodexFamilyDesign.Keys(options, "parameters", "types", "representations");
            var result = new CodexFamilyParameters();
            foreach (var p in CodexFamilyDesign.Items(source, "parameters", 0, 64).OfType<JObject>())
                result.Parameters.Add(new FamilyParameterSpec { Name = (string)p["name"], Kind = "length", Group = "geometry", Description = "Dimension en millimètres.",
                    Value = FamilyValue.Numeric(CodexFamilyDesign.Scalar(p["value_mm"], "value_mm", 1, 100000), 1),
                    Tests = { FamilyValue.Numeric(CodexFamilyDesign.Scalar(p["test_value_mm"], "test_value_mm", 1, 100000), 1) } });
            foreach (var p in (source["angles"] as JArray ?? new JArray()).OfType<JObject>())
                result.Parameters.Add(new FamilyParameterSpec { Name = (string)p["name"], Kind = "angle", Group = "geometry", Description = "Angle en degrés.",
                    Value = FamilyValue.Numeric(CodexFamilyDesign.Scalar(p["value_deg"], "value_deg", 0, 180), 0, 1),
                    Tests = { FamilyValue.Numeric(CodexFamilyDesign.Scalar(p["test_value_deg"], "test_value_deg", 0, 180), 0, 1) } });
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in CodexFamilyDesign.Items(options, "parameters", 0, 64))
            {
                var p = token as JObject; CodexFamilyDesign.Keys(p, "name", "kind", "instance", "value", "test_values", "formula", "group", "description", "shared_guid");
                var item = new FamilyParameterSpec { Name = (string)p["name"], Kind = (string)p["kind"], Group = (string)p["group"], Description = (string)p["description"], SharedGuid = (string)p["shared_guid"] };
                if (item.Name == null || !Regex.IsMatch(item.Name, "^[A-Za-z][A-Za-z0-9_]{0,39}$") || item.Name.StartsWith("BIM_", StringComparison.OrdinalIgnoreCase) || !declared.Add(item.Name)) throw new InvalidOperationException("Nom de paramètre invalide ou dupliqué : " + item.Name);
                if (!new[] { "length", "angle", "integer", "number", "yesno", "text" }.Contains(item.Kind) || !new[] { "geometry", "constraints", "visibility", "identity", "data" }.Contains(item.Group)) throw new InvalidOperationException("Type/groupe de paramètre inconnu : " + item.Name);
                if (p["instance"].Type != JTokenType.Boolean || p["description"].Type != JTokenType.String || item.Description.Length > 250 || p["shared_guid"].Type != JTokenType.String) throw new InvalidOperationException("Métadonnées de paramètre invalides.");
                item.Instance = (bool)p["instance"];
                if (item.SharedGuid.Length > 0 && (!Guid.TryParse(item.SharedGuid, out var guid) || guid == Guid.Empty)) throw new InvalidOperationException("GUID partagé invalide : " + item.Name);
                item.Value = item.Read(p["value"]);
                foreach (var test in CodexFamilyDesign.Items(p, "test_values", 0, 8)) item.Tests.Add(item.Read(test));
                if (p["formula"].Type != JTokenType.String) throw new InvalidOperationException("Formule texte attendue.");
                if (!string.IsNullOrWhiteSpace((string)p["formula"])) item.Formula = FamilyFormula.Parse((string)p["formula"]);
                var previous = result.Parameters.FirstOrDefault(v => v.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (previous != null)
                { if (previous.Name != item.Name || previous.Kind != item.Kind) throw new InvalidOperationException("Redéfinition incompatible : " + item.Name); result.Parameters.Remove(previous); }
                result.Parameters.Add(item);
            }
            if (result.Parameters.Count > 64 || result.Parameters.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Parameters.Count) throw new InvalidOperationException("Maximum 64 paramètres distincts.");
            foreach (var parameter in result.Parameters)
                if (parameter.Formula != null)
                    foreach (var name in parameter.Formula.Dependencies)
                    { var dependency = result.Find(name); if (!parameter.Instance && dependency.Instance) throw new InvalidOperationException("Un paramètre de type ne peut dépendre d'une occurrence : " + parameter.Name + " → " + name); }
            var visited = new HashSet<string>(); var stack = new HashSet<string>();
            void ValidateGraph(FamilyParameterSpec p)
            {
                if (visited.Contains(p.Name)) return;
                if (!stack.Add(p.Name)) throw new InvalidOperationException("Dépendance circulaire de formule : " + p.Name);
                if (p.Formula != null)
                { foreach (var name in p.Formula.Dependencies) ValidateGraph(result.Find(name)); p.Validate(p.Formula.Unit(name => result.Find(name).Value)); }
                stack.Remove(p.Name); visited.Add(p.Name);
            }
            foreach (var parameter in result.Parameters) ValidateGraph(parameter);
            // Automatically exercise boolean switches and direct threshold comparisons.
            // More complex calculated thresholds still require explicit test_values.
            foreach (var p in result.Parameters.Where(p => p.Formula == null && p.Kind == "yesno"))
                if (!p.Tests.Any(v => v.Boolean != p.Value.Boolean)) p.Tests.Add(FamilyValue.Bool(!p.Value.Boolean));
            foreach (var node in result.Parameters.Where(p => p.Formula != null).SelectMany(p => p.Formula.Nodes).Where(n => new[] { "<", ">", "<=", ">=", "=" }.Contains(n.Op)))
            {
                var variable = node.Children[0].Name != null ? node.Children[0] : node.Children[1];
                var literal = ReferenceEquals(variable, node.Children[0]) ? node.Children[1].Literal : node.Children[0].Literal;
                if (variable.Name == null || literal?.Kind != "numeric") continue;
                var p = result.Find(variable.Name); if (p.Formula != null || !p.Value.SameUnit(literal)) continue;
                double step = p.Kind == "number" ? 0.001 : 1;
                var thresholds = p.Kind == "integer" ? new[] { Math.Floor(literal.Number) - 1, Math.Floor(literal.Number), Math.Ceiling(literal.Number), Math.Ceiling(literal.Number) + 1 } : new[] { literal.Number - step, literal.Number, literal.Number + step };
                foreach (double threshold in thresholds)
                    if (Math.Abs(threshold) <= 100000 && (p.Kind != "angle" || threshold >= 0 && threshold <= 180) && !p.Tests.Any(v => v.Number == threshold))
                        p.Tests.Add(FamilyValue.Numeric(threshold, literal.LengthPower, literal.AnglePower));
            }
            foreach (var token in CodexFamilyDesign.Items(options, "types", 0, 16))
            {
                var t = token as JObject; CodexFamilyDesign.Keys(t, "name", "values");
                var item = new FamilyTypeSpec { Name = CodexFamilyDesign.String(t, "name", 70) };
                if (result.Types.Any(v => v.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Type de famille dupliqué.");
                foreach (var tokenValue in CodexFamilyDesign.Items(t, "values", 0, 64))
                {
                    var v = tokenValue as JObject; CodexFamilyDesign.Keys(v, "parameter", "value");
                    var parameter = result.Find((string)v["parameter"]);
                    if (parameter.Formula != null || item.Overrides.ContainsKey(parameter.Name)) throw new InvalidOperationException("Valeur de type dupliquée ou pilotée par formule : " + parameter.Name);
                    item.Overrides.Add(parameter.Name, parameter.Read(v["value"]));
                }
                result.Types.Add(item);
            }
            if (result.Types.Count == 0) result.Types.Add(new FamilyTypeSpec { Name = "Standard" });
            foreach (var token in CodexFamilyDesign.Items(options, "representations", 0, 100))
            {
                var v = token as JObject; CodexFamilyDesign.Keys(v, "component", "visible_parameter", "subcategory", "coarse", "medium", "fine", "plan", "front_back", "left_right");
                foreach (var key in new[] { "coarse", "medium", "fine", "plan", "front_back", "left_right" }) if (v[key].Type != JTokenType.Boolean) throw new InvalidOperationException("Réglage de visibilité booléen attendu.");
                var item = new FamilyDisplaySpec { Component = CodexFamilyDesign.String(v, "component", 70), VisibleParameter = (string)v["visible_parameter"], Subcategory = (string)v["subcategory"],
                    Coarse = (bool)v["coarse"], Medium = (bool)v["medium"], Fine = (bool)v["fine"], Plan = (bool)v["plan"], Front = (bool)v["front_back"], Side = (bool)v["left_right"] };
                if (item.VisibleParameter == null || item.Subcategory == null || item.Subcategory.Length > 70 || result.Displays.Any(d => d.Component == item.Component)) throw new InvalidOperationException("Représentation invalide ou dupliquée.");
                if (item.VisibleParameter.Length > 0 && result.Find(item.VisibleParameter).Kind != "yesno") throw new InvalidOperationException("La visibilité exige un paramètre Oui/Non.");
                if (!item.Coarse && !item.Medium && !item.Fine) throw new InvalidOperationException("Au moins un niveau de détail doit être visible.");
                result.Displays.Add(item);
            }
            if (result.Types.Count * (2 + result.Parameters.Where(p => p.Formula == null).Sum(p => p.Tests.Count)) > 128)
                throw new InvalidOperationException("Maximum 128 scénarios natifs par famille. Réduire les types ou les essais redondants.");
            if (result.Parameters.Where(p => !string.IsNullOrEmpty(p.SharedGuid)).GroupBy(p => Guid.Parse(p.SharedGuid)).Any(g => g.Count() > 1)) throw new InvalidOperationException("Un même GUID partagé ne peut identifier deux paramètres.");
            foreach (var test in result.Cases()) { /* Validate every explicit scenario before entering Revit. */ }
            return result;
        }
        internal Dictionary<string, FamilyValue> Evaluate(Dictionary<string, FamilyValue> overrides)
        {
            var values = new Dictionary<string, FamilyValue>(); var visiting = new HashSet<string>();
            FamilyValue Get(string name)
            {
                if (values.TryGetValue(name, out var cached)) return cached;
                var p = Find(name); if (!visiting.Add(name)) throw new InvalidOperationException("Dépendance circulaire de formule : " + name);
                var value = p.Formula != null ? p.Formula.Evaluate(Get) : overrides != null && overrides.TryGetValue(name, out var supplied) ? supplied : p.Value;
                p.Validate(value); values.Add(name, value); visiting.Remove(name); return value;
            }
            foreach (var p in Parameters) Get(p.Name);
            return values;
        }
        internal IEnumerable<FamilyCase> Cases()
        {
            foreach (var type in Types)
            {
                yield return new FamilyCase { Name = type.Name + " — nominal", TypeName = type.Name, Values = Evaluate(type.Overrides) };
                foreach (var p in Parameters.Where(p => p.Formula == null))
                    foreach (var value in p.Tests)
                    { var changes = new Dictionary<string, FamilyValue>(type.Overrides) { [p.Name] = value }; yield return new FamilyCase { Name = type.Name + " — " + p.Name, TypeName = type.Name, Values = Evaluate(changes) }; }
                var combined = new Dictionary<string, FamilyValue>(type.Overrides);
                foreach (var p in Parameters.Where(p => p.Formula == null && p.Tests.Count > 0)) combined[p.Name] = p.Tests.Last();
                yield return new FamilyCase { Name = type.Name + " — combiné", TypeName = type.Name, Values = Evaluate(combined) };
            }
        }
    }
}
