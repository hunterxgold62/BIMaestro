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
            yield return Tool("revit_configure_family", "Configure la famille OUVERTE, manuelle ou BIMaestro, sans reconstruction : paramètres length/angle/integer/number/yesno/text/material, portée instance/type, formules natives Revit, types nommés et associations aux éléments. Lire revit_inspect_family et revit_inspect_family_element. parameters.mode=add crée, reuse conserve, update modifie explicitement portée/valeur/formule. formula=null conserve, chaîne vide supprime ; value=null conserve/calculée. Valeur initiale des nouveaux paramètres appliquée à tous les types ; update.value au type courant seulement. shared_guid vide pour interne, GUID explicite pour partagé. bindings.property=visibility pour la case Visible, dimension_label pour une cote, sinon parameter_id exact lu sur l'élément. family_parameter vide dissocie. replace_associations=true seulement si remplacement/dissociation demandé. Exemple service de table : ajouter un yesno instance=true, value=true, group=visibility, puis associer visibility de TOUS les éléments concernés au même paramètre. Aucun filtre sur l'origine ou la classe des éléments : compatibilité vérifiée par Revit. Un Ctrl+Z annule le lot. Les unités des valeurs sont mm/degrés ; les formules utilisent mm/deg ; material.value=unique_id du matériau existant. Aucun enregistrement/rechargement du projet.", FamilyConfigurationEdit.Properties());
            yield return Tool("revit_inspect_family_element", "Lit tous les paramètres réels d'un élément de famille, leur parameter_id, type, association existante et possibilité d'association. Fonctionne aussi sur formes libres, familles imbriquées, courbes, connecteurs et éléments manuels. À utiliser avant revit_configure_family pour des propriétés autres que visibility.", new JObject {
                ["document_key"] = new JObject { ["type"] = "string", ["maxLength"] = 100 },
                ["element_unique_id"] = new JObject { ["type"] = "string", ["maxLength"] = 100 } });
            yield return Tool("revit_inspect_family", "Inspecte la famille ouverte, y compris une famille manuelle : paramètres réels, inventaire paginé des formes, courbes, plans, cotes et connecteurs, encombrements, profils et limites des extrusions. Retourne document_key et unique_id à réutiliser exactement. Lire toutes les pages utiles avant une modification ; ce n'est pas une extraction complète de la logique constructive.", new JObject {
                ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 1000000 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 } });
            yield return Tool("revit_edit_family_representation", "Ajoute, remplace ou retire un groupe nommé de dessins dans la famille OUVERTE, sans nouveau RFA. Lire revit_inspect_family avant. add exige un nouveau group_name ; replace/remove ne ciblent que les dessins marqués par cet outil sous ce nom, jamais les dessins manuels. representation_2d=null pour remove, description complète du groupe sinon. hide_model_in=[] obligatoire pour conserver la visibilité existante. Courbes/régions fixes, sans liaison aux dimensions. Un Ctrl+Z annule tout le lot ; aucun enregistrement automatique.", FamilyRepresentationEdit.Properties());
            yield return Tool("revit_edit_family_extrusion", "Modifie les limites début/fin d'une extrusion existante, pleine ou vide, dans la famille ouverte. Lire revit_inspect_family : document_key et element_unique_id exacts, start_mm/end_mm selon l'axe du profil. Conserve son profil et son identité ; profondeur >=1 mm. Refuse les limites associées à des paramètres : utiliser alors revit_set_family_parameters. Ne change ni formules ni contraintes. Un Ctrl+Z, aucun enregistrement automatique.", FamilyExtrusionEdit.Properties());
            yield return Tool("revit_set_family_category", "Change la catégorie métier de la famille ouverte, sans recréer sa géométrie ni enregistrer le fichier. Lire revit_family_parameters avant. Ne change PAS son hébergement (sol, mur, indépendant...) et n'ajoute aucun connecteur. Revit peut refuser une catégorie incompatible. Transaction annulable.", new JObject { ["category"] = CodexFamilyDesign.CategorySchema() });
            yield return Tool("revit_cut_floor_with_family", "Découpe le sol sélectionné avec les vides non attachés de la famille placée sélectionnée. Sélectionner exactement un sol et une instance dans le projet. La famille doit autoriser Couper avec des vides au chargement ; host_opening avec hosting=floor prépare ce vide. Tous les vides non attachés de cette instance participent à la découpe. Contrôle la réduction du volume, opération annulable sans enregistrer le projet. Ne place pas la famille et ne modifie aucun autre sol.", new JObject());
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
                rectangular_extrusions = true, polygonal_parametric_profiles = true, rectangular_openings = true, native_wall_host_openings = true, parametric_wall_host_openings = true,
                floor_host_voids = true, parametric_floor_host_voids = true, selected_floor_cut_with_family = true, nested_rectangular_arrays = true, rectangular_component_grids = true, template_host_measurement = true, visible_array_count_range = new[] { 0, 200 },
                hosting_templates = new[] { "auto", "free", "face", "wall", "floor", "ceiling", "work_plane" },
                small_counts = "V1 : visibilité conditionnelle, géométries cachées conservées pour compatibilité Revit 2023+.",
                symbolic_parametric_rectangles = true, free_symbolic_lines_arcs_circles = true, model_lines_arcs_circles = true, filled_regions = true, masking_regions = true, model_visibility_by_view_plane = true, face_centered_mep_connectors = true, native_validation_without_save = true,
                array_angle_range_degrees = new[] { 0, 180 }, live_parameter_inspection = true, batch_parameter_edit = true,
                family_categories = CodexFamilyDesign.Categories, edit_open_family_category = true,
                live_family_element_inventory = true, edit_open_family_drawing_groups = true, edit_unassociated_extrusion_extents = true,
                configure_existing_family_parameters = true, existing_family_formulas = true, existing_family_parameter_associations = true,
                instance_visibility_switches_on_existing_elements = true, existing_family_named_types = true },
            native_validation = "Essais de variation pendant chaque création. Pas de certification générale de toutes les combinaisons par la seule compilation.",
            limitations = new[] { "Extrusions selon X/Y/Z : profils rectangulaires ou polygonaux droits pilotés par leurs sommets ; barres arrays inclinables de 0 à 180 degrés.", "Les pièces cachées doivent rester géométriquement valides.", "Les paramètres existants compatibles sont réutilisés sans distinction de majuscules. Pour un paramètre partagé existant, fournir son GUID. Un conflit de type, de formule ou de portée non convertible exige un autre nom ; ne pas réessayer le même nom.",
                "Connecteurs au centre d'une face d'une pièce pleine, avec section paramétrique. Les réglages électriques de puissance/tension ne sont pas exposés.",
                "Pas encore de profils courbes, lofts, balayages ou révolutions paramétriques, de réseaux radiaux ni de composants adaptatifs à points.",
                "component_grids : grilles rectangulaires de modules composés de blocs à dimensions fixes, matériaux distincts et niveaux de détail. Deux axes orthogonaux, au plus 200 modules visibles / 750 solides. Les dimensions disponibles pilotent les nombres sans déformer les modules. Pas d'import arbitraire d'une famille externe ni de rotation individuelle des modules dans cette première version.",
                "Les comptes 0/1 portent sur les éléments visibles, avec géométries cachées conservées ; ce ne sont pas des réseaux natifs de zéro ou un membre.",
                "host_opening : wall=min_xz/max_xz pour une baie native de mur ; floor=min_xyz/max_xyz pour un vide rectangulaire pilotable. Le vide de sol doit couvrir l'épaisseur de l'hôte. Après placement, sélectionner sol+instance puis revit_cut_floor_with_family, ou Couper la géométrie dans Revit. Pas de découpe automatique à la pose. Autres hôtes et contours courbes non pris en charge. Recréer les anciennes familles pour ajouter ce vide.",
                "Les gabarits hébergés exigent place_at_origin=false : le choix de l'hôte et le placement se font ensuite dans le projet. Catégorie métier et hébergement sont indépendants ; changer la catégorie ne convertit pas le gabarit. Le sol du gabarit reste non découpé dans l'éditeur avec le vide non attaché actuel.",
                "representation_2d : lignes/arcs/cercles symboliques ou de modèle, régions à un contour fermé simple (uni, hachure diagonale, masque). Coordonnées fixes, sans association aux dimensions ; symbolic_outlines reste disponible pour les rectangles paramétriques. Les lignes de modèle restent visibles en 3D.",
                "Édition de familles existantes : dessins ajoutés/remplacés par groupes identifiés, sans masquage automatique de la 3D ; limites des extrusions non associées modifiables. Profils existants et dessins manuels non réécrits ; paramètres, formules, portée et associations configurables par revit_configure_family. Les dessins libres restent fixes lors du redimensionnement.",
                "La géométrie détaillée FreeForm reste fixe : nouvelle version nécessaire pour la reconstruire avec des contraintes." } };
        internal static object Read(Document doc)
        {
            if (!doc.IsFamilyDocument) throw new InvalidOperationException("Ouvrez une famille pour lire ses paramètres.");
            var manager = doc.FamilyManager; var type = manager.CurrentType;
            var family = doc.OwnerFamily;
            var categoryCode = CodexFamilyDesign.Categories.FirstOrDefault(c => Category.GetCategory(doc, CodexFamilyBuilder.CategoryId(c))?.Id == family.FamilyCategory?.Id);
            return new { category = categoryCode, category_name = family.FamilyCategory?.Name,
                placement_type = family.FamilyPlacementType.ToString(), hosting_behavior = family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger(),
                current_type = type?.Name, types = manager.Types.Cast<FamilyType>().Take(64).Select(t => t.Name).ToArray(),
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
        internal static object SetCategory(Document doc, JObject args, Func<string, string, bool> confirm,
            Func<string, Transaction> transactionFactory, Action<Transaction> commit)
        {
            CodexFamilyDesign.Keys(args, "category");
            string code = CodexFamilyDesign.String(args, "category", 20);
            if (!CodexFamilyDesign.Categories.Contains(code)) throw new InvalidOperationException("Catégorie de famille non prise en charge.");
            var target = Category.GetCategory(doc, CodexFamilyBuilder.CategoryId(code));
            if (target == null) throw new InvalidOperationException("Catégorie indisponible dans cette version de Revit.");
            string previous = doc.OwnerFamily.FamilyCategory?.Name;
            if (doc.OwnerFamily.FamilyCategory?.Id == target.Id)
                return new { changed = false, category = code, category_name = target.Name, saved = false };
            if (!confirm("Changer la catégorie de la famille", previous + " → " + target.Name + ". L'hébergement reste identique. Un Ctrl+Z annule le changement."))
                throw new InvalidOperationException("Changement de catégorie refusé. Ne pas réessayer sans nouvelle demande.");
            var connectors = new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).ToElementIds().ToArray();
            using (var transaction = transactionFactory("Codex — catégorie de famille"))
            {
                doc.OwnerFamily.FamilyCategory = target;
                doc.Regenerate();
                if (doc.OwnerFamily.FamilyCategory?.Id != target.Id || connectors.Any(id => doc.GetElement(id) == null))
                    throw new InvalidOperationException("Le changement de catégorie n'a pas conservé les connecteurs existants ; modification annulée.");
                commit(transaction);
            }
            return new { changed = true, previous_category = previous, category = code, category_name = target.Name, saved = false,
                note = "Hébergement conservé. La description construction.json reste celle de la création initiale." };
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
                if (spec.Kind == "angle" && (value.Number < 0 || value.Number > 180)) throw new InvalidOperationException("Angles pris en charge : 0 à 180 degrés.");
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
