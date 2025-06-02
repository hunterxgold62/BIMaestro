using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Text;

namespace IA
{
    public static class ElementUtilities
    {
        private static int _revitVersion = 2023; // Valeur par défaut

        // Méthode d'initialisation
        public static void Initialize(UIApplication uiApp)
        {
            _revitVersion = GetRevitVersionNumber(uiApp);
            // Optionnel : Afficher la version détectée pour le débogage
            // TaskDialog.Show("Revit Version", $"VersionNumber détectée : {_revitVersion}");
        }

        // Méthode pour obtenir le numéro de version majeure de Revit (ex: 2023, 2024)
        private static int GetRevitVersionNumber(UIApplication uiApp)
        {
            try
            {
                string versionString = uiApp.Application.VersionNumber; // VersionNumber est une chaîne
                if (int.TryParse(versionString, out int versionNumber))
                {
                    return versionNumber;
                }
                else
                {
                    throw new InvalidOperationException("Impossible de convertir VersionNumber en entier.");
                }
            }
            catch (Exception)
            {
                return 2023; // Valeur par défaut si la version ne peut pas être déterminée
            }
        }

        public static string GetElementMaterials(Element element)
        {
            ICollection<ElementId> materialIds = element.GetMaterialIds(false);
            List<string> materialNames = new List<string>();

            foreach (ElementId materialId in materialIds)
            {
                Material material = element.Document.GetElement(materialId) as Material;
                if (material != null)
                {
                    materialNames.Add(material.Name);
                }
            }

            return materialNames.Count > 0 ? string.Join(", ", materialNames) : "Aucun matériau";
        }

        public static string GetCustomParameters(Element element)
        {
            StringBuilder parameters = new StringBuilder();

            foreach (Parameter param in element.Parameters)
            {
                try
                {
                    // Vérification si le paramètre appartient au groupe "Cotes" ou "Geometry"
                    bool isInDimensionsGroup = false;

                    if (_revitVersion >= 27) // Revit 2024+
                    {
                        isInDimensionsGroup = param.Definition.GetGroupTypeId() == GroupTypeId.Geometry;
                    }
                    else // Revit 2023 ou plus anciennes
                    {
                        isInDimensionsGroup = param.Definition.ParameterGroup == BuiltInParameterGroup.PG_GEOMETRY;
                    }

                    // Si le paramètre appartient au groupe "Cotes"
                    if (isInDimensionsGroup && param.HasValue)
                    {
                        string paramName = param.Definition.Name;
                        string paramValue = GetParameterValue(param);

                        parameters.AppendLine($"- {paramName}: {paramValue}");
                    }
                }
                catch (Exception ex)
                {
                    // Gestion des erreurs éventuelles (par exemple si une méthode ou une propriété manque)
                    parameters.AppendLine($"Erreur avec le paramètre: {ex.Message}");
                }
            }

            return parameters.Length > 0 ? parameters.ToString() : "Aucun paramètre dans 'Cotes' trouvé";
        }

        private static string GetParameterValue(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "N/A"; // Si null, retourner "N/A"
                case StorageType.Double:
                    return param.AsValueString() ?? "N/A"; // Si null, retourner "N/A"
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.ElementId:
                    ElementId id = param.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId)
                    {
                        return "N/A"; // Identifier invalide
                    }

                    try
                    {
                        // Revit 2023 utilise uniquement IntegerValue
                        if (id.IntegerValue >= 0) // Vérifie que l'ID est valide
                        {
                            Element e = param.Element.Document.GetElement(id);
                            return e != null ? e.Name : id.IntegerValue.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Capturer les erreurs inattendues
                        return $"Erreur: {ex.Message}";
                    }

                    return "N/A";

                default:
                    return "N/A";
            }
        }

