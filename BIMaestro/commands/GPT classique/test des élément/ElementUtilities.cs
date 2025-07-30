using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace IA
{
    public static class ElementUtilities
    {
        public static void Initialize(UIApplication uiApp)
        {
            // plus d'initialisation nécessaire
        }

        public static string GetElementMaterials(Element element)
        {
            var mats = new List<string>();
            foreach (ElementId matId in element.GetMaterialIds(false))
                if (element.Document.GetElement(matId) is Material m)
                    mats.Add(m.Name);
            return mats.Count > 0
                ? string.Join(", ", mats)
                : "Aucun matériau";
        }

        public static string GetCustomParameters(Element element)
        {
            var sb = new StringBuilder();
            // On prépare par réflexion les types qu'on va réutiliser
            Assembly revitAsm = typeof(Element).Assembly;
            // Type GroupTypeId (Revit2025+)
            Type groupTypeIdType = revitAsm.GetType("Autodesk.Revit.DB.GroupTypeId");
            object geometryGroupTypeId = null;
            if (groupTypeIdType != null)
            {
                // Lecture de la propriété statique Geometry
                geometryGroupTypeId = groupTypeIdType
                    .GetProperty("Geometry", BindingFlags.Public | BindingFlags.Static)?
                    .GetValue(null);
            }

            // Type BuiltInParameterGroup enum (Revit2023/2024)
            Type bipgEnumType = revitAsm.GetType("Autodesk.Revit.DB.BuiltInParameterGroup");
            string pgGeometryName = bipgEnumType != null
                ? "PG_GEOMETRY"
                : null;

            foreach (Parameter p in element.Parameters)
            {
                bool isGeomGroup = false;
                var def = p.Definition;
                var defType = def.GetType();

                // 1) Essayer GetGroupTypeId() (Revit2025+)
                MethodInfo mi = defType.GetMethod("GetGroupTypeId", Type.EmptyTypes);
                if (mi != null && geometryGroupTypeId != null)
                {
                    // Invocation
                    object raw = mi.Invoke(def, null);
                    // Si c'est un GroupTypeId on compare par égalité
                    if (geometryGroupTypeId.Equals(raw))
                        isGeomGroup = true;
                    // Sinon, s'il s'agit d'un ElementId (cas très rare), on compare l'IntegerValue à 2702 (PG_GEOMETRY)
                    else if (raw is ElementId eid && eid.IntegerValue == 2702)
                        isGeomGroup = true;
                }
                else
                {
                    // 2) Fallback pour Revit2023/2024 via la propriété enum ParameterGroup
                    PropertyInfo pi = defType.GetProperty("ParameterGroup", BindingFlags.Public | BindingFlags.Instance);
                    if (pi != null && pgGeometryName != null)
                    {
                        object raw = pi.GetValue(def, null);
                        if (raw != null && raw.ToString() == pgGeometryName)
                            isGeomGroup = true;
                    }
                }

                if (isGeomGroup && p.HasValue)
                {
                    sb.AppendLine($"- {def.Name}: {GetParameterValue(p, element.Document)}");
                }
            }

            return sb.Length > 0
                ? sb.ToString()
                : "Aucun paramètre dans 'Geometry' trouvé";
        }

        private static string GetParameterValue(Parameter p, Document doc)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? "N/A";
                case StorageType.Double:
                    return p.AsValueString() ?? "N/A";
                case StorageType.Integer:
                    return p.AsInteger().ToString();
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    if (id == ElementId.InvalidElementId) return "N/A";
                    var e = doc.GetElement(id);
                    return e?.Name ?? id.IntegerValue.ToString();
                default:
                    return "N/A";
            }
        }
    }
}
