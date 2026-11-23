using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using BIMaestro.VideoGames;

namespace BIMaestro.Navisworks.Services
{
    internal sealed class NavisworksBimMetadata
    {
        private static readonly Regex IfcClass = new Regex(@"\bIfc(?:Wall\w*|Slab\w*|Covering\w*|BuildingStorey|Door\w*|Window\w*|Beam\w*|Column\w*|Pipe\w*|Duct\w*|Flow\w*|Distribution\w*|Roof\w*|BuildingElementProxy|Member\w*|Footing\w*|Ramp\w*|Stair\w*|Space|Plate\w*|Cable\w*|SanitaryTerminal\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex Generic = new Regex(@"^(?:mesh|maillage|geometry|géométrie|fragment|body|solid|solide|item|élément|element|objet|object)(?:\s*\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public string Name = "";
        public string Category = "";
        public string Type = "";
        public string Level = "";
        public string Identity = "";
        public string SourceId = "";
        public string IfcType = "";
        private string _predefined = "";
        public GameElementData Element;

        public bool IsStorey => IfcType.Equals("IfcBuildingStorey", StringComparison.OrdinalIgnoreCase);
        public bool HasObjectIdentity => SourceId.Length > 0;

        public static NavisworksBimMetadata Read(ModelItem item)
        {
            var metadata = new NavisworksBimMetadata { Name = Meaningful(item.DisplayName) };
            metadata.ReadIfcClass(item.ClassName);
            metadata.ReadIfcClass(item.ClassDisplayName);
            metadata.ReadIfcClass(metadata.Name);
            foreach (var tab in item.PropertyCategories)
            {
                metadata.ReadIfcClass(tab.Name);
                metadata.ReadIfcClass(tab.DisplayName);
                foreach (var property in tab.Properties)
                {
                    var name = property.DisplayName ?? "";
                    var key = property.Name ?? "";
                    if (!IsUseful(name) && !IsUseful(key)) continue;
                    var value = property.Value;
                    if (value == null || value.IsNone) continue;
                    var label = (value.IsDisplayString ? value.ToDisplayString()
                        : value.IsIdentifierString ? value.ToIdentifierString() : value.ToString())?.Trim();
                    if (string.IsNullOrEmpty(label) || label.Length > 512) continue;
                    metadata.ReadProperty(IsUseful(name) ? name : key, label);
                }
            }
            metadata.Category = ToCategory(metadata.Category, metadata.IfcType, metadata._predefined);
            if (metadata.Category.Length == 0)
                metadata.Category = ToCategory(item.ClassDisplayName, "", "");
            if (metadata.Category.Length == 0)
            {
                string inferredCategory = ToCategory(metadata.Type, "", "");
                if (inferredCategory == "Murs" || inferredCategory == "Sols") metadata.Category = inferredCategory;
            }
            return metadata;
        }

        internal static string Meaningful(string value) =>
            string.IsNullOrWhiteSpace(value) || Generic.IsMatch(value.Trim()) ? "" : value.Trim();

        private static bool IsUseful(string key) => KeyKind(key) != 0;

        // Autodesk's internal identifiers and the common labels from IFC and Revit readers.
        private static int KeyKind(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "name": case "nom": case "lcopitemname": case "object name": return 1;
                case "category": case "catégorie": case "categorie": case "revit category": return 2;
                case "type": case "type name": case "nom du type": case "family and type": case "famille et type":
                case "lcopitemtype": case "objecttype": case "object type": case "type d'objet": return 3;
                case "level": case "niveau": case "reference level": case "niveau de référence":
                case "base level": case "niveau de base": case "base constraint": case "contrainte inférieure":
                case "storey": case "étage": return 4;
                case "globalid": case "global id": case "ifc guid": case "ifcguid": case "unique id":
                case "uniqueid": return 5;
                case "element id": case "id d’élément": case "id d'élément": case "lcrevititemid": return 6;
                case "predefinedtype": case "predefined type": case "type prédéfini": return 7;
                case "entity": case "entité": case "ifc class": case "classe ifc": case "ifc entity": return 8;
                default: return 0;
            }
        }

        private void ReadProperty(string key, string value)
        {
            switch (KeyKind(key))
            {
                case 1:
                    string name = Meaningful(value);
                    if (name.Length > 0 && (Name.Length == 0 || IfcClass.IsMatch(Name))) Name = name;
                    break;
                case 2: if (Category.Length == 0) Category = value; ReadIfcClass(value); break;
                case 3: if (Type.Length == 0) Type = Meaningful(value); ReadIfcClass(value); break;
                case 4: if (Level.Length == 0) Level = value; break;
                case 5: if (Identity.Length == 0) Identity = value; SourceId = Identity; break;
                case 6: if (SourceId.Length == 0) SourceId = value; break;
                case 7: if (_predefined.Length == 0) _predefined = value; break;
                case 8: ReadIfcClass(value); break;
            }
        }

        private void ReadIfcClass(string value)
        {
            if (IfcType.Length > 0 || string.IsNullOrEmpty(value)) return;
            var match = IfcClass.Match(value);
            if (match.Success) IfcType = match.Value;
        }

        internal static string ToCategory(string category, string entity, string predefined)
        {
            string type = entity.ToLowerInvariant();
            if (type.StartsWith("ifcwall")) return "Murs";
            if (type.StartsWith("ifcslab")) return predefined.Trim('.').Equals("ROOF", StringComparison.OrdinalIgnoreCase) ? "Toitures" : "Sols";
            if (type.StartsWith("ifcroof")) return "Toitures";
            if (type.StartsWith("ifccovering") && predefined.Trim('.').Equals("FLOORING", StringComparison.OrdinalIgnoreCase)) return "Sols";
            switch (category?.Trim().ToLowerInvariant())
            {
                case "mur": case "murs": case "wall": case "walls": case "ost_walls": return "Murs";
                case "sol": case "sols": case "floor": case "floors": case "ost_floors":
                case "dalle": case "dalles": case "slab": case "slabs": case "plancher": case "planchers": return "Sols";
            }
            return Meaningful(category);
        }

        public GameElementData MakeElement(int index, string lot, string inheritedLevel, string modelKey)
        {
            string category = Category.Length > 0 ? Category : IfcType;
            var element = new GameElementData
            {
                Key = "navisworks:bim:" + index,
                StableKey = SourceId.Length > 0 ? modelKey + "|" + SourceId : "",
                ElementId = int.TryParse(SourceId, out int sourceNumber) ? sourceNumber : index + 1,
                WebElementIndex = index,
                Name = Name.Length > 0 ? Name : category.Length > 0 ? category : "Objet " + (index + 1),
                Category = category,
                TypeName = Type.Length > 0 ? Type : IfcType,
                LevelName = Level.Length > 0 ? Level : inheritedLevel,
                DocumentTitle = lot
            };
            if (IfcType.Length > 0) element.WebProperties["Classe IFC"] = IfcType;
            if (SourceId.Length > 0) element.WebProperties["Identifiant source"] = SourceId;
            if (_predefined.Length > 0) element.WebProperties["Type prédéfini"] = _predefined;
            return element;
        }
    }
}