        public static string GetElementFloorAreaAndVolume(Element element, string category)
        {
            double floorArea = 0;
            double totalVolume = 0;

            // Toujours calculer la surface et le volume via la géométrie
            Options geomOptions = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geomElement = element.get_Geometry(geomOptions);
            if (geomElement != null)
            {
                foreach (GeometryObject geomObj in geomElement)
                {
                    ProcessGeometryObjectForFloorArea(geomObj, ref floorArea, ref totalVolume, category);
                }
            }

            // Conversion des unités et arrondi à deux décimales
            double floorAreaSqMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(floorArea, UnitTypeId.SquareMeters), 2);
            double totalVolumeCubicMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalVolume, UnitTypeId.CubicMeters), 2);

            StringBuilder sb = new StringBuilder();
            Document doc = element.Document;
            Units units = doc.GetUnits();

            if (floorAreaSqMeters > 0)
            {
                sb.AppendLine($"**Surface de Construction**: {floorAreaSqMeters} m²");
            }

            if (totalVolumeCubicMeters > 0)
            {
                sb.AppendLine($"**Volume**: {totalVolumeCubicMeters} m³");
            }

            return sb.Length > 0 ? sb.ToString() : string.Empty;
        }

        private static void ProcessGeometryObjectForFloorArea(GeometryObject geomObj, ref double floorArea, ref double totalVolume, string category)
        {
            if (geomObj is Solid solid)
            {
                if (solid.Volume > 0)
                {
                    totalVolume += solid.Volume;
                    floorArea += CalculateFloorArea(solid, category);
                }
            }
            else if (geomObj is GeometryInstance geomInstance)
            {
                foreach (GeometryObject instObj in geomInstance.GetInstanceGeometry())
                {
                    ProcessGeometryObjectForFloorArea(instObj, ref floorArea, ref totalVolume, category);
                }
            }
            else if (geomObj is GeometryElement geomElement)
            {
                foreach (GeometryObject childObj in geomElement)
                {
                    ProcessGeometryObjectForFloorArea(childObj, ref floorArea, ref totalVolume, category);
                }
            }
        }

        private static double CalculateFloorArea(Solid solid, string category)
        {
            double floorArea = 0;

            // Déterminer si on doit utiliser minZ ou maxZ selon la catégorie
            bool pickMaxZ = false;
            bool pickMinZ = false;

            string categoryLower = category.ToLower();

            if (categoryLower.Contains("toit"))
            {
                pickMaxZ = true;
            }
            else if (categoryLower.Contains("plancher") || categoryLower.Contains("floor"))
            {
                pickMinZ = true;
            }
            else
            {
                // Pour d'autres catégories, décider en conséquence. Par défaut, on prend minZ
                pickMinZ = true;
            }

            List<PlanarFace> targetFaces = new List<PlanarFace>();
            double targetZ = pickMaxZ ? double.MinValue : double.MaxValue;

            foreach (Face face in solid.Faces)
            {
                if (face is PlanarFace planarFace)
                {
                    XYZ normal = planarFace.FaceNormal;
                    if (Math.Abs(normal.Z) > 0.99) // Faces horizontales
                    {
                        // Calculer la moyenne des positions Z des sommets de la face
                        double averageZ = GetAverageZ(planarFace);

                        if (pickMaxZ)
                        {
                            if (averageZ > targetZ + 1e-3)
                            {
                                targetZ = averageZ;
                                targetFaces.Clear();
                                targetFaces.Add(planarFace);

                                // Debugging
                                // TaskDialog.Show("Debug", $"Toit: Nouvelle targetZ = {targetZ}, Area = {planarFace.Area}");
                            }
                            else if (Math.Abs(averageZ - targetZ) < 1e-3)
                            {
                                targetFaces.Add(planarFace);

                                // Debugging
                                // TaskDialog.Show("Debug", $"Toit: Ajout face à targetZ = {averageZ}, Area = {planarFace.Area}");
                            }
                        }
                        else if (pickMinZ)
                        {
                            if (averageZ < targetZ - 1e-3)
                            {
                                targetZ = averageZ;
                                targetFaces.Clear();
                                targetFaces.Add(planarFace);

                                // Debugging
                                // TaskDialog.Show("Debug", $"Plancher: Nouvelle targetZ = {targetZ}, Area = {planarFace.Area}");
                            }
                            else if (Math.Abs(averageZ - targetZ) < 1e-3)
                            {
                                targetFaces.Add(planarFace);

                                // Debugging
                                // TaskDialog.Show("Debug", $"Plancher: Ajout face à targetZ = {averageZ}, Area = {planarFace.Area}");
                            }
                        }
                    }
                }
            }

            // Additionner les aires des faces cibles
            foreach (var planarFace in targetFaces)
            {
                floorArea += planarFace.Area;
            }

            return floorArea;
        }

        private static double GetAverageZ(PlanarFace planarFace)
        {
            // Calculer la moyenne des positions Z des sommets de la face
            int numEdges = planarFace.EdgeLoops.Size;
            List<XYZ> points = new List<XYZ>();

            for (int i = 0; i < numEdges; i++)
            {
                EdgeArray edgeArray = planarFace.EdgeLoops.get_Item(i);
                foreach (Edge edge in edgeArray)
                {
                    Curve curve = edge.AsCurve();
                    if (curve is Line line)
                    {
                        points.Add(line.GetEndPoint(0));
                        points.Add(line.GetEndPoint(1));
                    }
                    else
                    {
                        // Pour d'autres types de courbes, extraire les points de contrôle si possible
                        // Sinon, ignorer
                        try
                        {
                            // Essayer d'obtenir les points de contrôle
                            XYZ p1 = curve.GetEndPoint(0);
                            XYZ p2 = curve.GetEndPoint(1);
                            points.Add(p1);
                            points.Add(p2);
                        }
                        catch
                        {
                            // Ignorer si impossible
                        }
                    }
                }
            }

            if (points.Count == 0)
                return 0;

            double sumZ = 0;
            foreach (XYZ pt in points)
            {
                sumZ += pt.Z;
            }

            return sumZ / points.Count;
        }

        internal static string GetElementFloorAreaAndVolume(Element element)
        {
            throw new NotImplementedException();
        }
    }
}
