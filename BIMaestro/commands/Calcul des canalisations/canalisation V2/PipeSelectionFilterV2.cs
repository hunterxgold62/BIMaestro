using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.IO;
using System.Diagnostics;
using Licensing;

// Références pour WPF
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using System.Windows.Media;



namespace Analyse
{
    [Transaction(TransactionMode.ReadOnly)]
    public partial class PipeLengthByDiameterCommandV2 : BaseTrackedCommand
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

        private sealed class NetworkSelectionHandler : IExternalEventHandler
        {
            private readonly Document _document;
            private readonly object _requestLock = new object();
            private List<ElementId> _pendingIds = new List<ElementId>();

            public NetworkSelectionHandler(Document document)
            {
                _document = document;
            }

            public void SetRequest(IEnumerable<ElementId> ids)
            {
                lock (_requestLock)
                {
                    _pendingIds = ids?.Where(id => id != null).Distinct().ToList()
                                  ?? new List<ElementId>();
                }
            }

            public void Execute(UIApplication app)
            {
                List<ElementId> ids;
                lock (_requestLock)
                {
                    ids = _pendingIds;
                    _pendingIds = new List<ElementId>();
                }

                if (ids.Count == 0)
                    return;

                try
                {
                    UIDocument uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null || _document == null || !_document.IsValidObject ||
                        !ReferenceEquals(uiDoc.Document, _document))
                    {
                        TaskDialog.Show(
                            "BIMaestro",
                            "Le document du calcul n'est plus le document actif.");
                        return;
                    }

                    var validIds = ids.Where(id => _document.GetElement(id) != null).ToList();
                    if (validIds.Count == 0)
                        return;

                    uiDoc.Selection.SetElementIds(validIds);
                    uiDoc.ShowElements(validIds);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(
                        "BIMaestro",
                        "Impossible d'afficher ce réseau sans risque pour Revit.\n\n" + ex.Message);
                }
            }

            public string GetName()
            {
                return "BIMaestro - Afficher un réseau";
            }
        }

        protected override string ButtonId => "PipeLengthByDiameterCommandV2";


        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
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
                var networkElementIds = new Dictionary<string, HashSet<ElementId>>();


                // Traitement des éléments
                foreach (ElementId id in selectedIds)
                {
                    Element elem = doc.GetElement(id);

                    if (!includeDucts)
                    {
                        if (elem is Duct)
                            continue;
                        if (elem is FamilyInstance ductFitting
                            && ductFitting.Category?.Id.GetIdValue() == (int)BuiltInCategory.OST_DuctFitting)
                            continue;
                    }

                    // Filtrer par système sauf pour les accessoires de canalisation
                    bool isPipeAccessory = elem is FamilyInstance fii
                           && fii.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeAccessory;
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
                        if (!networkElementIds.TryGetValue(networkName, out var ids))
                        {
                            ids = new HashSet<ElementId>();
                            networkElementIds[networkName] = ids;
                        }
                        ids.Add(elem.Id);
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
                        if (fi.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeFitting)
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
                        else if (fi.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_PipeAccessory)
                                {
                                    // 👉 uniquement comptage
                            string accessoryType = fi.Symbol.Family.Name;
                                    if (pipeAccessoryCounts.ContainsKey(accessoryType))
                                pipeAccessoryCounts[accessoryType]++;
                                    else
                                pipeAccessoryCounts[accessoryType] = 1;
                                }
                           else if (includeDucts && fi.Category.Id.GetIdValue() == (int)BuiltInCategory.OST_DuctFitting)

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

                // Export vers Excel seulement si l'option est cochée
                if (exportToExcel)
                {
                    // La méthode retourne le chemin complet du fichier généré
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
                }
                ShowNetworkInteractionWindow(
                   uidoc,
                   mainWindowHandle,
                   networkAggregates,
                   networkColors,
                   networkElementIds);
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
        private void ShowNetworkInteractionWindow(
           UIDocument uidoc,
           IntPtr mainWindowHandle,
           Dictionary<string, NetworkAggregation> networkAggregates,
           Dictionary<string, System.Drawing.Color> networkColors,
           Dictionary<string, HashSet<ElementId>> networkElementIds)
        {
            var items = new List<PipeNetworkDisplayItem>();

            foreach (var kvp in networkAggregates)
            {
                if (!networkElementIds.TryGetValue(kvp.Key, out var ids) || ids.Count == 0)
                    continue;

                double totalLength = kvp.Value.PipeLengths.Values.Sum() + kvp.Value.DuctLengths.Values.Sum();

                var color = networkColors.TryGetValue(kvp.Key, out var clr)
                    ? clr
                    : System.Drawing.Color.LightGray;

                items.Add(new PipeNetworkDisplayItem(
                    kvp.Key,
                    Math.Round(totalLength, 2),
                    ToBrush(color),
                    new HashSet<ElementId>(ids)));
            }

            if (items.Count == 0)
                return;

            var selectionHandler = new NetworkSelectionHandler(uidoc.Document);
            var selectionEvent = ExternalEvent.Create(selectionHandler);
            var interactionWindow = new PipeNetworkInteractionWindow(items, ids =>
            {
                selectionHandler.SetRequest(ids);
                ExternalEventRequest request = selectionEvent.Raise();
                if (request == ExternalEventRequest.Denied)
                {
                    MessageBox.Show(
                        "Revit ne peut pas traiter cette demande pour le moment.",
                        "BIMaestro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            });
            interactionWindow.Closed += (sender, args) =>
            {
                try { selectionEvent.Dispose(); } catch { }
            };
            var helper = new WindowInteropHelper(interactionWindow)
            {
                Owner = mainWindowHandle
            };
            interactionWindow.Show();
        }

        private SolidColorBrush ToBrush(System.Drawing.Color color)
        {
            var mediaColor = System.Windows.Media.Color.FromRgb(color.R, color.G, color.B);
            var brush = new SolidColorBrush(mediaColor);
            brush.Freeze();
            return brush;
        }

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
            return ExportToExcelNpoi(
                projectName,
                pipeData,
                pipeFittingData,
                ductData,
                ductFittingData,
                includeDucts,
                elbowCounts,
                teeCounts,
                dnToDiameters,
                pipeVolumes,
                singleSystemType,
                networkAggregates,
                pipeAccessoryCounts,
                networkColors);
        }
    }
}
