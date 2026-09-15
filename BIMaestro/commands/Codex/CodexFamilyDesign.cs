using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Declarative, bounded geometry. No model-generated code or file paths are executed.
    internal sealed class CodexFamilyDesign
    {
        internal string Name, Category;
        internal JObject Source;
        internal readonly List<FamilyMaterial> Materials = new List<FamilyMaterial>();
        internal readonly List<FamilyPart> Parts = new List<FamilyPart>();
        internal string[] Assumptions;
        internal double[] TargetDimensions;
        internal int SolidCount => Parts.Sum(p => p.Count);
        internal bool Load, Place;

        internal static JObject Tool(bool validateOnly = false)
        {
            var vector = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = Number(-100000, 100000) };
            var geometry = Object(new JObject
            {
                ["kind"] = Enum("box", "cylinder", "tube", "cone", "sphere", "extrusion", "lathe"),
                ["size_mm"] = vector.DeepClone(), ["position_mm"] = vector.DeepClone(), ["rotation_deg"] = vector.DeepClone(),
                ["profile_mm"] = new JObject { ["type"] = "array", ["maxItems"] = 128, ["items"] = new JObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = Number(-100000, 100000) } }
            });
            geometry["description"] = "Solide local transformé par Rx puis Ry puis Rz (degrés), puis position_mm. box : size=[largeur X, profondeur Y, hauteur Z], coin bas à 0. cylinder : [rayon,0,hauteur], axe Z. tube : [rayon extérieur,rayon intérieur,hauteur]. cone : [rayon bas,rayon haut,hauteur], axe Z. sphere : [rayon,0,0], centrée à 0. extrusion : profil fermé XY (sans répéter le premier point), size=[0,0,hauteur]. lathe : profil fermé [rayon,z] tourné autour de Z, size=[0,0,0]. profile_mm=[] pour les primitives sans profil.";
            var properties = new JObject
            {
                ["name"] = Text(90), ["category"] = Enum("generic", "electrical", "mechanical", "furniture", "plumbing"),
                ["target_dimensions_mm"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = Number(0, 100000), ["description"] = "Encombrements extérieurs demandés [X,Y,Z], en mm ; 0 pour une cote inconnue. Le constructeur refuse une différence supérieure à 0,5 % (minimum 1 mm) sur une cote non nulle." },
                ["assumptions"] = Array(Text(400), 0, 20),
                ["materials"] = Array(Object(new JObject { ["name"] = Text(70), ["rgb"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 255 }, ["minItems"] = 3, ["maxItems"] = 3 }, ["transparency"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 90 } }), 1, 32),
                ["parts"] = Array(Object(new JObject
                {
                    ["name"] = Text(70), ["material"] = Text(70), ["geometry"] = geometry.DeepClone(),
                    ["cuts"] = Array((JObject)geometry.DeepClone(), 0, 12),
                    ["repeat_count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 150 },
                    ["repeat_step_mm"] = vector.DeepClone()
                }), 1, 200),
                ["load_into_project"] = new JObject { ["type"] = "boolean" },
                ["place_at_origin"] = new JObject { ["type"] = "boolean" }
            };
            return new JObject { ["type"] = "function", ["name"] = validateOnly ? "revit_validate_family" : "revit_create_family", ["description"] = validateOnly
                ? "Teste la description complète dans un document Revit temporaire : solides, évidements, matériaux et encombrements. Aucun RFA enregistré, aucun chargement ou placement. Utiliser avant une création complexe ou une correction ; corriger l'erreur précise renvoyée puis créer avec les mêmes arguments validés."
                : "Crée une NOUVELLE famille RFA complète depuis une description géométrique, matériaux par pièce, sous-catégories, aperçu 3D, contrôle des dimensions. Enregistre sans écraser ; ne l'ouvre pas : utiliser ensuite revit_open_created_family pour afficher une révision dans l'éditeur. Peut charger dans un PROJET, jamais dans une famille ouverte. Jusqu'à 750 solides. cuts sont soustraits à geometry dans le même repère avant répétition. Géométrie fixe ; matériaux paramétrés. Une opération, un accord selon le mode du panneau.", ["inputSchema"] = Object(properties) };
        }
        private static JObject Object(JObject properties) => new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(properties.Properties().Select(p => p.Name)), ["additionalProperties"] = false };
        private static JObject Array(JObject items, int min, int max) => new JObject { ["type"] = "array", ["items"] = items, ["minItems"] = min, ["maxItems"] = max };
        private static JObject Number(double min, double max) => new JObject { ["type"] = "number", ["minimum"] = min, ["maximum"] = max };
        private static JObject Text(int max) => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max };
        private static JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };

        internal static CodexFamilyDesign Parse(JObject value)
        {
            Keys(value, "name", "category", "target_dimensions_mm", "assumptions", "materials", "parts", "load_into_project", "place_at_origin");
            var design = new CodexFamilyDesign { Name = String(value, "name", 90), Category = String(value, "category", 20), Source = (JObject)value.DeepClone() };
            if (!new[] { "generic", "electrical", "mechanical", "furniture", "plumbing" }.Contains(design.Category)) throw new InvalidOperationException("Catégorie de famille non prise en charge.");
            design.TargetDimensions = Vector(value["target_dimensions_mm"], "target_dimensions_mm", 3, 0);
            if (value["load_into_project"].Type != JTokenType.Boolean || value["place_at_origin"].Type != JTokenType.Boolean) throw new InvalidOperationException("Options de chargement invalides.");
            design.Load = value.Value<bool>("load_into_project"); design.Place = value.Value<bool>("place_at_origin");
            if (design.Place && !design.Load) throw new InvalidOperationException("Le placement exige le chargement dans le projet.");
            design.Assumptions = Items(value, "assumptions", 0, 20).Select(v => ValidString(v, "hypothèse", 400)).ToArray();
            foreach (var token in Items(value, "materials", 1, 32))
            {
                var material = token as JObject; Keys(material, "name", "rgb", "transparency");
                var rgb = Vector(material["rgb"], "rgb", 3, 0, 255);
                if (rgb.Any(c => c != Math.Truncate(c))) throw new InvalidOperationException("Les couleurs RGB doivent être entières.");
                var m = new FamilyMaterial { Name = String(material, "name", 70), Rgb = rgb.Select(c => (byte)c).ToArray(), Transparency = Integer(material["transparency"], "transparency", 0, 90) };
                if (design.Materials.Any(existing => existing.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Noms de matériaux en double.");
                design.Materials.Add(m);
            }
            var instances = new HashSet<string>();
            foreach (var token in Items(value, "parts", 1, 200))
            {
                try
                {
                var item = token as JObject; Keys(item, "name", "material", "geometry", "cuts", "repeat_count", "repeat_step_mm");
                var part = new FamilyPart { Name = String(item, "name", 70), Material = String(item, "material", 70), Geometry = FamilyPrimitive.Parse(item["geometry"] as JObject),
                    Count = Integer(item["repeat_count"], "repeat_count", 1, 150), Step = Vector(item["repeat_step_mm"], "repeat_step_mm", 3) };
                if (!design.Materials.Any(m => m.Name == part.Material)) throw new InvalidOperationException("Matériau inconnu : " + part.Material);
                if (design.Parts.Any(p => p.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Chaque groupe de pièces doit avoir un nom distinct.");
                part.Cuts = Items(item, "cuts", 0, 12).Select(c => FamilyPrimitive.Parse(c as JObject)).ToList();
                if (part.Count > 1 && part.Step.Sum(x => x * x) < 1) throw new InvalidOperationException("Répétition sans espacement suffisant.");
                if (design.SolidCount + part.Count > 750) throw new InvalidOperationException("Limite de 750 solides par famille.");
                for (int i = 0; i < part.Count; i++)
                {
                    var position = part.Geometry.Position.Select((p, axis) => p + i * part.Step[axis]).ToArray();
                    if (position.Any(p => Math.Abs(p) > 100000)) throw new InvalidOperationException("Répétition au-delà de 100 m de l'origine.");
                    string signature = JsonConvert.SerializeObject(new { part.Geometry.Kind, part.Geometry.Size, Position = position,
                        part.Geometry.Rotation, part.Geometry.Profile, Cuts = part.Cuts.Select(c => new { c.Kind, c.Size, c.Position, c.Rotation, c.Profile }) });
                    // Material differences do not justify coincident copies of an identical solid.
                    if (!instances.Add(signature)) throw new InvalidOperationException("Solides exactement superposés : " + part.Name);
                }
                design.Parts.Add(part);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    throw new InvalidOperationException("parts[" + design.Parts.Count + "] (« " + (string)(token as JObject)?["name"] + " ») : " + ex.Message, ex);
                }
            }
            return design;
        }
        internal void CheckDimensions(double[] measured)
        {
            var errors = new List<string>();
            for (int axis = 0; axis < 3; axis++)
                if (TargetDimensions[axis] > 0 && Math.Abs(measured[axis] - TargetDimensions[axis]) > Math.Max(1, TargetDimensions[axis] * 0.005))
                    errors.Add($"{"XYZ"[axis]} : {measured[axis]:0.###} mm obtenus, {TargetDimensions[axis]:0.###} mm demandés");
            if (errors.Count > 0) throw new InvalidOperationException("Encombrement incorrect : " + string.Join(" ; ", errors) + ". Corriger les pièces et rotations ; ne pas remplacer une cote demandée par 0 pour contourner ce contrôle.");
        }
        internal static void Keys(JObject value, params string[] keys)
        {
            if (value == null) throw new InvalidOperationException("Objet JSON attendu.");
            var missing = keys.Where(k => value[k] == null).ToArray();
            var unknown = value.Properties().Select(p => p.Name).Where(n => !keys.Contains(n)).ToArray();
            if (missing.Length > 0 || unknown.Length > 0) throw new InvalidOperationException("Structure invalide. Champs manquants : " + string.Join(", ", missing) + ". Champs inconnus : " + string.Join(", ", unknown) + ".");
        }
        internal static JArray Items(JObject value, string name, int min, int max)
        {
            if (!(value[name] is JArray items) || items.Count < min || items.Count > max) throw new InvalidOperationException("Liste invalide : " + name);
            return items;
        }
        internal static string String(JObject value, string name, int max) => ValidString(value[name], name, max);
        private static string ValidString(JToken token, string name, int max)
        {
            if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token) || ((string)token).Length > max || ((string)token).Any(char.IsControl)) throw new InvalidOperationException("Texte invalide : " + name);
            return ((string)token).Trim();
        }
        internal static double[] Vector(JToken token, string name, int size, double min = -100000, double max = 100000)
        {
            if (!(token is JArray array) || array.Count != size) throw new InvalidOperationException("Vecteur invalide : " + name);
            return array.Select(t => Scalar(t, name, min, max)).ToArray();
        }
        internal static double Scalar(JToken value, string name, double min, double max)
        {
            if (value == null || value.Type != JTokenType.Integer && value.Type != JTokenType.Float) throw new InvalidOperationException("Nombre attendu : " + name);
            double number = value.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max) throw new InvalidOperationException("Valeur hors limites : " + name);
            return number;
        }
        private static int Integer(JToken value, string name, int min, int max)
        {
            double n = Scalar(value, name, min, max);
            if (n != Math.Truncate(n)) throw new InvalidOperationException("Entier attendu : " + name);
            return (int)n;
        }
    }
    internal sealed class FamilyMaterial { internal string Name; internal byte[] Rgb; internal int Transparency; }
    internal sealed class FamilyPart { internal string Name, Material; internal FamilyPrimitive Geometry; internal List<FamilyPrimitive> Cuts; internal int Count; internal double[] Step; }
    internal sealed class FamilyPrimitive
    {
        internal string Kind;
        internal double[] Size, Position, Rotation;
        internal double[][] Profile;
        internal static FamilyPrimitive Parse(JObject item)
        {
            CodexFamilyDesign.Keys(item, "kind", "size_mm", "position_mm", "rotation_deg", "profile_mm");
            var p = new FamilyPrimitive { Kind = CodexFamilyDesign.String(item, "kind", 20), Size = CodexFamilyDesign.Vector(item["size_mm"], "size_mm", 3, 0),
                Position = CodexFamilyDesign.Vector(item["position_mm"], "position_mm", 3), Rotation = CodexFamilyDesign.Vector(item["rotation_deg"], "rotation_deg", 3, -360, 360),
                Profile = CodexFamilyDesign.Items(item, "profile_mm", 0, 128).Select(v => CodexFamilyDesign.Vector(v, "profile_mm", 2)).ToArray() };
            bool profile = p.Kind == "extrusion" || p.Kind == "lathe";
            if (!new[] { "box", "cylinder", "tube", "cone", "sphere", "extrusion", "lathe" }.Contains(p.Kind)) throw new InvalidOperationException("Forme inconnue.");
            if (p.Kind == "cylinder" && p.Size[1] != 0 || p.Kind == "sphere" && (p.Size[1] != 0 || p.Size[2] != 0) || p.Kind == "extrusion" && (p.Size[0] != 0 || p.Size[1] != 0) || p.Kind == "lathe" && p.Size.Any(v => v != 0))
                throw new InvalidOperationException("Les dimensions inutilisées de cette forme doivent être nulles (0).");
            if (profile) ValidatePolygon(p.Profile);
            else if (p.Profile.Length != 0) throw new InvalidOperationException("Profil inattendu pour une primitive.");
            if (p.Kind == "box" && p.Size.Any(s => s < 1) || new[] { "cylinder", "tube", "cone", "extrusion" }.Contains(p.Kind) && p.Size[2] < 1 || new[] { "cylinder", "tube", "sphere" }.Contains(p.Kind) && p.Size[0] < 1)
                throw new InvalidOperationException("Dimension minimale : 1 mm.");
            if (p.Kind == "tube" && (p.Size[1] < 1 || p.Size[0] - p.Size[1] < 1)) throw new InvalidOperationException("Le tube doit avoir au moins 1 mm d'épaisseur.");
            if (p.Kind == "cone" && (p.Size[0] < 1 && p.Size[1] < 1)) throw new InvalidOperationException("Rayons du cône invalides.");
            if (p.Kind == "lathe" && p.Profile.Any(v => v[0] < 0)) throw new InvalidOperationException("Rayon négatif dans le profil de révolution.");
            return p;
        }
        // Detect degeneracies/self intersections before entering Revit's geometry kernel.
        internal static void ValidatePolygon(double[][] points)
        {
            if (points.Length < 3) throw new InvalidOperationException("Un profil fermé exige au moins trois sommets.");
            double area = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                if (Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) < 1) throw new InvalidOperationException("Arête de profil " + i + " inférieure à 1 mm ou sommet répété.");
                area += a[0] * b[1] - b[0] * a[1];
                for (int j = i + 2; j < points.Length; j++)
                {
                    if ((j + 1) % points.Length == i) continue;
                    if (Intersects(a, b, points[j], points[(j + 1) % points.Length])) throw new InvalidOperationException("Le profil se croise ou se touche : arêtes " + i + " et " + j + ".");
                }
            }
            if (Math.Abs(area) < 2) throw new InvalidOperationException("Surface du profil trop petite.");
        }
        private static double Cross(double[] a, double[] b, double[] p) => (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);
        private static bool Intersects(double[] a, double[] b, double[] c, double[] d)
        {
            if (Math.Max(a[0], b[0]) < Math.Min(c[0], d[0]) || Math.Max(c[0], d[0]) < Math.Min(a[0], b[0]) || Math.Max(a[1], b[1]) < Math.Min(c[1], d[1]) || Math.Max(c[1], d[1]) < Math.Min(a[1], b[1])) return false;
            return Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0;
        }
    }
}
