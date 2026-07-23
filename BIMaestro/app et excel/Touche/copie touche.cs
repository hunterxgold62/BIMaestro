using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

public class AppUI : IExternalApplication
{
    private static readonly List<RibbonPanel> ribbonPanels = new List<RibbonPanel>();
    public static UIApplication UiApplication { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        CreateRibbonUI(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }

    public static void CreateRibbonUI(UIControlledApplication application)
    {
        const string tabName = "BIMaestro";
        try { application.CreateRibbonTab(tabName); } catch { /* déjà créé */ }

        // Panneaux
        RibbonPanel panelVisualization = application.CreateRibbonPanel(tabName, "Outils de Visualisation");
        ribbonPanels.Add(panelVisualization);

        RibbonPanel panelEditing = application.CreateRibbonPanel(tabName, "Modification");
        ribbonPanels.Add(panelEditing);

        RibbonPanel panelIA = application.CreateRibbonPanel(tabName, "Outils IA");
        ribbonPanels.Add(panelIA);

        RibbonPanel panelAnalysis = application.CreateRibbonPanel(tabName, "Analyse");
        ribbonPanels.Add(panelAnalysis);

        RibbonPanel panelFamille = application.CreateRibbonPanel(tabName, "Spécifique aux familles");
        ribbonPanels.Add(panelFamille);

        RibbonPanel panelCouleur = application.CreateRibbonPanel(tabName, "Couleur et information");
        ribbonPanels.Add(panelCouleur);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // ============================
        // VISUALISATION
        // ============================
        AddPushButton(
            panelVisualization, "HighlightElementsByCategories", "Sélection\nd'éléments",
            assemblyPath, typeof(Visualisation.HighlightElementsByCategoriesCommand),
            "Safeimagekit-resized-img (1).png",
            "Cette commande permet de :\r\n- Mettre en évidence et filtrer les éléments d'une ou plusieurs catégories.\r\n- Regrouper automatiquement les éléments similaires.\r\n- Simplifier la gestion et la sélection précise dans un projet Revit.\r\n\r\nUtilité : Facilite les actions répétitives et assure un traitement efficace."
        );

        AddPushButton(
            panelVisualization, "GetPaintedMaterialsButton", "Peinture de\nmatériaux",
            assemblyPath, typeof(Visualisation.GetPaintedMaterialsCommand),
            "Peinture et matériaux.png",
            "Permet d'obtenir une liste des matériaux appliqués à un élément Revit, y compris la peinture appliquée aux faces."
        );

        AddPushButton(
            panelVisualization, "OpenSheetFromViewButton", "Ouvrir la vue\ndu Plan",
            assemblyPath, typeof(Visualisation.OpenSheetFromView),
            "safeimagekit-doc.png",
            "Bascule entre une vue active et les feuilles qui la contiennent, ou ouvre la vue depuis un viewport."
        );

        AddPushButton(
            panelVisualization, "ReorientViewButton", "Réorienter\nVue 3D",
            assemblyPath, typeof(Visualisation.ReorientViewCommand),
            "Element 3D.png",
            "Réoriente une vue 3D active selon la géométrie d'une face sélectionnée."
        );

        AddPushButton(
            panelVisualization, "Export Nomenclature", "Export \nNomenclature",
            assemblyPath, typeof(Visualisation.ExportScheduleCommand),
            "rvt to excel et pdf.png",
            "Exporte les nomenclatures Revit sélectionnées en fichier Excel ou PDF."
        );

        AddPushButton(
            panelVisualization, "ExportDwgBatch", "Export \nDWG",
            assemblyPath, typeof(Visualisation.ExportSheetsCommand),
            "export DWG.png",
            "Exporte automatiquement plusieurs vues/feuilles en DWG avec nommage projet+vue."
        );

        AddPushButton(
            panelVisualization, "Sélection d'objet", "Sélection\nd'objet",
            assemblyPath, typeof(Visualisation.SelectSimilarCommand),
            "Sélection d'élément.png",
            "Sélectionne des éléments similaires dans le projet."
        );

        // ============================
        // MODIFICATION
        // ============================
        AddPushButton(
            panelEditing, "OverrideColor", "Changer couleur\nélément",
            assemblyPath, typeof(Modification.OverrideColorCommand),
            "Pallette de couleur anexe .png",
            "Cette commande permet :\r\n- De personnaliser les couleurs, motifs et la transparence.\r\n- D'appliquer à plusieurs vues.\r\n- De réinitialiser les modifications."
        );

        AddPushButton(
            panelEditing, "ElementRenamerButton", "Organisateur\nd'Éléments",
            assemblyPath, typeof(Modification.RenameElementsCommand),
            "Organisateur d'éléments.png",
            "Renomme et numérote des éléments. Sur une feuille, organise aussi les fenêtres de vue selon leur position."
        );

        AddPushButton(
            panelEditing, "ResérvationAuto", "Auto\nRéservation",
            assemblyPath, typeof(Modification.ReservationAutoMultiCommand),
            "safeimagekit-Réservation.png",
            "Crée des réservations automatiques."
        );

        AddSplitButton(
            panelEditing, "Bride auto", "Bride\nauto", assemblyPath,
            new List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)>
            {
                ("Bride auto", "Bride\nauto", typeof(Modification.AddFlangesAtEnds), "bride auto.png", "Ajoute automatiquement des brides aux extrémités sélectionnées", null),
                ("Choix bride", "Choix\nbride", typeof(Modification.PickDefaultFlange), "safeimagekit-bouton reset4.png", "Permet de choisir la bride par défaut", null),
                ("suppression bride", "suppression\nbride", typeof(Modification.RemoveFlangesReconnect), "bride suppresion.png", "Supprime les brides et reconnecte", null),
            }
        );

        AddSplitButton(
            panelEditing, "chatbot IA", "Outils Canalisations", assemblyPath,
            new List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)>
            {
                ("dynamo 1", "auto\ndynamo 1", typeof(Modification.RunDynamo1Command), "dynamo 1.png", "Lance le script Dynamo n°1.", null),
                ("dynamo 2", "auto\ndynamo 2", typeof(Modification.RunDynamo2Command), "dynamo 2.png", "Lance le script Dynamo n°2.", null),
                ("dynamo 3", "auto\ndynamo 3", typeof(Modification.RunDynamo3Command), "dynamo 3.png", "Lance le script Dynamo n°3.", null),
                ("dynamo 4", "auto\ndynamo 4", typeof(Modification.RunDynamo4Command), "dynamo 4.png", "Lance le script Dynamo n°4.", null),
                ("dynamo 5", "auto\ndynamo 5", typeof(Modification.RunDynamo5Command), "dynamo 5.png", "Lance le script Dynamo n°5.", null),
                ("dynamo réglage", "auto dynamo\nréglage", typeof(Modification.ConfigureDynamoButtonCommand), "réglage.png", "Configure les paramètres Dynamo", null),
            }
        );

