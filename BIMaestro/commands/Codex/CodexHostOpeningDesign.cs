using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilyHostOpeningSpec
    {
        internal LengthExpression[] Min, Max;
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
                        ["min_xz"] = vector, ["max_xz"] = vector.DeepClone() } }),
                ["description"] = "Ouverture native RECTANGULAIRE traversant le mur hôte. Obligatoire pour door/window avec hosting=wall, null sinon en l'absence de découpe. Limites [X,Z] dans les mêmes coordonnées locales que les pièces ; mur parallèle à X, Z vertical. Décrire la baie de pose, pas l'encombrement des poignées/appuis. Chaque coordonnée = {offset_mm,terms:[{parameter,factor}]}. Famille fixe : terms=[] ; paramétrique : utiliser les paramètres de longueur du dormant pour que la baie suive largeur/hauteur. Aucun solide ni vitrage n'est créé par ce champ. Autres hôtes et contours courbes non pris en charge." };
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
            if (hosting != "wall") throw new InvalidOperationException("host_opening exige hosting=wall ; seules les ouvertures de mur sont prises en charge.");
            var value = token as JObject;
            CodexFamilyDesign.Keys(value, "min_xz", "max_xz");
            return new FamilyHostOpeningSpec {
                Min = CodexFamilyDesign.Items(value, "min_xz", 2, 2).Select(t => LengthExpression.Parse(t as JObject, names)).ToArray(),
                Max = CodexFamilyDesign.Items(value, "max_xz", 2, 2).Select(t => LengthExpression.Parse(t as JObject, names)).ToArray() };
        }

        internal void Validate(Dictionary<string, double> values, Dictionary<string, double> initial)
        {
            for (int a = 0; a < 2; a++)
                if (Max[a].Value(values) - Min[a].Value(values) < 1)
                    throw new InvalidOperationException("host_opening : largeur/hauteur inférieure à 1 mm pendant un test.");
            foreach (var e in Expressions)
            {
                double v = e.Value(values), start = e.Value(initial);
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 100000)
                    throw new InvalidOperationException("host_opening : coordonnée hors limites.");
                if (!e.IsZero && (Math.Abs(v) < 0.001 || Math.Abs(start) < 0.001 || Math.Sign(v) != Math.Sign(start)))
                    throw new InvalidOperationException("host_opening : une limite pilotée traverse l'origine ; utiliser une limite constante nulle ou un repère décalé.");
            }
        }
    }
}
