using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIMaestro.Codex
{
    // A small declarative constraint language: lengths and linear expressions, never executable code.
    internal sealed class CodexParametricDesign
    {
        internal CodexFamilyDesign Metadata;
        internal CodexFamilyParameters Registry;
        internal List<FamilyConnectorSpec> Connectors = new List<FamilyConnectorSpec>();
        internal List<FamilySymbolicSpec> Symbols = new List<FamilySymbolicSpec>();
        internal string Hosting = "free";
        internal readonly List<DrivingLength> Parameters = new List<DrivingLength>();
        internal readonly List<DrivingLength> Angles = new List<DrivingLength>(); // Values and tests in degrees.
        internal readonly List<ParametricPart> Parts = new List<ParametricPart>();
        internal readonly List<ParametricArray> Arrays = new List<ParametricArray>();
        internal int SolidCount => Parts.Count + Arrays.Sum(a => a.Count(Initial));
        internal Dictionary<string, double> Initial => Registry == null ? Parameters.Concat(Angles).ToDictionary(p => p.Name, p => p.Value) : new FamilyCase { Values = Registry.Initial }.Numeric;

        internal static JObject Tool(bool validateOnly = false)
        {
            var root = CodexFamilyDesign.Tool();
            root["name"] = validateOnly ? "revit_validate_parametric_family" : "revit_create_parametric_family";
            root["description"] = "Crée une NOUVELLE famille paramétrique : extrusions rectangulaires natives, ouvertures, cotes et paramètres de type ou d'occurrence via family_options. Les réseaux arrays peuvent incliner leurs barres via angles et rotation (X, Y ou Z, 1 à 89 degrés), avec dimensions et nombre également réglables sans Codex. Les pièces parts acceptent aussi profile_uv : un contour polygonal simple, sans trous, piloté par des coordonnées de longueur. Vérifie dimensions, positions, nombres et inclinaisons lors d'essais de variation, puis restaure les valeurs initiales avant sauvegarde. Ne convertit pas automatiquement les FreeFormElement existants. Connecteurs natifs via connectors, contours 2D via symbolic_outlines. Pas de loft paramétrique.";
            var properties = (JObject)root["inputSchema"]["properties"];
            root["description"] = (string)root["description"] + " arrays permet aussi des réseaux natifs de barres imbriquées, avec nombre entier calculé à partir de la longueur utile et du pas. Les calculs de longueur sont simplifiés et partagés pour limiter les paramètres internes.";
            properties.Remove("target_dimensions_mm");
            properties["connectors"] = FamilyConnectorSpec.Schema();
            properties["symbolic_outlines"] = FamilySymbolicSpec.Schema();
            properties["hosting"] = new JObject { ["type"] = "string", ["enum"] = new JArray("free", "face", "wall", "ceiling", "work_plane"),
                ["description"] = "Gabarit et comportement de placement du RFA. free par défaut historique. Les modes hébergés peuvent être chargés mais exigent place_at_origin=false : l'hôte doit ensuite être choisi dans le projet. Choisir avant la construction, pas après." };
            properties["family_options"] = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "null" }, CodexFamilyParameters.Schema()),
                ["description"] = "V1 : paramètres typés, formules, type/occurrence, types nommés et visibilité conditionnelle/grossier-moyen-fin. null pour une ancienne description simple. Les formules restent actives dans Revit sans Codex. Une pièce cachée doit rester géométriquement valide." };
            var number = new JObject { ["type"] = "number", ["minimum"] = -100000, ["maximum"] = 100000 };
            var parameterName = new JObject { ["type"] = "string", ["pattern"] = "^[A-Za-z][A-Za-z0-9_]{0,39}$" };
            var expression = Obj(new JObject {
                ["offset_mm"] = number.DeepClone(),
                ["terms"] = Arr(Obj(new JObject { ["parameter"] = parameterName.DeepClone(), ["factor"] = new JObject { ["type"] = "number", ["minimum"] = -1000, ["maximum"] = 1000 } }), 0, 8) });
            expression["description"] = "offset_mm + somme(parameter * factor). Exemple -Largeur/2 : offset_mm=0, terms=[{parameter:Largeur,factor:-0.5}]. Les coordonnées ne doivent pas traverser l'origine pendant les tests ; une coordonnée identiquement nulle est autorisée.";
            var positive = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 100000 };
            properties["parameters"] = Arr(Obj(new JObject { ["name"] = parameterName.DeepClone(), ["value_mm"] = positive.DeepClone(), ["test_value_mm"] = positive.DeepClone() }), 1, 8);
            properties["parameters"]["minItems"] = 0;
            properties["parameters"]["description"] = "Format historique des longueurs. En V1, peut être vide si les réglages sont déclarés dans family_options.parameters. Les coordonnées utilisent les noms des longueurs des deux formats.";
            var angleValue = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 89 };
            properties["angles"] = Arr(Obj(new JObject { ["name"] = parameterName.DeepClone(), ["value_deg"] = angleValue.DeepClone(), ["test_value_deg"] = angleValue.DeepClone() }), 0, 8);
            properties["angles"]["description"] = "Paramètres d'angle de TYPE, en degrés, pour rotation des barres de arrays. Plage 1 à 89 degrés, test différent d'au moins 1 degré. angles=[] si aucune inclinaison. Les angles ne sont pas des longueurs et ne peuvent pas apparaître dans les expressions de coordonnées.";
            properties["parts"] = Arr(Obj(new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
                ["material"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
                ["axis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("x", "y", "z") },
                ["minimum"] = Arr((JObject)expression.DeepClone(), 3, 3), ["maximum"] = Arr((JObject)expression.DeepClone(), 3, 3),
                ["profile_uv"] = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "null" }, Arr(Arr((JObject)expression.DeepClone(), 2, 2), 3, 24)),
                    ["description"] = "null : rectangle. Sinon contour polygonal paramétrique simple en UV (axes du profil), sans répéter le premier sommet ; pas d'ouvertures internes dans ce mode. Ses limites doivent correspondre à minimum/maximum. Les sommets peuvent utiliser des longueurs calculées par formules (dont trigonométrie), donc des faces obliques restent réglables." },
                ["openings"] = Arr(Obj(new JObject { ["minimum_uv"] = Arr((JObject)expression.DeepClone(), 2, 2), ["maximum_uv"] = Arr((JObject)expression.DeepClone(), 2, 2) }), 0, 12)
            }), 1, 80);
            properties["parts"]["description"] = "Encombrement XYZ local, minimum et maximum sont des expressions. Extrusion selon axis ; profil UV : X=>YZ, Y=>XZ, Z=>XY. Les openings sont des trous traversants dans ce profil, strictement à l'intérieur, séparés d'au moins 1 mm entre eux et du bord.";
            properties["parts"]["minItems"] = 0;
            properties["arrays"] = Arr(Obj(new JObject {
                ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
                ["material"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 70 },
                ["count_parameter"] = parameterName.DeepClone(),
                ["axis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("x", "y", "z") },
                ["minimum"] = Arr((JObject)expression.DeepClone(), 3, 3), ["maximum"] = Arr((JObject)expression.DeepClone(), 3, 3),
                ["span"] = expression.DeepClone(), ["pitch"] = expression.DeepClone(),
                ["quantity_parameter"] = new JObject { ["type"] = "string", ["maxLength"] = 40, ["description"] = "Chaîne vide : nombre = rounddown(span/pitch). Sinon paramètre entier de family_options pilotant le nombre visible, 0 à 200. Pour un composant isolé inclinable : entier constant 1. En compatibilité 2023, les cas 0/1 utilisent une représentation conditionnelle et conservent des géométries cachées ; pas une suppression physique pour les quantités." },
                ["rotation"] = new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "null" }, Obj(new JObject {
                    ["axis"] = new JObject { ["type"] = "string", ["enum"] = new JArray("x", "y", "z") }, ["angle_parameter"] = parameterName.DeepClone() })) }
            }), 0, 8);
            properties["arrays"]["description"] = "Réseaux natifs de barres rectangulaires imbriquées. minimum/maximum décrivent UNIQUEMENT la première barre en XYZ. Copies vers l'axe positif, pas entre origines = pitch (expression en mm). count_parameter est un nouveau paramètre entier calculé par rounddown(span/pitch) : span est la longueur utile, hors cadre. Ex. span=500 et pitch=50 donnent 10 barres. Prévoir les marges dans la position de la première barre. 2 à 200 barres par réseau en mode ancien ; 0 à 200 visibles en V1 avec family_options. Section projetée < pitch. Tester une variation de span ou pitch qui change le nombre. Une seule barre source, jamais une pièce par répétition. arrays=[] si aucun réseau.";
            properties["arrays"]["description"] = (string)properties["arrays"]["description"] + " rotation=null pour une barre droite. Sinon minimum/maximum sont les dimensions AVANT rotation ; la rotation positive selon la règle de la main droite se fait autour de l'axe choisi PASSANT PAR minimum (coin d'ancrage). Le paramètre d'angle doit exister dans angles. L'encombrement incliné est vérifié, et peut dépasser ces coordonnées : prévoir le dégagement du cadre. La projection de la section sur l'axe de répétition doit rester inférieure au pas à tous les essais.";
            root["inputSchema"]["required"] = new JArray(properties.Properties().Select(p => p.Name));
            if (validateOnly) root["description"] = "Teste une famille paramétrique complète dans un document temporaire : construction, formules, variations, visibilité, types, connecteurs et contours 2D demandés. Aucun RFA enregistré et aucun chargement dans le projet, même si load_into_project/place_at_origin sont vrais. Renvoie les contrôles natifs réellement exécutés. Autorisation de modifications nécessaire, sans confirmation supplémentaire car le document utilisateur n'est pas modifié.";
            return root;
        }
        private static JObject Obj(JObject p) => new JObject { ["type"] = "object", ["properties"] = p, ["required"] = new JArray(p.Properties().Select(v => v.Name)), ["additionalProperties"] = false };
        private static JObject Arr(JObject item, int min, int max) => new JObject { ["type"] = "array", ["items"] = item, ["minItems"] = min, ["maxItems"] = max };

        internal static CodexParametricDesign Parse(JObject source)
        {
            var keys = new List<string> { "name", "category", "assumptions", "materials", "parameters", "parts", "load_into_project", "place_at_origin" };
            if (source?["family_options"] != null) keys.Add("family_options");
            if (source?["connectors"] != null) keys.Add("connectors");
            if (source?["symbolic_outlines"] != null) keys.Add("symbolic_outlines");
            if (source?["hosting"] != null) keys.Add("hosting");
            if (source?["arrays"] != null) keys.Add("arrays");
            if (source?["angles"] != null) keys.Add("angles");
            CodexFamilyDesign.Keys(source, keys.ToArray());
            var firstMaterial = CodexFamilyDesign.Items(source, "materials", 1, 32)[0] as JObject;
            if (firstMaterial == null) throw new InvalidOperationException("Un matériau doit être un objet avec un nom et une couleur.");
            // Reuse the existing validated metadata/material contract without pretending these are fixed solids.
            var metadata = (JObject)source.DeepClone(); metadata.Remove("parameters"); metadata.Remove("arrays"); metadata.Remove("angles"); metadata.Remove("family_options");
            metadata.Remove("connectors");
            metadata.Remove("symbolic_outlines");
            metadata.Remove("hosting");
            metadata["target_dimensions_mm"] = new JArray(0, 0, 0);
            metadata["parts"] = new JArray(new JObject { ["name"] = "Metadata", ["material"] = CodexFamilyDesign.String(firstMaterial, "name", 70),
                ["geometry"] = new JObject { ["kind"] = "box", ["size_mm"] = new JArray(1, 1, 1), ["position_mm"] = new JArray(0, 0, 0), ["rotation_deg"] = new JArray(0, 0, 0), ["profile_mm"] = new JArray() },
                ["cuts"] = new JArray(), ["repeat_count"] = 1, ["repeat_step_mm"] = new JArray(0, 0, 0) });
            var design = new CodexParametricDesign { Metadata = CodexFamilyDesign.Parse(metadata), Registry = CodexFamilyParameters.Parse(source) };
            design.Metadata.Parts.Clear(); design.Metadata.Source = (JObject)source.DeepClone();
            if (source["hosting"] != null) design.Hosting = (string)source["hosting"];
            if (!new[] { "free", "face", "wall", "ceiling", "work_plane" }.Contains(design.Hosting)) throw new InvalidOperationException("Mode d'hébergement inconnu.");
            if (design.Hosting != "free" && design.Metadata.Place) throw new InvalidOperationException("Une famille hébergée nécessite le choix d'un hôte : place_at_origin doit être false. Le chargement reste possible.");
            var lengthTokens = design.Registry == null ? CodexFamilyDesign.Items(source, "parameters", 1, 8) : new JArray(design.Registry.Lengths.Select(p => new JObject {
                ["name"] = p.Name, ["value_mm"] = design.Registry.Initial[p.Name].Number,
                ["test_value_mm"] = design.Registry.Cases().Select(c => c.Values[p.Name].Number).FirstOrDefault(v => v != design.Registry.Initial[p.Name].Number) is double test && test != 0 ? test : design.Registry.Initial[p.Name].Number }));
            foreach (var token in lengthTokens)
            {
                var p = token as JObject; CodexFamilyDesign.Keys(p, "name", "value_mm", "test_value_mm");
                string name = CodexFamilyDesign.String(p, "name", 40);
                if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,39}$") || name.StartsWith("BIM_", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Nom de paramètre invalide ou réservé : " + name);
                if (design.Parameters.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Paramètre en double : " + name);
                double value = CodexFamilyDesign.Scalar(p["value_mm"], name, 1, 100000), test = CodexFamilyDesign.Scalar(p["test_value_mm"], name, 1, 100000);
                if (design.Registry == null && Math.Abs(value - test) < 1) throw new InvalidOperationException("Le test doit changer « " + name + " » d'au moins 1 mm.");
                design.Parameters.Add(new DrivingLength { Name = name, Value = value, TestValue = test });
            }
            var names = new HashSet<string>(design.Parameters.Select(p => p.Name));
            var angleTokens = design.Registry == null ? (source["angles"] == null ? new JArray() : CodexFamilyDesign.Items(source, "angles", 0, 8)) :
                new JArray(design.Registry.Angles.Select(p => new JObject { ["name"] = p.Name, ["value_deg"] = design.Registry.Initial[p.Name].Number,
                    ["test_value_deg"] = p.Tests.Count > 0 ? p.Tests[0].Number : design.Registry.Initial[p.Name].Number }));
                foreach (var token in angleTokens)
                {
                    var a = token as JObject; CodexFamilyDesign.Keys(a, "name", "value_deg", "test_value_deg");
                    string name = CodexFamilyDesign.String(a, "name", 40);
                    if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,39}$") || name.StartsWith("BIM_", StringComparison.OrdinalIgnoreCase) || design.Parameters.Concat(design.Angles).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Nom d'angle invalide, réservé ou déjà utilisé : " + name);
                    double value = CodexFamilyDesign.Scalar(a["value_deg"], name, 1, 89), test = CodexFamilyDesign.Scalar(a["test_value_deg"], name, 1, 89);
                    if (design.Registry == null && Math.Abs(value - test) < 1) throw new InvalidOperationException("Le test de l'angle doit varier d'au moins 1 degré : " + name);
                    design.Angles.Add(new DrivingLength { Name = name, Value = value, TestValue = test });
                }
            foreach (var token in CodexFamilyDesign.Items(source, "parts", 0, 80))
            {
                var p = token as JObject;
                var partKeys = new List<string> { "name", "material", "axis", "minimum", "maximum", "openings" }; if (p?["profile_uv"] != null) partKeys.Add("profile_uv");
                CodexFamilyDesign.Keys(p, partKeys.ToArray());
                var part = new ParametricPart { Name = CodexFamilyDesign.String(p, "name", 70), Material = CodexFamilyDesign.String(p, "material", 70),
                    Axis = "xyz".IndexOf(CodexFamilyDesign.String(p, "axis", 1), StringComparison.Ordinal),
                    Min = ReadExpressions(p, "minimum", 3, names), Max = ReadExpressions(p, "maximum", 3, names) };
                if (part.Axis < 0) throw new InvalidOperationException("Axe d'extrusion attendu : x, y ou z.");
                if (p["profile_uv"] != null && p["profile_uv"].Type != JTokenType.Null)
                    part.Profile = CodexFamilyDesign.Items(p, "profile_uv", 3, 24).Select(v => {
                        if (!(v is JArray coordinates) || coordinates.Count != 2) throw new InvalidOperationException("Sommet UV attendu.");
                        return coordinates.Select(e => LengthExpression.Parse(e as JObject, names)).ToArray(); }).ToArray();
                if (design.Parts.Any(v => v.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Nom de pièce en double : " + part.Name);
                if (!design.Metadata.Materials.Any(m => m.Name == part.Material)) throw new InvalidOperationException("Matériau inconnu : " + part.Material);
                foreach (var holeToken in CodexFamilyDesign.Items(p, "openings", 0, 12))
                {
                    var hole = holeToken as JObject; CodexFamilyDesign.Keys(hole, "minimum_uv", "maximum_uv");
                    part.Openings.Add(new ParametricOpening { Min = ReadExpressions(hole, "minimum_uv", 2, names), Max = ReadExpressions(hole, "maximum_uv", 2, names) });
                }
                design.Parts.Add(part);
            }
            if (source["arrays"] != null)
                foreach (var token in CodexFamilyDesign.Items(source, "arrays", 0, 8))
                {
                    var a = token as JObject;
                    var arrayKeys = new List<string> { "name", "material", "count_parameter", "axis", "minimum", "maximum", "span", "pitch" };
                    if (a?["rotation"] != null) arrayKeys.Add("rotation");
                    if (a?["quantity_parameter"] != null) arrayKeys.Add("quantity_parameter");
                    CodexFamilyDesign.Keys(a, arrayKeys.ToArray());
                    var array = new ParametricArray { Name = CodexFamilyDesign.String(a, "name", 70), Material = CodexFamilyDesign.String(a, "material", 70),
                        CountParameter = CodexFamilyDesign.String(a, "count_parameter", 40), Axis = "xyz".IndexOf(CodexFamilyDesign.String(a, "axis", 1), StringComparison.Ordinal),
                        Min = ReadExpressions(a, "minimum", 3, names), Max = ReadExpressions(a, "maximum", 3, names),
                        Span = LengthExpression.Parse(a["span"] as JObject, names), Pitch = LengthExpression.Parse(a["pitch"] as JObject, names) };
                    array.SmallCounts = design.Registry != null;
                    array.QuantityParameter = a["quantity_parameter"] == null ? "" : (string)a["quantity_parameter"];
                    if (array.QuantityParameter == null || array.QuantityParameter.Length > 0 && (design.Registry == null || design.Registry.Find(array.QuantityParameter).Kind != "integer")) throw new InvalidOperationException("Le nombre du réseau doit référencer un paramètre entier de family_options.");
                    if (array.Axis < 0) throw new InvalidOperationException("Axe de réseau attendu : x, y ou z.");
                    if (!Regex.IsMatch(array.CountParameter, "^[A-Za-z][A-Za-z0-9_]{0,39}$") || array.CountParameter.StartsWith("BIM_", StringComparison.OrdinalIgnoreCase) ||
                        design.Parameters.Concat(design.Angles).Any(p => p.Name.Equals(array.CountParameter, StringComparison.OrdinalIgnoreCase)) || design.Registry?.Parameters.Any(p => p.Name.Equals(array.CountParameter, StringComparison.OrdinalIgnoreCase)) == true || design.Arrays.Any(p => p.CountParameter.Equals(array.CountParameter, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Nom du paramètre de nombre invalide, réservé ou déjà utilisé : " + array.CountParameter);
                    if (design.Parts.Any(p => p.Name.Equals(array.Name, StringComparison.OrdinalIgnoreCase)) || design.Arrays.Any(p => p.Name.Equals(array.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Nom de réseau en double : " + array.Name);
                    if (!design.Metadata.Materials.Any(m => m.Name == array.Material)) throw new InvalidOperationException("Matériau inconnu : " + array.Material);
                    if (a["rotation"] != null && a["rotation"].Type != JTokenType.Null)
                    {
                        var rotation = a["rotation"] as JObject; CodexFamilyDesign.Keys(rotation, "axis", "angle_parameter");
                        array.RotationAxis = "xyz".IndexOf(CodexFamilyDesign.String(rotation, "axis", 1), StringComparison.Ordinal);
                        array.AngleParameter = CodexFamilyDesign.String(rotation, "angle_parameter", 40);
                        if (array.RotationAxis < 0 || !design.Angles.Any(p => p.Name == array.AngleParameter)) throw new InvalidOperationException("Axe de rotation ou paramètre d'angle inconnu : " + array.Name);
                    }
                    design.Arrays.Add(array);
                }
            if (design.Parts.Count + design.Arrays.Count == 0) throw new InvalidOperationException("La famille doit contenir au moins une pièce ou un réseau.");
            var used = design.Parts.SelectMany(p => p.Expressions).Concat(design.Arrays.SelectMany(a => a.Expressions)).SelectMany(e => e.Terms.Keys).ToHashSet();
            if (design.Registry == null && source["connectors"] == null && design.Parameters.Any(p => !used.Contains(p.Name))) throw new InvalidOperationException("Chaque paramètre doit piloter au moins une coordonnée de géométrie.");
            if (design.Registry == null && design.Angles.Any(p => !design.Arrays.Any(a => a.AngleParameter == p.Name))) throw new InvalidOperationException("Chaque angle doit piloter la rotation d'un réseau.");
            if (design.Registry != null)
                foreach (var display in design.Registry.Displays)
                    if (!design.Parts.Any(p => p.Name == display.Component) && !design.Arrays.Any(a => a.Name == display.Component)) throw new InvalidOperationException("Composant de visibilité inconnu : " + display.Component);
            foreach (var values in design.TestCases()) design.ValidateAt(values);
            foreach (var array in design.Arrays)
                if (design.Registry == null && !design.TestCases().Any(v => array.Count(v) != array.Count(design.Initial))) throw new InvalidOperationException("Le test doit modifier le nombre de barres du réseau « " + array.Name + " » via span ou pitch.");
            design.Connectors = FamilyConnectorSpec.Parse(source, design);
            design.Symbols = FamilySymbolicSpec.Parse(source, design);
            return design;
        }
        private static LengthExpression[] ReadExpressions(JObject value, string key, int count, HashSet<string> names) => CodexFamilyDesign.Items(value, key, count, count).Select(t => LengthExpression.Parse(t as JObject, names)).ToArray();
        internal IEnumerable<Dictionary<string, double>> TestCases()
        {
            if (Registry != null) { foreach (var test in Registry.Cases()) yield return test.Numeric; yield break; }
            yield return Initial;
            foreach (var p in Parameters.Concat(Angles))
            {
                if (Arrays.Any(a => a.Span.Terms.ContainsKey(p.Name) || a.Pitch.Terms.ContainsKey(p.Name)))
                { var middle = Initial; middle[p.Name] = (p.Value + p.TestValue) / 2; yield return middle; }
                var values = Initial; values[p.Name] = p.TestValue; yield return values;
            }
            if (Parameters.Count + Angles.Count > 1) yield return Parameters.Concat(Angles).ToDictionary(p => p.Name, p => p.TestValue);
        }
        internal void ValidateAt(Dictionary<string, double> values)
        {
            if (Angles.Any(a => values[a.Name] < 1 || values[a.Name] > 89)) throw new InvalidOperationException("Angle hors de la plage de 1 à 89 degrés.");
            foreach (var array in Arrays)
            {
                double pitch = array.Pitch.Value(values), span = array.Span.Value(values);
                if (pitch < 1 || span < 1 || pitch > 100000 || span > 100000) throw new InvalidOperationException("Pas ou longueur utile invalide : " + array.Name);
                int count = array.Count(values);
                if (count < (array.SmallCounts ? 0 : 2) || count > 200) throw new InvalidOperationException("Nombre du réseau hors limites : " + array.Name);
                for (int axis = 0; axis < 3; axis++)
                {
                    double min = array.Min[axis].Value(values), max = array.Max[axis].Value(values);
                    if (max - min < 1 || Math.Abs(min) > 100000 || Math.Abs(max + (axis == array.Axis ? (count - 1) * pitch : 0)) > 100000) throw new InvalidOperationException("Dimensions de barre invalides : " + array.Name);
                }
                var corners = array.Corners(values, 0);
                if (corners.Max(p => p[array.Axis]) - corners.Min(p => p[array.Axis]) >= pitch) throw new InvalidOperationException("Les projections des barres se touchent ou se chevauchent : " + array.Name);
                if (array.Corners(values, Math.Max(2, count) - 1).Concat(corners).SelectMany(p => p).Any(p => Math.Abs(p) > 100000)) throw new InvalidOperationException("Encombrement incliné hors des limites : " + array.Name);
                foreach (var e in array.Min.Concat(new[] { LengthExpression.Combine(array.Min[array.Axis], array.Pitch) }))
                    if (!e.IsZero && (Math.Abs(e.Value(values)) < 0.001 || Math.Abs(e.Value(Initial)) < 0.001 || Math.Sign(e.Value(values)) != Math.Sign(e.Value(Initial))))
                        throw new InvalidOperationException("L'ancrage du réseau traverse l'origine : " + array.Name);
            }
            if (Parts.Count + Arrays.Sum(a => a.Count(values)) > 750) throw new InvalidOperationException("Limite de 750 solides, réseaux compris.");
            foreach (var part in Parts)
            {
                CodexProfileDesign.Validate(part, values);
                var min = part.Min.Select(e => e.Value(values)).ToArray(); var max = part.Max.Select(e => e.Value(values)).ToArray();
                if (Enumerable.Range(0, 3).Any(a => max[a] - min[a] < 1)) throw new InvalidOperationException("Pièce « " + part.Name + " » : épaisseur inférieure à 1 mm pendant un test.");
                foreach (var e in part.Expressions)
                {
                    double initial = e.Value(Initial), next = e.Value(values);
                    if (Math.Abs(next) > 100000) throw new InvalidOperationException("Pièce hors de la limite de 100 m : " + part.Name);
                    if (!e.IsZero && (Math.Abs(initial) < 0.001 || Math.Abs(next) < 0.001 || Math.Sign(initial) != Math.Sign(next))) throw new InvalidOperationException("Coordonnée variable nulle ou traversant l'origine : " + part.Name + ". Utiliser un repère stable, par exemple -Largeur/2 et Largeur/2.");
                }
                int[] uv = part.ProfileAxes;
                var holes = part.Openings.Select(h => new { Min = h.Min.Select(e => e.Value(values)).ToArray(), Max = h.Max.Select(e => e.Value(values)).ToArray() }).ToArray();
                for (int i = 0; i < holes.Length; i++)
                {
                    var h = holes[i];
                    if (Enumerable.Range(0, 2).Any(a => h.Max[a] - h.Min[a] < 1 || h.Min[a] - min[uv[a]] < 1 || max[uv[a]] - h.Max[a] < 1)) throw new InvalidOperationException("Ouverture hors du profil ou paroi trop fine : " + part.Name);
                    for (int j = 0; j < i; j++) if (!Enumerable.Range(0, 2).Any(a => h.Min[a] - holes[j].Max[a] >= 1 || holes[j].Min[a] - h.Max[a] >= 1)) throw new InvalidOperationException("Ouvertures superposées ou trop proches : " + part.Name);
                }
            }
        }
    }
    internal sealed class DrivingLength { internal string Name; internal double Value, TestValue; }
    internal sealed class ParametricArray
    {
        internal string Name, Material, CountParameter; internal int Axis;
        internal string QuantityParameter = ""; internal bool SmallCounts;
        internal int RotationAxis = -1; internal string AngleParameter;
        internal LengthExpression[] Min, Max; internal LengthExpression Span, Pitch;
        internal int Count(Dictionary<string, double> values) => string.IsNullOrEmpty(QuantityParameter) ? checked((int)Math.Floor(Span.Value(values) / Pitch.Value(values))) : checked((int)values[QuantityParameter]);
        internal string CountFormula => string.IsNullOrEmpty(QuantityParameter) ? "rounddown((" + Span.Formula() + ") / (" + Pitch.Formula() + "))" : QuantityParameter;
        internal IEnumerable<LengthExpression> Expressions => Min.Concat(Max).Concat(new[] { Span, Pitch });
        internal double Angle(Dictionary<string, double> values) => AngleParameter == null ? 0 : values[AngleParameter];
        internal double[][] Corners(Dictionary<string, double> values, int index)
        {
            var min = Min.Select(e => e.Value(values)).ToArray(); var size = Max.Select((e, a) => e.Value(values) - min[a]).ToArray();
            return Enumerable.Range(0, 8).Select(c => {
                var local = Enumerable.Range(0, 3).Select(a => (c & (1 << a)) == 0 ? 0 : size[a]).ToArray();
                var rotated = Rotate(local, RotationAxis, Angle(values));
                return rotated.Select((v, a) => v + min[a] + (a == Axis ? index * Pitch.Value(values) : 0)).ToArray();
            }).ToArray();
        }
        internal static double[] Rotate(double[] point, int axis, double degrees)
        {
            var result = (double[])point.Clone(); if (axis < 0) return result;
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            result[u] = point[u] * cos - point[v] * sin; result[v] = point[u] * sin + point[v] * cos;
            return result;
        }
    }
    internal sealed class ParametricOpening { internal LengthExpression[] Min, Max; }
    internal sealed class ParametricPart
    {
        internal string Name, Material; internal int Axis; internal LengthExpression[] Min, Max;
        internal List<ParametricOpening> Openings = new List<ParametricOpening>();
        internal LengthExpression[][] Profile;
        internal int[] ProfileAxes => Enumerable.Range(0, 3).Where(a => a != Axis).ToArray();
        internal IEnumerable<LengthExpression> Expressions => Min.Concat(Max).Concat(Openings.SelectMany(h => h.Min.Concat(h.Max))).Concat(Profile == null ? Enumerable.Empty<LengthExpression>() : Profile.SelectMany(p => p));
    }
    internal sealed class LengthExpression
    {
        internal double Offset;
        internal readonly SortedDictionary<string, double> Terms = new SortedDictionary<string, double>(StringComparer.Ordinal);
        internal bool IsZero => Offset == 0 && Terms.Count == 0;
        internal double Value(Dictionary<string, double> values) => Offset + Terms.Sum(t => values[t.Key] * t.Value);
        internal static LengthExpression Combine(LengthExpression first, LengthExpression second, double factor = 1)
        {
            var result = new LengthExpression { Offset = first.Offset + second.Offset * factor };
            foreach (var key in first.Terms.Keys.Concat(second.Terms.Keys).Distinct())
            {
                first.Terms.TryGetValue(key, out double a); second.Terms.TryGetValue(key, out double b);
                double value = a + b * factor; if (value != 0) result.Terms.Add(key, value);
            }
            return result;
        }
        internal LengthExpression Scaled(double factor)
        {
            var result = new LengthExpression { Offset = Offset * factor };
            foreach (var term in Terms) if (term.Value * factor != 0) result.Terms.Add(term.Key, term.Value * factor);
            return result;
        }
        internal static LengthExpression Parse(JObject value, HashSet<string> names)
        {
            CodexFamilyDesign.Keys(value, "offset_mm", "terms");
            var expression = new LengthExpression { Offset = CodexFamilyDesign.Scalar(value["offset_mm"], "offset_mm", -100000, 100000) };
            foreach (var token in CodexFamilyDesign.Items(value, "terms", 0, 8))
            {
                var term = token as JObject; CodexFamilyDesign.Keys(term, "parameter", "factor");
                string name = CodexFamilyDesign.String(term, "parameter", 40);
                if (!names.Contains(name)) throw new InvalidOperationException("Paramètre de formule inconnu : " + name);
                if (expression.Terms.ContainsKey(name)) throw new InvalidOperationException("Terme de formule en double : " + name);
                double factor = CodexFamilyDesign.Scalar(term["factor"], "factor", -1000, 1000);
                if (factor != 0) expression.Terms.Add(name, factor);
            }
            return expression;
        }
        internal string Formula(double sign = 1)
        {
            string formula = (Offset * sign).ToString("0.################", CultureInfo.InvariantCulture) + " mm";
            foreach (var term in Terms) { double factor = term.Value * sign; formula += (factor >= 0 ? " + " : " - ") + term.Key + " * " + Math.Abs(factor).ToString("0.################", CultureInfo.InvariantCulture); }
            return formula;
        }
    }
}