        AddPushButton(
            panelEditing, "GestionExcelCmd", "Gestion\nExcel",
            assemblyPath, typeof(ScheduleIO.ScheduleExcelIOCommand),
            "export import Excel.png",
            "Exporter ou importer une nomenclature au format Excel."
        );

        AddPushButton(
            panelEditing, "Purge du plan", "purge du\nplan",
            assemblyPath, typeof(Modification.CombinedCleanupCommand),
            "purge.png",
            "Supprime les vues non placées, familles et nomenclatures inutilisées (choix fins)."
        );

        AddPushButton(
            panelEditing, "Auto canalisation", "(Béta)Auto\ncanalisation",
            assemblyPath, typeof(Modification.ConnectPipesCommand),
            "cana auto.png",
            "Connecte automatiquement les canalisations sélectionnées en évitant les obstacles."
        );

        // ============================
        // IA
        // ============================
        AddPushButton(
            panelIA, "GPTBotWindowButton", "Chatbot\n+ élément",
            assemblyPath, typeof(IA.GPTBotWindowCommand),
            "Image IA.png",
            "Assistant IA basé sur GPT; infos détaillées sur éléments; conversation interactive."
        );

        AddPushButton(
            panelIA, "TextCorrectionButton", "Correction de\ntexte IA",
            assemblyPath, typeof(IA.TextCorrectionCommand),
            "safeimagekit-correction de texte IA.png",
            "Corrige et reformule les textes (styles : pro, cool, baratin, personnalisé)."
        );

