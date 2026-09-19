using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilyHostOpeningSpec
    {
        internal LengthExpression[] Min, Max;
        internal bool IsFloor;
        internal IEnumerable<LengthExpression> Expressions => Min.Concat(Max);

        internal static JObject Schema()
        {
            var term = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("parameter", "factor"), ["properties"] = new JObject {
                    ["parameter"] = new JObject { ["type"] = "string", ["maxLength"] = 40 },
                    ["factor"] = new JObject { ["type"] = "number", ["minimum"] = -1000, ["maximum"] = 1000 } } };
            var expression = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("offset_mm", "terms"), ["properties"] = new JObject {
                    ["offset_mm"] = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 },
                    ["terms"] = new JObject { ["type"] = "array", ["maxItems"] = 8, ["items"] = term } } };
            var vector = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = expression };
            return new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "null" },
                new JObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JArray("min_xz", "max_xz"), ["properties"] = new JObject {
                        ["min_xz"] = vector, ["max_xz"] = vector.DeepClone() } },
                new JObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JArray("min_xyz", "max_xyz"), ["properties"] = new JObject {
                        ["min_xyz"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = expression.DeepClone() },
                        ["max_xyz"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = expression.DeepClone() } } }),
                ["description"] = "Découpe rectangulaire de l'hôte. hosting=wall : min_xz/max_xz [X,Z], baie native traversante, obligatoire pour door/window. hosting=floor : min_xyz/max_xyz [X,Y,Z], vide d'extrusion non attaché avec Couper avec des vides au chargement ; prévoir une profondeur Z couvrant le sol. Après placement, sélectionner le sol et l'instance puis appeler revit_cut_floor_with_family (ou Couper la géométrie dans Revit) : pas de découpe automatique à la pose. Coordonnées locales en mm, chaque coordonnée={offset_mm,terms:[{parameter,factor}]}. Famille fixe : terms=[] ; paramétrique : paramètres de longueur pour piloter la découpe. null sans découpe. Autres hôtes et contours courbes non pris en charge." };
        }

        internal static FamilyHostOpeningSpec Parse(JObject source, string hosting, string category, HashSet<string> names)
        {
            var token = source["host_opening"];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (hosting == "wall" && (category == "door" || category == "window"))
                    throw new InvalidOperationException("Une porte/fenêtre murale exige host_opening : fournir min_xz et max_xz pour la baie traversante (expressions de longueur). Les ouvertures des pièces ne percent pas le mur.");
                return null;
            }
            if (hosting != "wall" && hosting != "floor") throw new InvalidOperationException("host_opening exige hosting=wall ou floor.");
            bool floor = hosting == "floor";
            string min = floor ? "min_xyz" : "min_xz", max = floor ? "max_xyz" : "max_xz";
            int dimensions = floor ? 3 : 2;
            var value = token as JObject;
            CodexFamilyDesign.Keys(value, min, max);
            return new FamilyHostOpeningSpec {
                IsFloor = floor,
                Min = CodexFamilyDesign.Items(value, min, dimensions, dimensions).Select(t => LengthExpression.Parse(t as JObject, names)).ToArray(),
                Max = CodexFamilyDesign.Items(value, max, dimensions, dimensions).Select(t => LengthExpression.Parse(t as JObject, names)).ToArray() };
        }

        internal void Validate(Dictionary<string, double> values, Dictionary<string, double> initial)
        {
            for (int a = 0; a < Min.Length; a++)
                if (Max[a].Value(values) - Min[a].Value(values) < 1)
                    throw new InvalidOperationException("host_opening : dimension inférieure à 1 mm pendant un test.");
            foreach (var e in Expressions)
            {
                double v = e.Value(values), start = e.Value(initial);
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 100000)
                    throw new InvalidOperationException("host_opening : coordonnée hors limites.");
                if (!e.IsZero && (Math.Abs(v) < 1 || Math.Abs(start) < 1 || Math.Sign(v) != Math.Sign(start)))
                    throw new InvalidOperationException("host_opening : une limite non nulle est à moins de 1 mm de l'origine ou la traverse ; utiliser une limite constante nulle ou un repère décalé.");
            }
        }
    }
}
