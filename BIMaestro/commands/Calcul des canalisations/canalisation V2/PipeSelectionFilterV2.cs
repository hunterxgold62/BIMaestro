using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.IO;
using System.Diagnostics;

// Références pour EPPlus
using OfficeOpenXml;
using OfficeOpenXml.Style;
using OfficeOpenXml.Drawing.Chart;

// Références pour WPF
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MyRevitPluginV2
{
    [Transaction(TransactionMode.ReadOnly)]
    public class PipeLengthByDiameterCommandV2 : IExternalCommand
    {
        // Classe pour agréger les données par réseau
        private class NetworkAggregation
        {
            public Dictionary<double, double> PipeLengths = new Dictionary<double, double>();
            public Dictionary<double, double> PipeFittingLengths = new Dictionary<double, double>();
            public Dictionary<string, double> DuctLengths = new Dictionary<string, double>();
            public Dictionary<string, double> DuctFittingLengths = new Dictionary<string, double>();
            public Dictionary<double, double> PipeVolumes = new Dictionary<double, double>();
            public Dictionary<double, int> ElbowCounts = new Dictionary<double, int>();
            public Dictionary<double, int> TeeCounts = new Dictionary<double, int>();
            public Dictionary<double, (double DiametreInterieur, double DiametreExterieur)> DnToDiameters =
                new Dictionary<double, (double, double)>();
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Obtenir le document actif
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Autodesk.Revit.DB.Document doc = uidoc.Document;

            try
            {
                // Obtenir la liste des Types de système disponibles
                List<string> systemTypes = GetSystemTypes(doc);

                // Afficher la fenêtre WPF pour la sélection des options
                PipeSystemTypeSelectionWindowV2 selectionWindow = new PipeSystemTypeSelectionWindowV2(systemTypes);
                IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                WindowInteropHelper helper = new WindowInteropHelper(selectionWindow);
                helper.Owner = mainWindowHandle;

                bool? dialogResult = selectionWindow.ShowDialog();
                if (dialogResult != true)
                {
                    TaskDialog.Show("Information", "Opération annulée.");
                    return Result.Cancelled;
                }

                bool includeDucts = selectionWindow.IncludeDucts;
                bool filterBySystemType = selectionWindow.FilterBySystemType;
                List<string> selectedSystemTypes = selectionWindow.SelectedSystemTypes;
                bool exportToExcel = selectionWindow.ExportToExcel;

                ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0)
                {
                    TaskDialog.Show("Information", "Veuillez sélectionner des éléments avant de lancer le script.");
                    return Result.Cancelled;
                }

                // Déclaration des dictionnaires globaux
                Dictionary<double, double> pipeLengths = new Dictionary<double, double>();
                Dictionary<double, double> pipeFittingLengths = new Dictionary<double, double>();
                Dictionary<string, double> ductLengths = new Dictionary<string, double>();
                Dictionary<string, double> ductFittingLengths = new Dictionary<string, double>();
                Dictionary<double, int> elbowCounts = new Dictionary<double, int>();
                Dictionary<double, int> teeCounts = new Dictionary<double, int>();
                Dictionary<double, (double DiametreInterieur, double DiametreExterieur)> dnToDiameters =
                    new Dictionary<double, (double, double)>();
                Dictionary<double, double> pipeVolumes = new Dictionary<double, double>();
                // === Accessoires de canalisation ===
                
                var pipeAccessoryCounts = new Dictionary<string, int>();


                // Agrégation par réseau (clé = nom complet du réseau)
                Dictionary<string, NetworkAggregation> networkAggregates = new Dictionary<string, NetworkAggregation>();
                // Nouveau : couleur de chaque réseau
                var networkColors = new Dictionary<string, System.Drawing.Color>();

                // Traitement des éléments
                foreach (ElementId id in selectedIds)
                {
                    Element elem = doc.GetElement(id);


                    // Filtrer par système sauf pour les accessoires de canalisation
                    bool isPipeAccessory = elem is FamilyInstance fii
                           && fii.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory;
                       if (filterBySystemType && !isPipeAccessory)
                           {
                        string systemTypeName = GetElementSystemTypeName(elem);
                               if (systemTypeName == null || !selectedSystemTypes.Contains(systemTypeName))
                                       continue;
                           }

                    // Récupérer le nom du réseau
                    string networkName = GetElementSystemTypeName(elem);
                    NetworkAggregation netAgg = null;
                    if (!string.IsNullOrEmpty(networkName))
                    {
                        if (!networkAggregates.ContainsKey(networkName))
                            networkAggregates[networkName] = new NetworkAggregation();
                        netAgg = networkAggregates[networkName];
                    }
                    // Remplir networkColors au premier tuyau rencontré
                    if (!string.IsNullOrEmpty(networkName)
    && !networkColors.ContainsKey(networkName)
    && elem is Pipe pipeForColor)
                    {
                        Autodesk.Revit.DB.Color revitClr = null;

                        // 1) On récupère l’ID du PipingSystemType
                        var sysTypeId = pipeForColor
                            .get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                            ?.AsElementId() ?? ElementId.InvalidElementId;

                        // 2) Si valide, on prend la couleur du type de système
                        if (sysTypeId != ElementId.InvalidElementId)
                        {
                            var pst = doc.GetElement(sysTypeId) as PipingSystemType;
                            if (pst != null && pst.LineColor.IsValid)
                                revitClr = pst.LineColor;
                        }

                        // 3) Fallback : couleur du matériau
                        if (revitClr == null || !revitClr.IsValid)
                        {
                            var matId = pipeForColor
                                .get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)
                                ?.AsElementId() ?? ElementId.InvalidElementId;
                            if (matId != ElementId.InvalidElementId)
                            {
                                var mat = doc.GetElement(matId) as Material;
                                if (mat != null && mat.Color.IsValid)
                                    revitClr = mat.Color;
                            }
                        }

                        // 4) Conversion en System.Drawing.Color ou gris par défaut
                        if (revitClr != null && revitClr.IsValid)
                        {
                            networkColors[networkName] = System.Drawing.Color.FromArgb(
                                revitClr.Red, revitClr.Green, revitClr.Blue);
                        }
                        else
                        {
                            networkColors[networkName] = System.Drawing.Color.LightGray;
                        }
                    

                }

                    // --- Traitement pour les canalisations ---
                    if (elem is Pipe pipe)
                    {
                        double diametre = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                        if (diametre == 0) continue;
                        diametre = UnitUtils.ConvertFromInternalUnits(diametre, UnitTypeId.Millimeters);

                        double diametreInterieur = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM)?.AsDouble() ?? 0;
                        if (diametreInterieur == 0) continue;
                        double diametreInterieur_mm = UnitUtils.ConvertFromInternalUnits(diametreInterieur, UnitTypeId.Millimeters);

                        double diametreExterieur = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() ?? 0;
                        if (diametreExterieur == 0) continue;
                        double diametreExterieur_mm = UnitUtils.ConvertFromInternalUnits(diametreExterieur, UnitTypeId.Millimeters);

                        if (!dnToDiameters.ContainsKey(diametre))
                            dnToDiameters[diametre] = (diametreInterieur_mm, diametreExterieur_mm);
                        if (netAgg != null && !netAgg.DnToDiameters.ContainsKey(diametre))
                            netAgg.DnToDiameters[diametre] = (diametreInterieur_mm, diametreExterieur_mm);

                        double longueur = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble();
                        longueur = UnitUtils.ConvertFromInternalUnits(longueur, UnitTypeId.Meters);

                        if (pipeLengths.ContainsKey(diametre))
                            pipeLengths[diametre] += longueur;
                        else
                            pipeLengths[diametre] = longueur;
                        if (netAgg != null)
                        {
                            if (netAgg.PipeLengths.ContainsKey(diametre))
                                netAgg.PipeLengths[diametre] += longueur;
                            else
                                netAgg.PipeLengths[diametre] = longueur;
                        }

                        double diametreInterieur_m = UnitUtils.ConvertFromInternalUnits(diametreInterieur, UnitTypeId.Meters);
                        double volume = Math.PI * Math.Pow(diametreInterieur_m / 2, 2) * longueur;
                        if (pipeVolumes.ContainsKey(diametreInterieur_mm))
                            pipeVolumes[diametreInterieur_mm] += volume;
                        else
                            pipeVolumes[diametreInterieur_mm] = volume;
                        if (netAgg != null)
                        {
                            if (netAgg.PipeVolumes.ContainsKey(diametreInterieur_mm))
                                netAgg.PipeVolumes[diametreInterieur_mm] += volume;
                            else
                                netAgg.PipeVolumes[diametreInterieur_mm] = volume;
                        }
                    }
                    // --- Traitement pour les gaines ---
                    else if (includeDucts && elem is Duct duct)
                    {
                        ConnectorProfileType shape = duct.DuctType.Shape;
                        string dimensionKey = "";
                        if (shape == ConnectorProfileType.Round)
                        {
                            double diametre = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)?.AsDouble() ?? 0;
                            if (diametre == 0) continue;
                            diametre = UnitUtils.ConvertFromInternalUnits(diametre, UnitTypeId.Millimeters);
                            dimensionKey = $"Ø{diametre:F0} mm";
                        }
                        else if (shape == ConnectorProfileType.Rectangular)
                        {
                            double largeur = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0;
                            double hauteur = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0;
                            if (largeur == 0 || hauteur == 0) continue;
                            largeur = UnitUtils.ConvertFromInternalUnits(largeur, UnitTypeId.Millimeters);
                            hauteur = UnitUtils.ConvertFromInternalUnits(hauteur, UnitTypeId.Millimeters);
                            dimensionKey = $"{largeur:F0} x {hauteur:F0} mm";
                        }
                        else continue;

                        double longueur = duct.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble();
                        longueur = UnitUtils.ConvertFromInternalUnits(longueur, UnitTypeId.Meters);
                        if (ductLengths.ContainsKey(dimensionKey))
                            ductLengths[dimensionKey] += longueur;
                        else
                            ductLengths[dimensionKey] = longueur;
                        if (netAgg != null)
                        {
                            if (netAgg.DuctLengths.ContainsKey(dimensionKey))
                                netAgg.DuctLengths[dimensionKey] += longueur;
                            else
                                netAgg.DuctLengths[dimensionKey] = longueur;
                        }
                    }
                    // --- Traitement des accessoires ---
                    else if (elem is FamilyInstance fi)
                    {
                        if (fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)
                        {
                            List<double> diametres = new List<double>();
                            ConnectorSet connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                            if (connectors != null)
                            {
                                foreach (Connector connector in connectors)
                                {
                                    double d = connector.Radius * 2;
                                    d = UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.Millimeters);
                                    diametres.Add(d);
                                }
                            }

                            if (diametres.Count == 0) continue;
                            double maxDiametre = diametres.Max();
                            double longueur = EstimateFittingLength(fi);
                            if (pipeFittingLengths.ContainsKey(maxDiametre))
                                pipeFittingLengths[maxDiametre] += longueur;
                            else
                                pipeFittingLengths[maxDiametre] = longueur;
                            if (netAgg != null)
                            {
                                if (netAgg.PipeFittingLengths.ContainsKey(maxDiametre))
                                    netAgg.PipeFittingLengths[maxDiametre] += longueur;
                                else
                                    netAgg.PipeFittingLengths[maxDiametre] = longueur;
                            }
                            string familyName = fi.Symbol.Family.Name.ToLower();
                            string typeName = fi.Name.ToLower();
                            bool isElbow = familyName.Contains("coude") || familyName.Contains("elbow") ||
                                            typeName.Contains("coude") || typeName.Contains("elbow");
                            bool isTee = familyName.Contains("té") || familyName.Contains("tee") ||
                                         typeName.Contains("té") || typeName.Contains("tee");
                            if (isElbow)
                            {
                                if (elbowCounts.ContainsKey(maxDiametre))
                                    elbowCounts[maxDiametre]++;
                                else
                                    elbowCounts[maxDiametre] = 1;
                                if (netAgg != null)
                                {
                                    if (netAgg.ElbowCounts.ContainsKey(maxDiametre))
                                        netAgg.ElbowCounts[maxDiametre]++;
                                    else
                                        netAgg.ElbowCounts[maxDiametre] = 1;
                                }
                            }
                            else if (isTee)
                            {
                                if (teeCounts.ContainsKey(maxDiametre))
                                    teeCounts[maxDiametre]++;
                                else
                                    teeCounts[maxDiametre] = 1;
                                if (netAgg != null)
                                {
                                    if (netAgg.TeeCounts.ContainsKey(maxDiametre))
                                        netAgg.TeeCounts[maxDiametre]++;
                                    else
                                        netAgg.TeeCounts[maxDiametre] = 1;
                                }
                            }
                        }
                        else if (fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_PipeAccessory)
                                {
                                    // 👉 uniquement comptage
                            string accessoryType = fi.Symbol.Family.Name;
                                    if (pipeAccessoryCounts.ContainsKey(accessoryType))
                                pipeAccessoryCounts[accessoryType]++;
                                    else
                                pipeAccessoryCounts[accessoryType] = 1;
                                }
                           else if (includeDucts && fi.Category.Id.IntegerValue == (int)BuiltInCategory.OST_DuctFitting)

                        {
                            List<string> dimensions = new List<string>();
                            ConnectorSet connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                            if (connectors != null)
                            {
                                foreach (Connector connector in connectors)
                                {
                                    string dimensionKey = "";
                                    if (connector.Shape == ConnectorProfileType.Round)
                                    {
                                        double d = connector.Radius * 2;
                                        d = UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.Millimeters);
                                        dimensionKey = $"Ø{d:F0} mm";
                                    }
                                    else if (connector.Shape == ConnectorProfileType.Rectangular)
                                    {
                                        double largeur = connector.Width;
                                        double hauteur = connector.Height;
                                        largeur = UnitUtils.ConvertFromInternalUnits(largeur, UnitTypeId.Millimeters);
                                        hauteur = UnitUtils.ConvertFromInternalUnits(hauteur, UnitTypeId.Millimeters);
                                        dimensionKey = $"{largeur:F0} x {hauteur:F0} mm";
                                    }
                                    else continue;
                                    dimensions.Add(dimensionKey);
                                }
                            }
                            if (dimensions.Count == 0) continue;
                            string keyDimension = dimensions.First();
                            double longueur = EstimateFittingLength(fi);
                            if (ductFittingLengths.ContainsKey(keyDimension))
                                ductFittingLengths[keyDimension] += longueur;
                            else
                                ductFittingLengths[keyDimension] = longueur;
                            if (netAgg != null)
                            {
                                if (netAgg.DuctFittingLengths.ContainsKey(keyDimension))
                                    netAgg.DuctFittingLengths[keyDimension] += longueur;
                                else
                                    netAgg.DuctFittingLengths[keyDimension] = longueur;
                            }
       
                        }
                        else continue;
                    }
                    else continue;
                }

                // Arrondir les valeurs globales
                foreach (var key in pipeLengths.Keys.ToList())
                    pipeLengths[key] = Math.Round(pipeLengths[key], 2);
                foreach (var key in pipeFittingLengths.Keys.ToList())
                    pipeFittingLengths[key] = Math.Round(pipeFittingLengths[key], 2);
                foreach (var key in ductLengths.Keys.ToList())
                    ductLengths[key] = Math.Round(ductLengths[key], 2);
                foreach (var key in ductFittingLengths.Keys.ToList())
                    ductFittingLengths[key] = Math.Round(ductFittingLengths[key], 2);
                foreach (var key in pipeVolumes.Keys.ToList())
                    pipeVolumes[key] = Math.Round(pipeVolumes[key], 3);
                foreach (var netAgg in networkAggregates.Values)
                {
                    foreach (var key in netAgg.PipeLengths.Keys.ToList())
                        netAgg.PipeLengths[key] = Math.Round(netAgg.PipeLengths[key], 2);
                    foreach (var key in netAgg.PipeFittingLengths.Keys.ToList())
                        netAgg.PipeFittingLengths[key] = Math.Round(netAgg.PipeFittingLengths[key], 2);
                    foreach (var key in netAgg.DuctLengths.Keys.ToList())
                        netAgg.DuctLengths[key] = Math.Round(netAgg.DuctLengths[key], 2);
                    foreach (var key in netAgg.DuctFittingLengths.Keys.ToList())
                        netAgg.DuctFittingLengths[key] = Math.Round(netAgg.DuctFittingLengths[key], 2);
                    foreach (var key in netAgg.PipeVolumes.Keys.ToList())
                        netAgg.PipeVolumes[key] = Math.Round(netAgg.PipeVolumes[key], 3);
                }

                // Affichage global des résultats (pour information)
                StringBuilder sb = new StringBuilder();
                double totalPipeLength = 0;
                if (pipeLengths.Count > 0)
                {
                    sb.AppendLine("Longueur totale des canalisations par diamètre (DN) :");
                    foreach (var item in pipeLengths.OrderBy(kvp => kvp.Key))
                    {
                        sb.AppendLine($"{item.Key:N0} mm : {item.Value:F2} m");
                        totalPipeLength += item.Value;
                    }
                    sb.AppendLine($"Total : {totalPipeLength:F2} m");
                    sb.AppendLine();
                }
                if (pipeVolumes.Count > 0)
                {
                    double totalWaterVolume = 0;
                    sb.AppendLine("Volume total d'eau par diamètre intérieur :");
                    foreach (var item in pipeVolumes.OrderBy(kvp => kvp.Key))
                    {
                        sb.AppendLine($"{item.Key:N0} mm : {item.Value:F3} m³");
                        totalWaterVolume += item.Value;
                    }
                    sb.AppendLine($"Total : {totalWaterVolume:F3} m³");
                    sb.AppendLine();
                }
                if (elbowCounts.Count > 0)
                {
                    sb.AppendLine("Nombre de coudes par diamètre :");
                    foreach (var item in elbowCounts.OrderBy(kvp => kvp.Key))
                        sb.AppendLine($"{item.Key:N0} mm : {item.Value}");
                    sb.AppendLine();
                }
                if (teeCounts.Count > 0)
                {
                    sb.AppendLine("Nombre de tés par diamètre :");
                    foreach (var item in teeCounts.OrderBy(kvp => kvp.Key))
                        sb.AppendLine($"{item.Key:N0} mm : {item.Value}");
                    sb.AppendLine();
                }
                if (includeDucts && ductLengths.Count > 0)
                {
                    double totalDuctLength = 0;
                    sb.AppendLine("Longueur totale des gaines par dimension :");
                    foreach (var item in ductLengths.OrderBy(kvp => kvp.Key))
                    {
                        sb.AppendLine($"{item.Key} : {item.Value:F2} m");
                        totalDuctLength += item.Value;
                    }
                    sb.AppendLine($"Total : {totalDuctLength:F2} m");
                    sb.AppendLine();
                }
                if (includeDucts && ductFittingLengths.Count > 0)
                {
                    double totalDuctFittingLength = 0;
                    sb.AppendLine("Accessoires de gaines (approximatif) :");
                    foreach (var item in ductFittingLengths.OrderBy(kvp => kvp.Key))
                    {
                        sb.AppendLine($"{item.Key} : {item.Value:F2} m");
                        totalDuctFittingLength += item.Value;
                    }
                    sb.AppendLine($"Total : {totalDuctFittingLength:F2} m");
                    sb.AppendLine();
                }
                // Remarque : Le tableau "Accessoires de canalisations (approximatif)" sera traité plus bas.
                TaskDialog.Show("Résultats", sb.ToString());

                // Déterminer le système unique pour le nom du fichier (si applicable)
                string singleSystemType = "";
                if (filterBySystemType && selectedSystemTypes.Count == 1)
                    singleSystemType = selectedSystemTypes[0];

                // Export vers Excel (la méthode retourne le chemin complet du fichier)
                string excelFilePath = ExportToExcel(
    doc.Title,
    pipeLengths,
    pipeFittingLengths,
    ductLengths,
    ductFittingLengths,
    includeDucts,
    elbowCounts,
    teeCounts,
    dnToDiameters,
    pipeVolumes,
    singleSystemType,
    networkAggregates,
    pipeAccessoryCounts,
    networkColors);


                // À la fin, proposer d'ouvrir le fichier
                if (MessageBox.Show("Les résultats ont été exportés vers Excel avec succès.\nVoulez-vous ouvrir le fichier ?",
                                    "Succès", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(excelFilePath) { UseShellExecute = true });
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

      

        // Méthode qui récupère les types de systèmes disponibles
        private List<string> GetSystemTypes(Autodesk.Revit.DB.Document doc)
        {
            HashSet<string> systemTypes = new HashSet<string>();

            FilteredElementCollector pipeSystemTypeCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType));
            foreach (PipingSystemType systemType in pipeSystemTypeCollector)
            {
                string typeName = systemType.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
                if (!string.IsNullOrEmpty(typeName))
                    systemTypes.Add($"Canalisation : {typeName}");
            }

            FilteredElementCollector ductSystemTypeCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType));
            foreach (MechanicalSystemType systemType in ductSystemTypeCollector)
            {
                string typeName = systemType.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
                if (!string.IsNullOrEmpty(typeName))
                    systemTypes.Add($"Gaine : {typeName}");
            }
            return systemTypes.ToList();
        }

        // Récupération du nom du système d'un élément
        private string GetElementSystemTypeName(Element elem)
        {
            if (elem is Pipe pipe)
            {
                ElementId systemTypeId = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                if (systemTypeId != null && systemTypeId != ElementId.InvalidElementId)
                {
                    PipingSystemType systemType = pipe.Document.GetElement(systemTypeId) as PipingSystemType;
                    string typeName = systemType?.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
                    if (!string.IsNullOrEmpty(typeName))
                        return $"Canalisation : {typeName}";
                }
            }
            else if (elem is Duct duct)
            {
                ElementId systemTypeId = duct.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId();
                if (systemTypeId != null && systemTypeId != ElementId.InvalidElementId)
                {
                    MechanicalSystemType systemType = duct.Document.GetElement(systemTypeId) as MechanicalSystemType;
                    string typeName = systemType?.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
                    if (!string.IsNullOrEmpty(typeName))
                        return $"Gaine : {typeName}";
                }
            }
            else if (elem is FamilyInstance fi)
            {
                ConnectorSet connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                if (connectors != null)
                {
                    foreach (Connector connector in connectors)
                    {
                        if (connector.MEPSystem != null)
                        {
                            ElementId systemTypeId = connector.MEPSystem.GetTypeId();
                            if (systemTypeId != null && systemTypeId != ElementId.InvalidElementId)
                            {
                                Element systemTypeElement = fi.Document.GetElement(systemTypeId);
                                string typeName = systemTypeElement?.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString();
                                string prefix = "";
                                if (systemTypeElement is PipingSystemType)
                                    prefix = "Canalisation : ";
                                else if (systemTypeElement is MechanicalSystemType)
                                    prefix = "Gaine : ";
                                if (!string.IsNullOrEmpty(typeName))
                                    return $"{prefix}{typeName}";
                            }
                        }
                    }
                }
            }
            return null;
        }

        // --- NOUVELLE IMPLEMENTATION ---
        // Estimation de la longueur d'un accessoire par interpolation linéaire
        private double EstimateFittingLength(FamilyInstance fitting)
        {
            double maxDiameter = 0;
            var connectors = fitting.MEPModel?.ConnectorManager?.Connectors;
            if (connectors != null)
            {
                List<double> diametres = new List<double>();
                foreach (Connector connector in connectors)
                {
                    double diam = connector.Radius * 2;
                    diam = UnitUtils.ConvertFromInternalUnits(diam, UnitTypeId.Millimeters);
                    diametres.Add(diam);
                }
                if (diametres.Count > 0)
                    maxDiameter = diametres.Max();
            }
            if (maxDiameter <= 0)
                return 0;
            return InterpolateFittingLength(maxDiameter);
        }

        private double InterpolateFittingLength(double diameterMm)
        {
            var knownPoints = new List<(double Dn, double Length)>
            {
                (80, 0.18),
                (100, 0.24),
                (125, 0.30),
                (150, 0.36),
                (200, 0.48),
                (250, 0.60),
                (300, 0.72),
                (350, 0.84),
                (400, 0.96),
                (450, 1.05),
                (500, 1.20)
            };

            if (diameterMm <= knownPoints[0].Dn)
                return knownPoints[0].Length;
            if (diameterMm >= knownPoints[knownPoints.Count - 1].Dn)
                return knownPoints[knownPoints.Count - 1].Length;
            for (int i = 0; i < knownPoints.Count - 1; i++)
            {
                double d1 = knownPoints[i].Dn;
                double l1 = knownPoints[i].Length;
                double d2 = knownPoints[i + 1].Dn;
                double l2 = knownPoints[i + 1].Length;
                if (diameterMm >= d1 && diameterMm <= d2)
                {
                    double ratio = (diameterMm - d1) / (d2 - d1);
                    return l1 + ratio * (l2 - l1);
                }
            }
            return knownPoints[0].Length;
        }
        // --- FIN NOUVELLE IMPLEMENTATION ---

        // Méthode d'export vers Excel (retourne le chemin complet du fichier généré)
        private string ExportToExcel(
            string projectName,
            Dictionary<double, double> pipeData,
            Dictionary<double, double> pipeFittingData,
            Dictionary<string, double> ductData,
            Dictionary<string, double> ductFittingData,
            bool includeDucts,
            Dictionary<double, int> elbowCounts,
            Dictionary<double, int> teeCounts,
            Dictionary<double, (double DiametreInterieur, double DiametreExterieur)> dnToDiameters,
            Dictionary<double, double> pipeVolumes,
            string singleSystemType,
            Dictionary<string, NetworkAggregation> networkAggregates,
            Dictionary<string, int> pipeAccessoryCounts,
            Dictionary<string, System.Drawing.Color> networkColors)
        {
            string folderName = includeDucts ? "LongueurCanalisations-Gaine" : "LongueurCanalisations";
            string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitLogs", folderName);
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            // Préparer le nom du fichier
            string fileName;
            if (!string.IsNullOrEmpty(singleSystemType))
            {
                string safeSystemType = string.Join("_", singleSystemType.Split(Path.GetInvalidFileNameChars()));
                fileName = $"{projectName}_{safeSystemType}_LongueurElements_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            }
            else
                fileName = $"{projectName}_LongueurElements_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            string filePath = Path.Combine(folderPath, fileName);

            // Couleurs de mise en forme pastel
            var headerFillColor = System.Drawing.Color.LightBlue;
            var totalFillColor = System.Drawing.Color.LightGreen;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using (ExcelPackage excel = new ExcelPackage())
            {
                // Feuille générale
                var wsGen = excel.Workbook.Worksheets.Add("Général");
                int row = 1;
                wsGen.Cells[row, 1].Value = "Nom de la maquette";
                wsGen.Cells[row, 2].Value = projectName;
                wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                row += 2;
                if (!string.IsNullOrEmpty(singleSystemType))
                {
                    wsGen.Cells[row, 1].Value = "Système sélectionné";
                    wsGen.Cells[row, 2].Value = singleSystemType;
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    row += 2;
                }
                // 1. Canalisations par DN
                if (pipeData.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Longueur totale des canalisations par diamètre (DN)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "DN (mm)";
                    wsGen.Cells[row, 2].Value = "Longueur (m)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    double totalPipeLength = 0;
                    foreach (var item in pipeData.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = item.Value;
                        totalPipeLength += item.Value;
                        row++;
                    }
                    wsGen.Cells[row, 1].Value = "Total";
                    wsGen.Cells[row, 2].Value = totalPipeLength;
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                    row += 2;
                }
                // 2. Légende des diamètres
                if (dnToDiameters.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Légende des diamètres (DN, Diamètre Intérieur, Diamètre Extérieur)";
                    wsGen.Cells[row, 1, row, 3].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 3].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 3].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "DN (mm)";
                    wsGen.Cells[row, 2].Value = "Diamètre Int. (mm)";
                    wsGen.Cells[row, 3].Value = "Diamètre Ext. (mm)";
                    wsGen.Cells[row, 1, row, 3].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 3].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 3].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    foreach (var item in dnToDiameters.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = $"{item.Value.DiametreInterieur:N1}";
                        wsGen.Cells[row, 3].Value = $"{item.Value.DiametreExterieur:N1}";
                        row++;
                    }
                    row += 2;
                }
                // 3. Volume total d'eau par diamètre intérieur
                if (pipeVolumes.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Volume total d'eau par diamètre intérieur";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "Diamètre Int. (mm)";
                    wsGen.Cells[row, 2].Value = "Volume (m³)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    double totalWaterVolume = 0;
                    foreach (var item in pipeVolumes.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = item.Value;
                        totalWaterVolume += item.Value;
                        row++;
                    }
                    wsGen.Cells[row, 1].Value = "Total";
                    wsGen.Cells[row, 2].Value = totalWaterVolume;
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                    row += 2;
                }
                // 4. Nombre de coudes par diamètre
                if (elbowCounts.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Nombre de coudes par diamètre";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "Diamètre (mm)";
                    wsGen.Cells[row, 2].Value = "Nombre";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    foreach (var item in elbowCounts.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = item.Value;
                        row++;
                    }
                    row += 2;
                }
                // 5. Nombre de tés par diamètre
                if (teeCounts.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Nombre de tés par diamètre";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "Diamètre (mm)";
                    wsGen.Cells[row, 2].Value = "Nombre";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    foreach (var item in teeCounts.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = item.Value;
                        row++;
                    }
                    row += 2;
                }
                // 6. Gaines (et accessoires de gaines) – si activé
                if (includeDucts && ductData.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Longueur totale des gaines par dimension";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "Dimension";
                    wsGen.Cells[row, 2].Value = "Longueur (m)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    double totalDuctLength = 0;
                    foreach (var item in ductData.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = item.Key;
                        wsGen.Cells[row, 2].Value = item.Value;
                        totalDuctLength += item.Value;
                        row++;
                    }
                    wsGen.Cells[row, 1].Value = "Total";
                    wsGen.Cells[row, 2].Value = totalDuctLength;
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                    row += 2;
                    if (ductFittingData.Count > 0)
                    {
                        wsGen.Cells[row, 1].Value = "Accessoires de gaines (approximatif)";
                        wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        row++;
                        wsGen.Cells[row, 1].Value = "Dimension";
                        wsGen.Cells[row, 2].Value = "Longueur (m)";
                        wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        row++;
                        double totalDuctFittingLength = 0;
                        foreach (var item in ductFittingData.OrderBy(kvp => kvp.Key))
                        {
                            wsGen.Cells[row, 1].Value = item.Key;
                            wsGen.Cells[row, 2].Value = item.Value;
                            totalDuctFittingLength += item.Value;
                            row++;
                        }
                        wsGen.Cells[row, 1].Value = "Total";
                        wsGen.Cells[row, 2].Value = totalDuctFittingLength;
                        wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                        row += 2;
                    }
                }
                // 7. Enfin, Accessoires de canalisations (approximatif) en dernier
                if (pipeFittingData.Count > 0)
                {
                    wsGen.Cells[row, 1].Value = "Accessoires de canalisations (approximatif)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    wsGen.Cells[row, 1].Value = "Diamètre (mm)";
                    wsGen.Cells[row, 2].Value = "Longueur (m)";
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    row++;
                    double totalPipeFittingLength = 0;
                    foreach (var item in pipeFittingData.OrderBy(kvp => kvp.Key))
                    {
                        wsGen.Cells[row, 1].Value = $"{item.Key:N0}";
                        wsGen.Cells[row, 2].Value = item.Value;
                        totalPipeFittingLength += item.Value;
                        row++;
                    }
                    wsGen.Cells[row, 1].Value = "Total";
                    wsGen.Cells[row, 2].Value = totalPipeFittingLength;
                    wsGen.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsGen.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                    row += 2;
                }
                wsGen.Cells[1, 1, row - 1, 3].AutoFitColumns();

                // Création de feuilles par réseau
                // 1) Calculer la longueur totale de chaque réseau et trier par décroissant
                var orderedNetworks = networkAggregates
                    .OrderByDescending(n => n.Value.PipeLengths.Values.Sum())
                    .ToList();

                // 2) Créer les feuilles dans l'ordre trié
                int networkSheetCounter = 1;
                foreach (var kvp in orderedNetworks)
                {
                    string sheetName = $"Réseau {networkSheetCounter}";
                    var wsNet = excel.Workbook.Worksheets.Add(sheetName);

                    // applique la couleur Revit à l’onglet
                    if (networkColors.TryGetValue(kvp.Key, out var tabClr))
                    {
                        wsNet.TabColor = tabClr;    // EPPlus prendra cette couleur sur l’onglet
                    }
                    else
                    {
                        wsNet.TabColor = headerFillColor;  // fallback si jamais
                    }
                    int r = 1;
                    wsNet.Cells[r, 1].Value = "Réseau :";
                    wsNet.Cells[r, 2].Value = kvp.Key;

                    // --- anciennement : out var clr provoquait un conflit de nom ---
                    System.Drawing.Color netColor;
                    if (networkColors.TryGetValue(kvp.Key, out netColor))
                    {
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(netColor);
                    }
                    else
                    {
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    }
                    wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                    r += 2;

                    // Canalisations par DN
                    if (kvp.Value.PipeLengths.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Canalisations par DN";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "DN (mm)";
                        wsNet.Cells[r, 2].Value = "Longueur (m)";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        double totPipe = 0;
                        foreach (var item in kvp.Value.PipeLengths.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = $"{item.Key:N0}";
                            wsNet.Cells[r, 2].Value = item.Value;
                            totPipe += item.Value;
                            r++;
                        }
                        wsNet.Cells[r, 1].Value = "Total";
                        wsNet.Cells[r, 2].Value = totPipe;
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                        r += 2;
                    }
                    // Volume d'eau par diamètre intérieur
                    if (kvp.Value.PipeVolumes.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Volume d'eau par diamètre intérieur";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "Diamètre Int. (mm)";
                        wsNet.Cells[r, 2].Value = "Volume (m³)";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        double totVolume = 0;
                        foreach (var item in kvp.Value.PipeVolumes.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = $"{item.Key:N0}";
                            wsNet.Cells[r, 2].Value = item.Value;
                            totVolume += item.Value;
                            r++;
                        }
                        wsNet.Cells[r, 1].Value = "Total";
                        wsNet.Cells[r, 2].Value = totVolume;
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                        r += 2;
                    }
                    // Nombre de coudes
                    if (kvp.Value.ElbowCounts.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Nombre de coudes";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "Diamètre (mm)";
                        wsNet.Cells[r, 2].Value = "Nombre";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        foreach (var item in kvp.Value.ElbowCounts.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = $"{item.Key:N0}";
                            wsNet.Cells[r, 2].Value = item.Value;
                            r++;
                        }
                        r += 2;
                    }
                    // Nombre de tés
                    if (kvp.Value.TeeCounts.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Nombre de tés";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "Diamètre (mm)";
                        wsNet.Cells[r, 2].Value = "Nombre";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        foreach (var item in kvp.Value.TeeCounts.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = $"{item.Key:N0}";
                            wsNet.Cells[r, 2].Value = item.Value;
                            r++;
                        }
                        r += 2;
                    }
                    // Gaines par dimension (si activé)
                    if (includeDucts && kvp.Value.DuctLengths.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Gaines par dimension";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "Dimension";
                        wsNet.Cells[r, 2].Value = "Longueur (m)";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        double totDuct = 0;
                        foreach (var item in kvp.Value.DuctLengths.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = item.Key;
                            wsNet.Cells[r, 2].Value = item.Value;
                            totDuct += item.Value;
                            r++;
                        }
                        wsNet.Cells[r, 1].Value = "Total";
                        wsNet.Cells[r, 2].Value = totDuct;
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                        r += 2;
                    }
                    // Accessoires de canalisations (placé en dernier)
                    if (kvp.Value.PipeFittingLengths.Count > 0)
                    {
                        wsNet.Cells[r, 1].Value = "Accessoires de canalisations (approximatif)";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        wsNet.Cells[r, 1].Value = "Diamètre (mm)";
                        wsNet.Cells[r, 2].Value = "Longueur (m)";
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                        r++;
                        double totFitting = 0;
                        foreach (var item in kvp.Value.PipeFittingLengths.OrderBy(x => x.Key))
                        {
                            wsNet.Cells[r, 1].Value = $"{item.Key:N0}";
                            wsNet.Cells[r, 2].Value = item.Value;
                            totFitting += item.Value;
                            r++;
                        }
                        wsNet.Cells[r, 1].Value = "Total";
                        wsNet.Cells[r, 2].Value = totFitting;
                        wsNet.Cells[r, 1, r, 2].Style.Font.Bold = true;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        wsNet.Cells[r, 1, r, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);
                        r += 2;
                    }
                    wsNet.Cells[1, 1, r - 1, 3].AutoFitColumns();
                    networkSheetCounter++;
                }

                // Création d'une feuille dédiée aux graphiques
                var wsChart = excel.Workbook.Worksheets.Add("Graphique");
                int chartRow = 1;
                // Premier graphique : répartition de la longueur des canalisations par DN (colonne)
                wsChart.Cells[chartRow, 1].Value = "DN (mm)";
                wsChart.Cells[chartRow, 2].Value = "Longueur (m)";
                wsChart.Cells[chartRow, 1, chartRow, 2].Style.Font.Bold = true;
                wsChart.Cells[chartRow, 1, chartRow, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                wsChart.Cells[chartRow, 1, chartRow, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                chartRow++;
                foreach (var item in pipeData.OrderBy(kvp => kvp.Key))
                {
                    wsChart.Cells[chartRow, 1].Value = item.Key;
                    wsChart.Cells[chartRow, 2].Value = item.Value;
                    chartRow++;
                }
                var chart1 = wsChart.Drawings.AddChart("chartPipe", eChartType.ColumnClustered);
                chart1.Title.Text = "Longueur des canalisations par DN";
                chart1.SetPosition(0, 0, 3, 0);
                chart1.SetSize(600, 400);
                chart1.Series.Add(wsChart.Cells[$"B2:B{chartRow - 1}"], wsChart.Cells[$"A2:A{chartRow - 1}"]);

                // === Feuille des accessoires de canalisation (uniquement comptage) ===
                var wsAcc = excel.Workbook.Worksheets.Add("Accessoires Canalisation");
                int rowAcc = 1;

                // 1) En-tête avec fond pastel
                wsAcc.Cells[rowAcc, 1].Value = "Type d'accessoire";
                wsAcc.Cells[rowAcc, 2].Value = "Quantité";
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Font.Bold = true;
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                rowAcc++;

                // 2) Lignes de contenu (inchangées)
                int totalQty = 0;
                foreach (var kvp in pipeAccessoryCounts.OrderBy(x => x.Key))
                {
                    wsAcc.Cells[rowAcc, 1].Value = kvp.Key;
                    wsAcc.Cells[rowAcc, 2].Value = kvp.Value;
                    totalQty += kvp.Value;
                    rowAcc++;
                }

                // 3) Ligne "Total" avec fond pastel
                wsAcc.Cells[rowAcc, 1].Value = "Total";
                wsAcc.Cells[rowAcc, 2].Value = totalQty;
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Font.Bold = true;
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                wsAcc.Cells[rowAcc, 1, rowAcc, 2].Style.Fill.BackgroundColor.SetColor(totalFillColor);

                // 4) Ajustement de la largeur des colonnes
                wsAcc.Cells[1, 1, rowAcc, 2].AutoFitColumns();


                // Second graphique (si des coudes existent) : répartition des coudes par DN (pie chart)
                if (elbowCounts.Count > 0)
                {
                    int startRow = chartRow + 2;
                    wsChart.Cells[startRow, 1].Value = "DN (mm)";
                    wsChart.Cells[startRow, 2].Value = "Nombre de coudes";
                    wsChart.Cells[startRow, 1, startRow, 2].Style.Font.Bold = true;
                    wsChart.Cells[startRow, 1, startRow, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    wsChart.Cells[startRow, 1, startRow, 2].Style.Fill.BackgroundColor.SetColor(headerFillColor);
                    startRow++;
                    foreach (var item in elbowCounts.OrderBy(kvp => kvp.Key))
                    {
                        wsChart.Cells[startRow, 1].Value = item.Key;
                        wsChart.Cells[startRow, 2].Value = item.Value;
                        startRow++;
                    }
                    var chart2 = wsChart.Drawings.AddChart("chartElbow", eChartType.Pie);
                    chart2.Title.Text = "Répartition des coudes par DN";
                    chart2.SetPosition(chartRow + 2, 0, 6, 0);
                    chart2.SetSize(600, 400);
                    chart2.Series.Add(wsChart.Cells[$"B{chartRow + 3}:B{startRow - 1}"], wsChart.Cells[$"A{chartRow + 3}:A{startRow - 1}"]);
                }
                wsChart.Cells[wsChart.Dimension.Address].AutoFitColumns();

                // Sauvegarde du fichier
                FileInfo fi = new FileInfo(filePath);
                excel.SaveAs(fi);
            }
            return filePath;
        }
    }
}