        AddPushButton(
            panelIA, "ScanText", "ScanText\nIA",
            assemblyPath, typeof(ScanTextRevit.SelectViewsCommand),
            "safeimagekit-qfdfsf.png",
            "Analyse les textes visibles et propose des corrections avec explications."
        );

        // ============================
        // COULEUR
        // ============================
        AddSplitButton(
            panelCouleur, "Changement de couleur", "couleur\nOui/Non", assemblyPath,
            new List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)>
            {
                ("couleur de projet", "couleur\nOui/Non", typeof(Couleur.ToggleCombinedColoringCommand), "bouton lumière.png", "Active/Désactive les couleurs du projet (simple/double clic)", null),
                ("couleur de maquette", "couleur reset", typeof(Couleur.ResetTabItemRandomColorsCommand), "safeimagekit-bouton reset4.png", "Réinitialise les couleurs appliquées", null),
            }
        );

        AddPushButton(
            panelCouleur, "papa Noël", "papa\nNoël",
            assemblyPath, typeof(Couleur.PapanoelCommand),
            "Père Noël.png",
            "Fait apparaître des couleurs type guirlandes.\r\nDouble-clic = retour à la normale (désactiver 'couleur Oui/Non' avant)."
        );

        // ============================
        // ANALYSE
        // ============================
        AddPushButton(
            panelAnalysis, "PipeLengthByDiameterV2", "Calcul des\ncanalisations",
            assemblyPath, typeof(Analyse.PipeLengthByDiameterCommandV2),
            "Canalisation.png",
            "Longueurs/volumes par diamètre; accessoires; filtre type système; export Excel."
        );

        AddPushButton(
            panelAnalysis, "Qui a fait ça ?", "Qui a\nfait ça ??",
            assemblyPath, typeof(Analyse.MainCommand),
            "Qui à fait ça.png",
            "Créateur/éditeur de vues/éléments; dernière synchronisation du modèle."
        );

        AddPushButton(
            panelAnalysis, "AnalysePoidsButton", "Analyse de \nPoids",
            assemblyPath, typeof(Analyse.CommandAnalysePoids),
            "Calcule de poid1.png",
            "Taille familles (Mo), nb d'instances, imports CAO/Liens, export Excel."
        );

        AddPushButton(
            panelAnalysis, "Temps par projet", "Temps par\nprojet",
            assemblyPath, typeof(BIMaestro.Dashboard.ShowTimeDashboard),
            "analyse de temps.png",
            "Affiche le temps passé par projet."
        );

        AddPushButton(
            panelAnalysis, "Chek 3D", "Chek 3D",
            assemblyPath, typeof(Analyse.SmartCheckCommand),
            "correction 3D.png",
            "Vérifie les éléments 3D sélectionnés pour détecter des incohérences."
        );

        // ============================
        // FAMILLE
        // ============================
        AddSplitButton(
            panelFamille, "FamilyBrowser", "Navigateur\nde Familles", assemblyPath,
            new List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)>
            {
                ("FamilyBrowser", "Navigateur\nde Familles", typeof(Famille.FamilyBrowserCommand), "maison famille (1).png",
                    "Parcourir/charger des familles, favoris, filtres, thèmes.", null),
                ("Rosace", ".", typeof(BIMaestro.UI.RadialMenuCommand), "vide.png",
                    "Rosace des familles à ajouter (raccourci clavier/souris).", null),
            }
        );

        AddPushButton(
            panelFamille, "PurgeFamilyParameters", "Purge des\nparamètres",
            assemblyPath, typeof(Famille.PurgeFamilyParametersCommand),
            "Purge famille32x32.png",
            "Supprime les paramètres inutilisés d'une famille (sauvegarde auto, vérifs)."
        );

        AddSplitButton(
            panelFamille, "Changement d'unité", "Changement\nd'unité", assemblyPath,
            new List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)>
            {
                ("Export d'unité", "Export\nd'unité", typeof(Famille.ExportProjectUnitsCommand), "export unité.png",
                    "Exporte les unités du projet.", null),
                ("Import d'unité", "Import\nd'unité", typeof(Famille.ImportProjectUnitsCommand), "import unité.png",
                    "Importe des unités dans le projet.", null),
            }
        );
    }

    // ============================
    // HELPERS
    // ============================
    private static void AddPushButton(
        RibbonPanel panel,
        string buttonName,
        string buttonText,
        string assemblyPath,
        Type commandType,
        string resourceImageName,
        string toolTip,
        Type availabilityType = null)
    {
        if (commandType == null) throw new ArgumentNullException(nameof(commandType));
        var buttonData = new PushButtonData(buttonName, buttonText, assemblyPath, commandType.FullName);

        // Tooltips (court + long)
        string tt = toolTip ?? "";
        string shortTip = tt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
        if (string.IsNullOrWhiteSpace(shortTip)) shortTip = $"Exécuter {buttonText}";
        buttonData.ToolTip = shortTip;
        if (!string.IsNullOrWhiteSpace(tt)) buttonData.LongDescription = tt;

        // Icône
        SetLargeImage(buttonData, resourceImageName);

        // Availability optionnelle
        if (availabilityType != null)
            buttonData.AvailabilityClassName = availabilityType.FullName;

        panel.AddItem(buttonData);
    }

    private static void AddSplitButton(
        RibbonPanel panel,
        string splitButtonName,
        string splitButtonText,
        string assemblyPath,
        List<(string buttonName, string buttonText, Type commandType, string resourceImageName, string toolTip, Type availabilityType)> buttons,
        string splitToolTip = null,
        string splitToolTipImageResource = null)
    {
        var splitButtonData = new SplitButtonData(splitButtonName, splitButtonText);
        var splitButton = panel.AddItem(splitButtonData) as SplitButton;

        if (!string.IsNullOrWhiteSpace(splitToolTip))
            splitButton.ToolTip = splitToolTip;

        if (!string.IsNullOrWhiteSpace(splitToolTipImageResource))
        {
            var img = LoadBitmapFromResource(splitToolTipImageResource);
            if (img != null) splitButton.ToolTipImage = img; // Revit 2023+
        }

        foreach (var (buttonName, buttonText, commandType, resourceImageName, toolTip, availabilityType) in buttons)
        {
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));

            var data = new PushButtonData(buttonName, buttonText, assemblyPath, commandType.FullName);

            string tt = toolTip ?? "";
            string shortTip = tt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            if (string.IsNullOrWhiteSpace(shortTip)) shortTip = $"Exécuter {buttonText}";
            data.ToolTip = shortTip;
            if (!string.IsNullOrWhiteSpace(tt)) data.LongDescription = tt;

            SetLargeImage(data, resourceImageName);

            if (availabilityType != null)
                data.AvailabilityClassName = availabilityType.FullName;

            splitButton.AddPushButton(data);
        }
    }

    private static void SetLargeImage(PushButtonData data, string resourceImageName)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resourcePath = $"BIMaestro.Resources.{resourceImageName}";
        using (Stream stream = asm.GetManifestResourceStream(resourcePath))
        {
            if (stream != null)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.EndInit();
                data.LargeImage = image;
            }
            else
            {
                TaskDialog.Show("Image introuvable",
                    $"L'image intégrée pour {data.Text} n'a pas été trouvée ({resourceImageName}).");
            }
        }
    }

    private static BitmapImage LoadBitmapFromResource(string resourceFileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resourcePath = $"BIMaestro.Resources.{resourceFileName}";
        using (var stream = asm.GetManifestResourceStream(resourcePath))
        {
            if (stream == null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.EndInit();
            return bmp;
        }
    }

    // ============================
    // API utilitaires
    // ============================
    public static List<RibbonPanel> GetRibbonPanels() => ribbonPanels;

    public static void SetUiApplication(UIApplication uiapp) => UiApplication = uiapp;

    public static UIApplication GetUiApplication() => UiApplication;

    public static Document GetCurrentDocument() => UiApplication?.ActiveUIDocument?.Document;
}
