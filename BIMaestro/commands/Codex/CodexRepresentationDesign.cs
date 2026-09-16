using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class FamilyRepresentationSpec
    {
        internal string[] HideModelIn;
        internal readonly List<FamilyDrawingSpec> Drawings = new List<FamilyDrawingSpec>();
        internal static JObject Schema()
        {
            JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
            JObject Number(double min, double max) => new JObject { ["type"] = "number", ["minimum"] = min, ["maximum"] = max };
            JObject Array(JObject item, int min, int max) => new JObject { ["type"] = "array", ["minItems"] = min, ["maxItems"] = max, ["items"] = item };
            JObject Obj(JObject properties) => new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(properties.Properties().Select(p => p.Name)), ["additionalProperties"] = false };
            var curve = Obj(new JObject {
                ["kind"] = Enum("line", "arc", "circle"),
                ["points_mm"] = Array(Array(Number(-100000, 100000), 2, 2), 1, 3),
                ["radius_mm"] = Number(0, 100000) });
            curve["description"] = "Coordonnées locales [u,v] : XY=[X,Y], XZ=[X,Z], YZ=[Y,Z]. line : 2 points début/fin, radius_mm=0. arc : 3 points début/fin/point intermédiaire SUR l'arc, radius_mm=0. circle : 1 centre, radius_mm>0. Géométrie fixe, non liée aux paramètres.";
            var drawing = Obj(new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
                ["mode"] = Enum("symbolic", "model", "filled", "masking"), ["plane"] = Enum("xy", "xz", "yz"),
                ["offset_mm"] = Number(-100000, 100000), ["curves"] = Array(curve, 1, 128),
                ["rgb"] = Array(new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 255 }, 3, 3),
                ["fill_pattern"] = Enum("solid", "diagonal") });
            drawing["description"] = "symbolic : traits/arcs uniquement dans les vues 2D parallèles au plan (préférer pour ouverture de porte). model : courbes également visibles en 3D. filled/masking : un contour fermé simple, sans trous ; cercle seul ou succession de lignes/arcs jointifs dans l'ordre. filled : couleur rgb et motif solid/diagonal ; masking : masque opaque sans motif. rgb/fill_pattern ignorés pour les courbes et masques. Tous niveaux de détail. Un masque ne remplace pas le réglage hide_model_in.";
            return new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "null" }, Obj(new JObject {
                ["hide_model_in"] = Array(Enum("xy", "xz", "yz"), 0, 3), ["drawings"] = Array(drawing, 1, 80) })),
                ["description"] = "Représentation 2D intégrée au RFA, aussi disponible pour géométries fixes (arbres). null si inutile. hide_model_in masque les solides dans les vues parallèles choisies, tout en conservant la 3D ; xy = plan/plafond. Exemple arbre : hide_model_in=[xy], disque filled et traits symbolic sur xy. Les tracés libres restent à dimensions FIXES même dans une famille paramétrique ; conserver symbolic_outlines pour les rectangles pilotés. Les modes model restent visibles en 3D." };
        }
        internal static FamilyRepresentationSpec Parse(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var value = token as JObject; CodexFamilyDesign.Keys(value, "hide_model_in", "drawings");
            var result = new FamilyRepresentationSpec { HideModelIn = CodexFamilyDesign.Items(value, "hide_model_in", 0, 3).Select(t => (string)t).ToArray() };
            if (result.HideModelIn.Any(p => !new[] { "xy", "xz", "yz" }.Contains(p)) || result.HideModelIn.Distinct().Count() != result.HideModelIn.Length)
                throw new InvalidOperationException("representation_2d.hide_model_in : plans inconnus ou dupliqués.");
            foreach (var item in CodexFamilyDesign.Items(value, "drawings", 1, 80))
            {
                var drawing = item as JObject;
                CodexFamilyDesign.Keys(drawing, "name", "mode", "plane", "offset_mm", "curves", "rgb", "fill_pattern");
                var spec = new FamilyDrawingSpec { Name = CodexFamilyDesign.String(drawing, "name", 70), Mode = (string)drawing["mode"], Plane = (string)drawing["plane"],
                    Offset = CodexFamilyDesign.Scalar(drawing["offset_mm"], "offset_mm", -100000, 100000), Pattern = (string)drawing["fill_pattern"] };
                if (!new[] { "symbolic", "model", "filled", "masking" }.Contains(spec.Mode) || !new[] { "xy", "xz", "yz" }.Contains(spec.Plane) || !new[] { "solid", "diagonal" }.Contains(spec.Pattern))
                    throw new InvalidOperationException("Mode, plan ou motif 2D inconnu : " + spec.Name);
                var rgb = CodexFamilyDesign.Vector(drawing["rgb"], "rgb", 3, 0, 255);
                if (rgb.Any(c => c != Math.Truncate(c))) throw new InvalidOperationException("Couleur 2D entière attendue.");
                spec.Rgb = rgb.Select(c => (byte)c).ToArray();
                foreach (var c in CodexFamilyDesign.Items(drawing, "curves", 1, 128))
                {
                    var curve = c as JObject; CodexFamilyDesign.Keys(curve, "kind", "points_mm", "radius_mm");
                    string kind = (string)curve["kind"];
                    int count = kind == "line" ? 2 : kind == "arc" ? 3 : kind == "circle" ? 1 : 0;
                    if (count == 0) throw new InvalidOperationException("Courbe 2D inconnue.");
                    var part = new FamilyDrawingCurve { Kind = kind, Points = CodexFamilyDesign.Items(curve, "points_mm", count, count).Select(p => CodexFamilyDesign.Vector(p, "points_mm", 2, -100000, 100000)).ToArray(),
                        Radius = CodexFamilyDesign.Scalar(curve["radius_mm"], "radius_mm", 0, 100000) };
                    if (kind == "circle" ? part.Radius < 1 : part.Radius != 0 || Distance(part.Points[0], part.Points[1]) < 1)
                        throw new InvalidOperationException("Courbe 2D dégénérée ou rayon invalide : " + spec.Name);
                    if (kind == "arc")
                    {
                        var a = part.Points[0]; var b = part.Points[1]; var m = part.Points[2];
                        if (Distance(a, m) < 1 || Distance(b, m) < 1 || Math.Abs((b[0]-a[0])*(m[1]-a[1])-(b[1]-a[1])*(m[0]-a[0])) < 1)
                            throw new InvalidOperationException("Arc 2D colinéaire ou dégénéré : " + spec.Name);
                    }
                    spec.Curves.Add(part);
                }
                if (spec.Mode == "filled" || spec.Mode == "masking")
                {
                    if (spec.Curves.Any(c => c.Kind == "circle"))
                    { if (spec.Curves.Count != 1) throw new InvalidOperationException("Un contour circulaire doit être seul."); }
                    else for (int i = 0; i < spec.Curves.Count; i++)
                        if (Distance(spec.Curves[i].Points[1], spec.Curves[(i + 1) % spec.Curves.Count].Points[0]) > 0.0001)
                            throw new InvalidOperationException("Le contour de pochage/masquage doit être fermé et ordonné : " + spec.Name);
                }
                if (result.Drawings.Any(d => d.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Nom de dessin 2D dupliqué.");
                result.Drawings.Add(spec);
            }
            if (result.Drawings.Sum(d => d.Curves.Count) > 1000) throw new InvalidOperationException("Maximum 1000 courbes de représentation 2D.");
            foreach (var plane in result.HideModelIn)
                if (!result.Drawings.Any(d => d.Plane == plane && d.Mode != "model")) throw new InvalidOperationException("Prévoir une représentation 2D dans chaque plan où la géométrie est masquée.");
            return result;
        }
        private static double Distance(double[] a, double[] b) => Math.Sqrt(Math.Pow(a[0]-b[0], 2)+Math.Pow(a[1]-b[1], 2));
    }
    internal sealed class FamilyDrawingSpec
    {
        internal string Name, Mode, Plane, Pattern; internal double Offset; internal byte[] Rgb;
        internal readonly List<FamilyDrawingCurve> Curves = new List<FamilyDrawingCurve>();
    }
    internal sealed class FamilyDrawingCurve { internal string Kind; internal double[][] Points; internal double Radius; }
}
