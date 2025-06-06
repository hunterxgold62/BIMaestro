using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

public class AppUI : IExternalApplication
{
    private static List<RibbonPanel> ribbonPanels = new List<RibbonPanel>();
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
        string tabName = "BIMaestro";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Exception)
        {
            // Ignorer l'exception si l'onglet existe déjà
        }

        // Créer les panneaux
        RibbonPanel panelVisualization = application.CreateRibbonPanel(tabName, "Outils de Visualisation");
        ribbonPanels.Add(panelVisualization);

        RibbonPanel panelEditing = application.CreateRibbonPanel(tabName, "Modification");
        ribbonPanels.Add(panelEditing);

        RibbonPanel panelIA = application.CreateRibbonPanel(tabName, "Outils IA");
        ribbonPanels.Add(panelIA);

        //RibbonPanel panelTest = application.CreateRibbonPanel(tabName, "Panneaux réservés au test");
        //ribbonPanels.Add(panelTest);

        RibbonPanel panelAnalysis = application.CreateRibbonPanel(tabName, "Analyse");
        ribbonPanels.Add(panelAnalysis);

        RibbonPanel panelFamille = application.CreateRibbonPanel(tabName, "Spécifique aux familles");
        ribbonPanels.Add(panelFamille);


        RibbonPanel panelCouleur = application.CreateRibbonPanel(tabName, "Couleur du projet");
        ribbonPanels.Add(panelFamille);

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        // Boutons préexistants
        AddPushButton(panelVisualization, "HighlightElementsByCategories", "Sélection\nd'élements", assemblyPath, "Visualisation.HighlightElementsByCategoriesCommand", "Safeimagekit-resized-img (1).png", "Cette commande permet de :  \r\n- Mettre en évidence et filtrer les éléments d'une ou plusieurs catégories.  \r\n- Regrouper automatiquement les éléments similaires.  \r\n- Simplifier la gestion et la sélection précise dans un projet Revit.  \r\n\r\nUtilité : Facilite les actions répétitives et assure un traitement efficace.  ");
        AddPushButton(panelVisualization, "GetPaintedMaterialsButton", "Peinture de\nmatériaux", assemblyPath, "Visualisation.GetPaintedMaterialsCommand", "Peinture et matériaux.png", "Permet d'obtenir une liste des matériaux appliqués à un élément Revit, qu'il s'agisse de matériaux directement associés à l'objet ou de matériaux de peinture appliqués sur ses faces. Une fenêtre d'information affiche les matériaux identifiés pour mieux comprendre la composition de l'élément sélectionné.");
        AddPushButton(panelVisualization, "OpenSheetFromViewButton", "Ouvrir la vue\ndu Plan", assemblyPath, "Visualisation.OpenSheetFromView", "safeimagekit-doc.png", "Cette commande permet de basculer entre une vue active (plan, coupe ou 3D) et les feuilles qui la contiennent, ou d'ouvrir une vue directement depuis un viewport sélectionné sur une feuille. \n\nElle simplifie la navigation entre les feuilles et les vues associées dans un projet Revit.");
        AddPushButton(panelVisualization, "ReorientViewButton", "Réorienter\nVue 3D", assemblyPath, "Visualisation.ReorientViewCommand", "Element 3D.png", "Permet de réorienter une vue 3D active en fonction de la géométrie d'une face sélectionnée.");
        AddPushButton(panelVisualization, "Information d'élément", "Information\nd'élément", assemblyPath, "IA.SelectElementsCommand", "safeimagekit-Information.png", "Ce module utilitaire fournit des méthodes avancées pour :\r\n\r\n- Identifier les matériaux appliqués aux éléments du modèle.\r\n- Obtenir des paramètres personnalisés liés à la géométrie et aux dimensions.\r\nCalculer la surface au sol et le volume des éléments, avec une distinction basée sur la catégorie (toit, plancher, etc.).");
        AddPushButton(panelVisualization, "Export Nomenclature", "Export \nNomenclature", assemblyPath, "Visualisation.ExportImportScheduleCommand", "rvt to excel et pdf.png", "Exporte les nomenclatures Revit sélectionné en fichier excel ou PDF.");


        AddPushButton(panelEditing, "OverrideColor", "Changer couleur\nélément", assemblyPath, "Modification.OverrideColorCommand", "Pallette de couleur anexe .png", "Cette commande permet :  \r\n- De personnaliser les couleurs, motifs et transparence des éléments.  \r\n- D'appliquer des paramètres graphiques à plusieurs vues simultanément.  \r\n- De réinitialiser les modifications si nécessaire.  \r\n\r\nUtilité : Améliorez le rendu et la lisibilité de vos vues.  ");
        AddPushButton(panelEditing, "ElementRenamerButton", "Organisateur\nd'Éléments", assemblyPath, "Modification.RenameElementsCommand", "Safeimagekit-resized-img (4).png", "Cette commande permet :  \r\n- De renommer des éléments sélectionnés dans Revit avec des préfixes, suffixes, ou des numérotations personnalisées.  \r\n- De trier les éléments par niveau ou par emplacement dans la vue active.  \r\n- De réinitialiser les paramètres texte sélectionnés si nécessaire.  \r\n\r\nUtilité :  \r\nFacilite la gestion des noms d'éléments pour une organisation cohérente dans vos projets.");
        AddPushButton(panelEditing, "ResérvationAuto", "Auto \nRéservation", assemblyPath, "Modification.ReservationAutoMultiCommand", "safeimagekit-Réservation.png", "Crée des réservations automatique");

        AddSplitButton(panelEditing, "chatbot IA", "Outils Canalisations", assemblyPath,
           new List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)>
           {
                ("dynamo 1", "auto\ndynamo 1", "Modification.RunDynamo1Command", "dynamo 1.png","dynamo 1"),
                ("dynamo 2", "auto\ndynamo 2", "Modification.RunDynamo2Command", "dynamo 2.png","dynamo 2"),
                ("dynamo 3", "auto\ndynamo 3", "Modification.RunDynamo3Command", "dynamo 3.png","dynamo 3"),
                ("dynamo 4", "auto\ndynamo 4", "Modification.RunDynamo4Command", "dynamo 4.png","dynamo 4"),
                ("dynamo 5", "auto\ndynamo 5", "Modification.RunDynamo5Command", "dynamo 5.png","dynamo 5"),
                ("dynamo réglage", "auto dynamo\nréglage", "Modification.ConfigureDynamoButtonCommand", "réglage.png","eeeee"),
           });


        AddSplitButton(panelIA, "chatbot IA", "Outils Canalisations", assemblyPath,
            new List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)>
            {
                                ("GPTBotWindowButton", "Chatbot\n+ élement", "IA.GPTBotWindowCommand", "Image IA.png","Cette commande permet :  \r\n- D'envoyer des questions ou des demandes d'analyse à un assistant IA basé sur GPT.  \r\n- De récupérer des informations détaillées sur les éléments sélectionnés dans Revit (niveau, matériaux, surface, volume).  \r\n- D'afficher une conversation interactive avec l'IA directement dans une interface dédiée.  \r\n- De personnaliser le profil du chatbot pour s'adapter à différents contextes (BIM Manager, utilisateur Revit, etc.).  \r\n\r\nUtilité :  \r\nOptimisez votre travail dans Revit grâce à un assistant intelligent capable de fournir des conseils, des analyses, et des informations détaillées. "),
                ("ScreenCaptureButton", "Chatbot\n+ screen", "IA.ScreenCaptureCommand", "Safeimagekit-resized-img (5).png","Cette commande permet :  \r\n- De capturer des captures d'écran en sélectionnant une zone spécifique.  \r\n- D'enregistrer et gérer les captures dans un répertoire dédié.  \r\n- D'envoyer les captures d'écran et des messages texte à une API IA pour traitement.  \r\n- D'afficher les réponses de l'IA directement dans l'interface.  \r\n\r\nUtilité :  \r\nAutomatisez l'analyse d'images et intégrez des workflows basés sur l'IA pour gagner du temps. ")
            });
        AddPushButton(panelIA, "TextCorrectionButton", "Correction de\ntexte IA", assemblyPath, "IA.TextCorrectionCommand", "safeimagekit-correction de texte IA.png", "Cette commande permet :  \r\n- De corriger les fautes dans les textes sélectionnés dans Revit.  \r\n- De reformuler les textes dans différents styles : professionnel, cool, baratin ou personnalisé.  \r\n- D'interagir avec une interface utilisateur pour accepter, modifier ou ignorer les corrections proposées.  \r\n- D'utiliser une IA avancée (basée sur GPT) pour produire des textes plus clairs et sans erreurs.  \r\n\r\nUtilité :  \r\nAméliorez rapidement la qualité des textes dans vos annotations Revit grâce à une correction automatisée et personnalisable.  ");
        AddPushButton(panelIA, "ScanText", "ScanText\nIA", assemblyPath, "ScanTextRevit.SelectViewsCommand", "safeimagekit-qfdfsf.png", "Corrige automatiquement les fautes d'orthographe et de grammaire dans les textes visibles sur les vues ou feuilles du projet. \r\nL'IA analyse les textes scannés par chunk et indique les erreurs ligne par ligne avec explication. \r\nLes corrections sont classées en \"Mineur\" (ponctuation, espaces) ou \"Erreur\" (grammaire, orthographe).\r\n");


       // AddSplitButton(panelTest, "Auto-Canalisation", "Auto-\nCanalisation", assemblyPath,
          // new List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)>
          // {
          //      ("Auto-Canalisation", "Auto-\nCanalisation", "RevitAddin.UltimatePipeConnector", "mini-AutocanalisationV2.png","Permet la liaision la plus simple et rapide entre 2 canalisations"),
          //      ("Auto-Canalisation avec obstacle", "Auto-Canalisation\n avec obstacle", "RevitAddin.ConnectTwoPipesAvoidDupConnection", "mini-AutocanalisationV2.png","Permet la liaison la plus simple et rapide entre 2 canalisations en fonction d'un ou plusieurs obstacles sélectionnés")
          // });
        //AddPushButton(panelTest, "Auto-CanalisationV3", "Auto-\nCanalisationV3", assemblyPath, "RevitAddin.AutoCanalisationSettingsCommand", "mini-AutocanalisationV2.png", "Paramètre Auto-Canalisation avec obstacle");

       // AddPushButton(panelTest, "text maj", "text maj", assemblyPath, "MiseAJourCommand", "safeimagekit-Texte maj.png", "ouvre une feuille visualisable en temps réels");
       
       // AddPushButton(panelTest, "popup", "popup", assemblyPath, "MyRevitTroll.CommandStickmanAnimation", "safeimagekit-Texte maj.png", "ouvre une feuille visualisable en temps réels");

       /// AddPushButton(panelTest, "popup2", "popup2", assemblyPath, "MyRevitTroll.CommandHeartsParam", "wtf bouton.png", "ouvre une feuille visualisable en temps réels");

        ///AddPushButton(panelTest, "popup3", "popup3", assemblyPath, "RandomImageAddin.ShowVirusPopupsCommand", "attention bouton.png", "ouvre une feuille visualisable en temps réels");

       // AddPushButton(panelTest, "menu context", "menu context", assemblyPath, "TonNamespace.CommandMenuContextuel", "menu contextuel.png", "ouvre une feuille visualisable en temps réels");

        //  AddPushButton(panelTest, "menu conteeext", "menu coneetext", assemblyPath, "MyFlangePlugin.AddFlangesCommand", "menu contextuel.png", "ouvre une feuille visualisable en temps réels");



        AddSplitButton(panelCouleur, "Changement de couleur", "couleur\nOui/Non", assemblyPath,
            new List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)>
            {
                ("couleur de projet", "couleur\nOui/Non", "MyRevitPlugin.ToggleCombinedColoringCommand", "bouton lumière.png",""),
                ("couleur de maquette", "couleur reset", "MyRevitPlugin.ResetTabItemRandomColorsCommand", "safeimagekit-bouton reset4.png","")
            });        
        AddPushButton(panelCouleur, "papa Noël", "papa Noël", assemblyPath, "MyRevitPlugin.PapanoelCommand", "Père Noël.png", "Fait apparaître des couleurs comme des guirlandes\nDouble clic pour revenir à la normale.\n\nAttention désactiver <couleur Oui/Non> avant activation.");




        AddPushButton(panelAnalysis, "PipeLengthByDiameterV2", "Calcule des\ncanalisations", assemblyPath, "MyRevitPluginV2.PipeLengthByDiameterCommandV2", "Canalisation.png", "Description :\r\n- Calcule les longueurs des canalisations et gaines par diamètre (DN ou dimensions).\r\n- Compte les accessoires de type coudes et tés par diamètre.\r\n- Estime les volumes d'eau par diamètre intérieur.\r\n- Intègre un filtre par type de système pour une analyse précise.\r\n- Permet d'inclure ou non les gaines dans les calculs.\r\n- Exporte les résultats sous forme de tableau Excel détaillé.\r\n\r\nUtilité :\r\nOptimisez votre gestion des systèmes MEP en obtenant rapidement une analyse précise des longueurs, volumes et accessoires, avec possibilité d'exportation.");
        AddPushButton(panelAnalysis, "Qui a fait ça ?", "Qui a fait ça ??", assemblyPath, "MyRevitPlugin.MainCommand", "Qui à fait ça.png", "Description :\r\n- **Créateur de la vue active** : Identifie qui a créé et modifié la vue actuellement affichée.\r\n- **Créateur des éléments sélectionnés** : Récupère les informations de création et de modification pour un élément sélectionné.\r\n- **Dernière synchronisation** : Affiche l'utilisateur ayant effectué la dernière synchronisation du modèle.\r\n\r\nUtilité :\r\nFacilitez le suivi des responsabilités et identifiez rapidement les auteurs ou éditeurs des éléments et des vues dans un environnement collaboratif partagé.");
        AddPushButton(panelAnalysis, "AnalysePoidsButton", "Analyse de \nPoids", assemblyPath, "AnalysePoidsPlugin.CommandAnalysePoids", "Calcule de poid1.png", "Fonctionnalités principales :\r\n1. **Analyse des Familles** :\r\n   - Taille de chaque famille (Mo).\r\n   - Nombre d'instances pour chaque famille.\r\n   - Classement par taille décroissante.\r\n\r\n2. **Analyse des Imports CAO** :\r\n   - Taille des imports (Mo).\r\n   - Types d'éléments analysés : Imports CAO, Lien Revit/IFC.\r\n\r\n3. **Export des Résultats** :\r\n   - Export vers un fichier Excel (RevitLogs/TailleFamilleRevit).\r\n   - Organisation claire par nom, type, taille et nombre d'instances.\r\n\r\nUtilité :\r\n- Identifier les éléments volumineux dans votre projet.\r\n- Optimiser la performance du modèle en réduisant les familles et les imports inutiles.");


        AddPushButton(panelFamille, "PurgeFamilyParameters", "Purge des\nparamètres", assemblyPath, "MyRevitPlugin.PurgeFamilyParametersCommand", "Purge famille32x32.png", "Cette commande permet :  \r\n- De supprimer les paramètres inutilisés dans une famille Revit.  \r\n- De vérifier les cotes, formules et contraintes pour déterminer si un paramètre est utilisé.  \r\n- De sauvegarder automatiquement une copie de la famille avant la purge.  \r\n\r\nUtilité :  \r\nOptimisez vos familles en éliminant les paramètres inutiles, réduisant leur complexité et taille.  \r\n");
        AddPushButton(panelFamille, "FamilyBrowser", "Navigateur\nde Familles", assemblyPath, "FamilyBrowserPlugin.FamilyBrowserCommand", "maison famille (1).png", "Cette commande permet :  \r\n- De parcourir les dossiers et charger des familles Revit depuis un emplacement centralisé.  \r\n- D'afficher des aperçus d'icônes pour identifier rapidement les familles.  \r\n- De gérer des favoris pour accéder plus facilement aux familles les plus utilisées.  \r\n- D'appliquer des filtres de recherche pour une sélection rapide.  \r\n- D'ajuster le thème (mode clair/sombre) et les paramètres visuels.  \r\n\r\nUtilité :  \r\nSimplifie la gestion et le chargement des familles dans vos projets, augmentant votre efficacité.  ");
        AddPushButton(panelFamille, "Familytraduction", "Traduction de\nparamétre IA", assemblyPath, "MonPluginRevit.TraduireParametresFamilleOpenAI", "Pour paramètre de famille1.png", "Cette commande permet :  \r\n- De traduire les noms des paramètres utilisateur dans une famille Revit en français.  \r\n- De s'assurer que les paramètres déjà en français ne sont pas modifiés.  \r\n- D'utiliser l'API OpenAI pour garantir une traduction précise.  \r\n- De sauvegarder automatiquement les changements via une transaction.  \r\n\r\nUtilité :  \r\nFacilite l'adaptation des familles Revit à des projets nécessitant des noms de paramètres en français, améliorant la lisibilité et la conformité.  ");
    }

    private static void AddPushButton(RibbonPanel panel, string buttonName, string buttonText, string assemblyPath, string className, string resourceImageName, string toolTip)
    {
        PushButtonData buttonData = new PushButtonData(buttonName, buttonText, assemblyPath, className)
        {
            ToolTip = toolTip
        };

        var assembly = Assembly.GetExecutingAssembly();
        string resourcePath = $"BIMaestro.Resources.{resourceImageName}";

        using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
        {
            if (stream != null)
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.EndInit();
                buttonData.LargeImage = image;
            }
            else
            {
                TaskDialog.Show("Image introuvable", $"L'image intégrée pour {buttonText} n'a pas été trouvée.");
            }
        }

        panel.AddItem(buttonData);
    }

    private static void AddSplitButton(RibbonPanel panel, string splitButtonName, string splitButtonText, string assemblyPath, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons)
    {
        SplitButtonData splitButtonData = new SplitButtonData(splitButtonName, splitButtonText);
        SplitButton splitButton = panel.AddItem(splitButtonData) as SplitButton;

        foreach (var (buttonName, buttonText, className, resourceImageName, toolTip) in buttons)
        {
            PushButtonData buttonData = new PushButtonData(buttonName, buttonText, assemblyPath, className)
            {
                ToolTip = $"Exécuter {buttonText}"
            };

            var assembly = Assembly.GetExecutingAssembly();
            string resourcePath = $"BIMaestro.Resources.{resourceImageName}";

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream != null)
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.EndInit();
                    buttonData.LargeImage = image;
                }
                else
                {
                    TaskDialog.Show("Image introuvable", $"L'image intégrée pour {buttonText} n'a pas été trouvée.");
                }
            }

            splitButton.AddPushButton(buttonData);
        }
    }

    public static List<RibbonPanel> GetRibbonPanels()
    {
        return ribbonPanels;
    }

    public static void SetUiApplication(UIApplication uiapp)
    {
        UiApplication = uiapp;
    }

    public static UIApplication GetUiApplication()
    {
        return UiApplication;
    }

    public static Document GetCurrentDocument()
    {
        return UiApplication?.ActiveUIDocument?.Document;
    }
}