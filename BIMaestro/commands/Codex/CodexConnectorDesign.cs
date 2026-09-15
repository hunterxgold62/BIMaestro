using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilyConnectorSpec
    {
        internal string Name, Component, Domain, System, Shape, Width, Height, Diameter, Flow;
        internal int Axis, Sign;
        internal static JObject Schema()
        {
            JObject Text() => new JObject { ["type"] = "string", ["maxLength"] = 70 };
            JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
            var props = new JObject { ["name"] = Text(), ["component"] = Text(), ["face"] = Enum("x_min", "x_max", "y_min", "y_max", "z_min", "z_max"),
                ["domain"] = Enum("duct", "pipe", "electrical"), ["system"] = Text(), ["shape"] = Enum("round", "rectangular", "electrical"),
                ["width_parameter"] = Text(), ["height_parameter"] = Text(), ["diameter_parameter"] = Text(), ["flow"] = Enum("in", "out", "bidirectional") };
            return new JObject { ["type"] = "array", ["maxItems"] = 16, ["items"] = new JObject { ["type"] = "object", ["properties"] = props,
                ["required"] = new JArray(props.Properties().Select(p => p.Name)), ["additionalProperties"] = false },
                ["description"] = "Connecteurs natifs au CENTRE d'une face externe d'une pièce parts sans ouvertures. Dimensions liées aux paramètres de longueur. Duct systems: supply/return/exhaust/other ; pipe: cold_water/hot_water/sanitary/supply_hydronic/return_hydronic/other ; electrical: power/data/communication. Paramètres non applicables = chaîne vide. Direction géométrique vers l'extérieur ; flow indique le flux. Catégorie adaptée obligatoire. Pas de dimensionnement métier inventé." };
        }
        internal static List<FamilyConnectorSpec> Parse(JObject source, CodexParametricDesign design)
        {
            var result = new List<FamilyConnectorSpec>();
            if (source["connectors"] == null) return result;
            foreach (var token in CodexFamilyDesign.Items(source, "connectors", 0, 16))
            {
                var p = token as JObject; CodexFamilyDesign.Keys(p, "name", "component", "face", "domain", "system", "shape", "width_parameter", "height_parameter", "diameter_parameter", "flow");
                var item = new FamilyConnectorSpec { Name = CodexFamilyDesign.String(p, "name", 70), Component = CodexFamilyDesign.String(p, "component", 70),
                    Domain = (string)p["domain"], System = (string)p["system"], Shape = (string)p["shape"], Width = (string)p["width_parameter"], Height = (string)p["height_parameter"], Diameter = (string)p["diameter_parameter"], Flow = (string)p["flow"] };
                string face = (string)p["face"];
                if (!new[] { "x_min", "x_max", "y_min", "y_max", "z_min", "z_max" }.Contains(face)) throw new InvalidOperationException("Face de raccordement inconnue.");
                item.Axis = "xyz".IndexOf(face[0]); item.Sign = face.EndsWith("min", StringComparison.Ordinal) ? -1 : 1;
                if (result.Any(c => c.Name == item.Name)) throw new InvalidOperationException("Nom de connecteur dupliqué.");
                var part = design.Parts.SingleOrDefault(c => c.Name == item.Component);
                if (part == null || part.Openings.Count > 0 || part.Profile != null) throw new InvalidOperationException("Le connecteur exige une pièce parts rectangulaire sans ouverture : " + item.Component);
                if (!new[] { "in", "out", "bidirectional" }.Contains(item.Flow)) throw new InvalidOperationException("Direction de flux invalide.");
                var systems = item.Domain == "duct" ? new[] { "supply", "return", "exhaust", "other" } : item.Domain == "pipe" ? new[] { "cold_water", "hot_water", "sanitary", "supply_hydronic", "return_hydronic", "other" } : item.Domain == "electrical" ? new[] { "power", "data", "communication" } : new string[0];
                if (!systems.Contains(item.System)) throw new InvalidOperationException("Système de connecteur non pris en charge.");
                if (item.Domain == "duct" && !new[] { "mechanical", "air_terminal" }.Contains(design.Metadata.Category) || item.Domain == "pipe" && !new[] { "mechanical", "plumbing" }.Contains(design.Metadata.Category) || item.Domain == "electrical" && !new[] { "electrical", "mechanical", "lighting" }.Contains(design.Metadata.Category)) throw new InvalidOperationException("Catégorie incompatible avec le connecteur.");
                if (item.Domain == "electrical" ? item.Shape != "electrical" : item.Domain == "pipe" ? item.Shape != "round" : !new[] { "round", "rectangular" }.Contains(item.Shape)) throw new InvalidOperationException("Section de connecteur incompatible.");
                foreach (string name in item.Shape == "rectangular" ? new[] { item.Width, item.Height } : item.Shape == "round" ? new[] { item.Diameter } : new string[0])
                    if (!design.Parameters.Any(v => v.Name == name)) throw new InvalidOperationException("Paramètre de longueur de raccordement absent : " + name);
                foreach (var values in design.TestCases())
                    foreach (string name in item.Shape == "rectangular" ? new[] { item.Width, item.Height } : item.Shape == "round" ? new[] { item.Diameter } : new string[0])
                        if (values[name] < 1) throw new InvalidOperationException("Dimension de raccordement inférieure à 1 mm.");
                result.Add(item);
            }
            return result;
        }
    }
}
