using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexFamilyTools
    {
        private static JObject Tool(string name, string description, JObject properties) => new JObject { ["type"] = "function", ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(properties.Properties().Select(p => p.Name)), ["additionalProperties"] = false } };
        internal static IEnumerable<JObject> Definitions()
        {
            yield return Tool("revit_family_contract", "Lit le schéma JSON complet du moteur paramétrique. Consulter avant la première description paramétrique et après toute erreur de format ; corriger toute la description en une passe. Aucune lecture du modèle.", new JObject());
            yield return Tool("revit_test_family_engine", "Exécute les scénarios de validation V1 intégrés dans des familles temporaires : paramètres et seuils, types, répétitions 0/1 par visibilité, angles, ouvertures, connecteurs et contours 2D. Ne modifie pas le projet, ne sauvegarde aucune famille, écrit seulement un rapport local. Peut prendre plusieurs minutes ; utiliser pour une vérification demandée du moteur.", new JObject());
            yield return Tool("revit_capabilities", "Lit les capacités et limites réelles de la passerelle et la version de Revit. Appeler avant de concevoir une famille ou d'annoncer une limitation. Distingue code implémenté et validation native.", new JObject());
            yield return Tool("revit_family_parameters", "Lit les paramètres réels de la famille ouverte : valeurs du type courant, formules, portée type/occurrence, GUID partagés et types nommés. Reflète les modifications manuelles, contrairement au descriptif enregistré.", new JObject());
            yield return Tool("revit_set_family_parameters", "Modifie plusieurs paramètres existants du type courant dans une transaction annulable. Lire revit_family_parameters avant. Longueurs en mm, angles en degrés, Oui/Non en true/false. Ne change pas les formules. type_name vide conserve le type courant. Dans l'éditeur de famille, les paramètres d'occurrence définissent leurs valeurs par défaut. Aucun enregistrement automatique.", new JObject {
                ["type_name"] = new JObject { ["type"] = "string", ["maxLength"] = 70 },
                ["values"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64, ["items"] = new JObject {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("name", "value"), ["properties"] = new JObject {
                        ["name"] = new JObject { ["type"] = "string", ["maxLength"] = 100 }, ["value"] = new JObject { ["anyOf"] = new JArray(
                            new JObject { ["type"] = "number" }, new JObject { ["type"] = "boolean" }, new JObject { ["type"] = "string", ["maxLength"] = 500 }) } } } } });
        }
        internal static object Capabilities(string version) => new {
            family_engine = "BIMaestro V1", revit_version = version,
            implemented = new { typed_parameters = true, existing_template_parameter_reuse = true, case_insensitive_parameter_matching = true, shared_parameter_reuse_by_guid = true, formulas = true, instance_parameters = true, named_types = true,
                shared_parameters_explicit_guid = true, conditional_visibility = true, coarse_medium_fine = true, view_direction_visibility = true,
                rectangular_extrusions = true, polygonal_parametric_profiles = true, rectangular_openings = true, native_wall_host_openings = true, parametric_wall_host_openings = true, nested_rectangular_arrays = true, visible_array_count_range = new[] { 0, 200 },
                hosting_templates = new[] { "auto", "free", "face", "wall", "floor", "ceiling", "work_plane" },
                small_counts = "V1 : visibilité conditionnelle, géométries cachées conservées pour compatibilité Revit 2023+.",
                symbolic_parametric_rectangles = true, face_centered_mep_connectors = true, native_validation_without_save = true,
                array_angle_range_degrees = new[] { 1, 89 }, live_parameter_inspection = true, batch_parameter_edit = true },
            native_validation = "Essais de variation pendant chaque création. Pas de certification générale de toutes les combinaisons par la seule compilation.",
            limitations = new[] { "Extrusions selon X/Y/Z : profils rectangulaires ou polygonaux droits pilotés par leurs sommets ; barres arrays inclinables de 1 à 89 degrés.", "Les pièces cachées doivent rester géométriquement valides.", "Les paramètres existants compatibles sont réutilisés sans distinction de majuscules. Pour un paramètre partagé existant, fournir son GUID. Un conflit de type, de formule ou de portée non convertible exige un autre nom ; ne pas réessayer le même nom.",
                "Connecteurs au centre d'une face d'une pièce pleine, avec section paramétrique. Les réglages électriques de puissance/tension ne sont pas exposés.",
                "Pas encore de profils courbes, lofts, balayages ou révolutions paramétriques, de réseaux radiaux/2D ni de composants adaptatifs à points.",
                "Les comptes 0/1 portent sur les éléments visibles, avec géométries cachées conservées ; ce ne sont pas des réseaux natifs de zéro ou un membre.",
                "host_opening crée une baie rectangulaire native dans un mur droit parallèle à X du gabarit, avec contrôle du volume découpé et des variations. Autres hôtes et contours courbes non pris en charge. Les anciennes familles doivent être recréées pour ajouter cette baie.",
                "Les gabarits hébergés exigent place_at_origin=false : le choix de l'hôte et le placement se font ensuite dans le projet.",
                "La géométrie détaillée FreeForm reste fixe : nouvelle version nécessaire pour la reconstruire avec des contraintes." } };
        internal static object Read(Document doc)
        {
            if (!doc.IsFamilyDocument) throw new InvalidOperationException("Ouvrez une famille pour lire ses paramètres.");
            var manager = doc.FamilyManager; var type = manager.CurrentType;
            return new { current_type = type?.Name, types = manager.Types.Cast<FamilyType>().Take(64).Select(t => t.Name).ToArray(),
                parameters = manager.Parameters.Cast<FamilyParameter>().OrderBy(p => p.Definition.Name.StartsWith("BIM_", StringComparison.Ordinal) ? 1 : 0).Take(150).Select(p => new {
                    name = p.Definition.Name, kind = Kind(p), instance = p.IsInstance, formula = Trim(p.Formula, 2000),
                    editable = !p.IsReadOnly && !p.IsDeterminedByFormula && string.IsNullOrEmpty(p.Formula) && Kind(p) != "unsupported",
                    shared_guid = p.IsShared ? p.GUID.ToString() : null, value = Value(type, p) }).ToArray() };
        }
        private static string Kind(FamilyParameter p)
        {
            var type = p.Definition.GetDataType();
            if (type == SpecTypeId.Length) return "length";
            if (type == SpecTypeId.Angle) return "angle";
            if (type == SpecTypeId.Boolean.YesNo) return "yesno";
            if (type == SpecTypeId.Int.Integer) return "integer";
            if (type == SpecTypeId.Number) return "number";
            if (type == SpecTypeId.String.Text) return "text";
            return "unsupported";
        }
        private static object Value(FamilyType type, FamilyParameter p)
        {
            if (type == null || !type.HasValue(p)) return null;
            switch (Kind(p))
            {
                case "length": return type.AsDouble(p) * 304.8;
                case "angle": return type.AsDouble(p) * 180 / Math.PI;
                case "number": return type.AsDouble(p);
                case "integer": return type.AsInteger(p);
                case "yesno": return type.AsInteger(p) == 1;
                case "text": return Trim(type.AsString(p), 500);
                default: return null;
            }
        }
        private static string Trim(string value, int max) => value == null || value.Length <= max ? value : value.Substring(0, max);
        internal static object Set(Document doc, JObject args, Func<string, string, bool> confirm, Func<string, Transaction> transactionFactory, Action<Transaction> commit)
        {
            CodexFamilyDesign.Keys(args, "type_name", "values");
            if (args["type_name"].Type != JTokenType.String) throw new InvalidOperationException("Nom de type attendu.");
            var manager = doc.FamilyManager; string typeName = (string)args["type_name"];
            var type = typeName.Length == 0 ? manager.CurrentType : manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == typeName);
            if (type == null) throw new InvalidOperationException("Type de famille introuvable.");
            var values = new Dictionary<FamilyParameter, FamilyValue>();
            foreach (var token in CodexFamilyDesign.Items(args, "values", 1, 64))
            {
                var item = token as JObject; CodexFamilyDesign.Keys(item, "name", "value");
                var parameter = manager.get_Parameter(CodexFamilyDesign.String(item, "name", 100));
                if (parameter == null || parameter.IsReadOnly || parameter.IsDeterminedByFormula || !string.IsNullOrEmpty(parameter.Formula) || Kind(parameter) == "unsupported") throw new InvalidOperationException("Paramètre absent, calculé ou non modifiable : " + item["name"]);
                if (values.ContainsKey(parameter)) throw new InvalidOperationException("Paramètre dupliqué dans le lot.");
                var spec = new FamilyParameterSpec { Name = parameter.Definition.Name, Kind = Kind(parameter) }; var value = spec.Read(item["value"]);
                if (spec.Kind == "angle" && (value.Number < 1 || value.Number > 89)) throw new InvalidOperationException("Angles pris en charge : 1 à 89 degrés.");
                values.Add(parameter, value);
            }
            string description = string.Join("\n", values.Select(v => v.Key.Definition.Name + " = " + DisplayValue(v.Value)));
            if (!confirm("Modifier " + values.Count + " paramètres — " + type.Name, description))
                throw new InvalidOperationException("Modification refusée. Ne pas réessayer sans nouvelle demande.");
            using (var transaction = transactionFactory("Codex — paramètres de famille"))
            {
                manager.CurrentType = type;
                foreach (var pair in values)
                {
                    var value = pair.Value;
                    if (value.Kind == "text") manager.Set(pair.Key, value.Text);
                    else if (value.Kind == "yesno") manager.Set(pair.Key, value.Boolean ? 1 : 0);
                    else if (pair.Key.StorageType == StorageType.Integer) manager.Set(pair.Key, checked((int)value.Number));
                    else manager.Set(pair.Key, value.LengthPower == 1 ? value.Number / 304.8 : value.AnglePower == 1 ? value.Number * Math.PI / 180 : value.Number);
                }
                doc.Regenerate(); commit(transaction);
            }
            return new { saved = false, undo = "Ctrl+Z", state = Read(doc) };
        }
        private static string DisplayValue(FamilyValue value) => value.Kind == "text" ? value.Text : value.Kind == "yesno" ? value.Boolean.ToString() : value.Number.ToString("g") + (value.LengthPower == 1 ? " mm" : value.AnglePower == 1 ? " degrés" : "");
    }
}
