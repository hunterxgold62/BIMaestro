using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilySymbolicSpec
    {
        internal string Component, VisibleParameter; internal int Axis; internal bool Coarse, Medium, Fine;
        internal static JObject Schema()
        {
            var properties = new JObject { ["component"] = new JObject { ["type"] = "string", ["maxLength"] = 70 },
                ["plane"] = new JObject { ["type"] = "string", ["enum"] = new JArray("xy", "xz", "yz") },
                ["visible_parameter"] = new JObject { ["type"] = "string", ["maxLength"] = 40 },
                ["coarse"] = new JObject { ["type"] = "boolean" }, ["medium"] = new JObject { ["type"] = "boolean" }, ["fine"] = new JObject { ["type"] = "boolean" } };
            return new JObject { ["type"] = "array", ["maxItems"] = 40, ["items"] = new JObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = new JArray(properties.Properties().Select(p => p.Name)), ["additionalProperties"] = false },
                ["description"] = "Contours 2D symboliques RECTANGULAIRES contraints, pilotés par les limites XYZ de la pièce parts indiquée. Projection extérieure uniquement, sans traits cachés ni masquage automatique. Plan XY, élévation XZ ou YZ, à l'origine du plan. visible_parameter = nom Oui/Non ou chaîne vide." };
        }
        internal static List<FamilySymbolicSpec> Parse(JObject source, CodexParametricDesign design)
        {
            var result = new List<FamilySymbolicSpec>();
            if (source["symbolic_outlines"] == null) return result;
            foreach (var token in CodexFamilyDesign.Items(source, "symbolic_outlines", 0, 40))
            {
                var value = token as JObject; CodexFamilyDesign.Keys(value, "component", "plane", "visible_parameter", "coarse", "medium", "fine");
                string plane = (string)value["plane"];
                if (!new[] { "xy", "xz", "yz" }.Contains(plane)) throw new InvalidOperationException("Plan symbolique inconnu.");
                var spec = new FamilySymbolicSpec { Component = CodexFamilyDesign.String(value, "component", 70), Axis = plane == "xy" ? 2 : plane == "xz" ? 1 : 0,
                    VisibleParameter = (string)value["visible_parameter"] };
                foreach (var key in new[] { "coarse", "medium", "fine" }) if (value[key].Type != JTokenType.Boolean) throw new InvalidOperationException("Niveau de détail Oui/Non attendu.");
                spec.Coarse = (bool)value["coarse"]; spec.Medium = (bool)value["medium"]; spec.Fine = (bool)value["fine"];
                if (!design.Parts.Any(p => p.Name == spec.Component) || spec.VisibleParameter == null || !spec.Coarse && !spec.Medium && !spec.Fine) throw new InvalidOperationException("Contour symbolique invalide.");
                if (spec.VisibleParameter.Length > 0 && (design.Registry == null || design.Registry.Find(spec.VisibleParameter).Kind != "yesno")) throw new InvalidOperationException("La visibilité symbolique exige un paramètre Oui/Non.");
                if (result.Any(p => p.Component == spec.Component && p.Axis == spec.Axis)) throw new InvalidOperationException("Contour symbolique dupliqué.");
                result.Add(spec);
            }
            return result;
        }
    }
}
