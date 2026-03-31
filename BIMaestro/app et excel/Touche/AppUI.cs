using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.RibbonLayout;
using Modification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;

public class AppUI : IExternalApplication
{
    private static readonly List<RibbonPanel> ribbonPanels = new List<RibbonPanel>();
    public static UIApplication UiApplication { get; private set; }

    private static readonly Dictionary<string, RibbonButtonInfo> ribbonButtonRegistry =
        new Dictionary<string, RibbonButtonInfo>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ribbonButtonOrder = new List<string>();
    private static readonly Dictionary<string, RibbonButtonInfo> ribbonButtonsByCommandClass =
        new Dictionary<string, RibbonButtonInfo>(StringComparer.OrdinalIgnoreCase);

    public Result OnStartup(UIControlledApplication application)
    {
        CreateRibbonUI(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }

    public static List<RibbonPanelDefinition> BuildDefaultRibbonDefinitions(string assemblyPath)
    {
        return new List<RibbonPanelDefinition>
        {
            new RibbonPanelDefinition("Outils de Visualisation", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("HighlightElementsByCategories", "Sélection d'éléments", panel => AddPushButton(panel, "HighlightElementsByCategories", "Sélection\nd'éléments", assemblyPath, "Visualisation.HighlightElementsByCategoriesCommand", "Safeimagekit-resized-img (1).png", "Cette commande permet de :  \r\n- Mettre en évidence et filtrer les éléments d'une ou plusieurs catégories.  \r\n- Regrouper automatiquement les éléments similaires.  \r\n- Simplifier la gestion et la sélection précise dans un projet Revit.  \r\n\r\nUtilité : Facilite les actions répétitives et assure un traitement efficace.  ")),
                new RibbonItemDefinition("OpenSheetFromViewButton", "Ouvrir la vue du Plan", panel => AddPushButton(panel, "OpenSheetFromViewButton", " Ouvrir \nla vue", assemblyPath, "Visualisation.OpenSheetFromView", "safeimagekit-doc.png", "Cette commande permet de basculer entre une vue active (plan, coupe ou 3D) et les feuilles qui la contiennent, ou d'ouvrir une vue directement depuis un viewport sélectionné sur une feuille. \n\nElle simplifie la navigation entre les feuilles et les vues associées dans un projet Revit.")),
                new RibbonItemDefinition("Export Nomenclature", "Export Nomenclature", panel => AddPushButton(panel, "Export Nomenclature", "Export de\nNomenclature", assemblyPath, "Visualisation.ExportScheduleCommand", "rvt to excel et pdf.png", "Exporte les nomenclatures Revit sélectionnées en fichier Excel ou PDF.")),
                new RibbonItemDefinition("Sélection d'objet", "Sélection d'objet", panel => AddPushButton(panel, "Sélection d'objet", "Sélection\nd'objet", assemblyPath, "Visualisation.SelectSimilarCommand", "Sélection d'élément.png", "Sélectionne des éléments similaires dans le projet")),
                new RibbonItemDefinition("Boutons de Visualisation", "Boutons de Visualisation", panel => AddStackedPushButtons(
            panel,
            assemblyPath,
    
            ("ReorientViewButton", "Face 3D", "Visualisation.ReorientViewCommand", "Element 3D.png",
                "Permet de réorienter une vue 3D active en fonction de la géométrie d'une face sélectionnée."),
            ("ExportDwgBatch", "DWG Exp.", "Visualisation.ExportSheetsCommand", "export DWG.png",
                "Exporte automatiquement plusieurs vues ou feuilles en DWG, en nommant chaque fichier selon le projet et la vue comme pour les PDF."),
            ("GetPaintedMaterialsButton", "Peinture", "Visualisation.GetPaintedMaterialsCommand", "Peinture et matériaux.png",
                "Liste les matériaux (y compris peinture) appliqués à un élément.")
    
        )),

            }),

             new RibbonPanelDefinition("Modification", new List<RibbonItemDefinition>
            {
                
                // new RibbonItemDefinition("ResérvationAuto2", "Auto Réservation2", panel => AddPushButton(panel, "ResérvationAuto2", "Auto\nRéservation2", assemblyPath, "Modification.ReservationAutoMultiVoidCommandV2", "safeimagekit-Réservation.png", "Crée des réservations automatiques")),
                new RibbonItemDefinition("ResérvationAuto", "Auto Réservation", panel => AddPushButton(panel, "ResérvationAuto", " Auto \nRéservation", assemblyPath, "Modification.ReservationAutoV3Command", "résa cercle.png", "Crée des réservations automatiques")),
                //new RibbonItemDefinition("ResérvationAuto", "Auto Réservation", panel => AddPushButton(panel, "ResérvationAuto", "Auto\nRéservation", assemblyPath, "Modification.ReservationAutoMultiCommand", "safeimagekit-Réservation.png", "Crée des réservations automatiques")),
                new RibbonItemDefinition("Bride auto", "Bride auto", panel => AddSplitButton(panel, "Bride auto", "Bride\nauto", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Bride auto", "Bride\nauto", "Modification.AddFlangesAtEnds", "bride auto.png","Ajoute automatiquement des brides aux extrémités sélectionnées"),
                    ("Choix bride", "Choix\nbride", "Modification.PickDefaultFlange", "safeimagekit-bouton reset4.png","Permet de choisir la bride par défaut"),
                    ("Suppression de brides", "Suppression\nde brides", "Modification.RemoveFlangesReconnect", "bride suppresion.png","Permet de supprimer les brides")
                })),
                new RibbonItemDefinition("Dynamo auto", "Dynamo Auto", panel => AddSplitButton(panel, "Dynamo auto", "Dynamo\nAuto", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("dynamo 1", DynamoSettings.GetLabel(0), "Modification.RunDynamo1Command", "dynamo 1.png","Lance le script Dynamo n°1."),
                    ("dynamo 2", DynamoSettings.GetLabel(1), "Modification.RunDynamo2Command", "dynamo 2.png","Lance le script Dynamo n°2."),
                    ("dynamo 3", DynamoSettings.GetLabel(2), "Modification.RunDynamo3Command", "dynamo 3.png","Lance le script Dynamo n°3."),
                    ("dynamo 4", DynamoSettings.GetLabel(3), "Modification.RunDynamo4Command", "dynamo 4.png","Lance le script Dynamo n°4."),
                    ("dynamo 5", DynamoSettings.GetLabel(4), "Modification.RunDynamo5Command", "dynamo 5.png","Lance le script Dynamo n°5."),
                    ("dynamo réglage", "Auto dynamo\nréglage", "Modification.ConfigureDynamoButtonCommand", "réglage.png","Configure les paramètres Dynamo"),
                })),
                new RibbonItemDefinition("GestionExcelCmd", "Gestion Excel", panel => AddPushButton(panel, "GestionExcelCmd", "Gestion\nExcel", assemblyPath, "ScheduleIO.ScheduleExcelIOCommand", "export import Excel.png", "Exporter ou importer une nomenclature au format Excel")),
                new RibbonItemDefinition("ModificationQuickTools", "Outils rapides", panel => AddStackedPushButtons(
                        panel,
                        assemblyPath,
                        ("OverrideColor", "Couleur", "Modification.OverrideColorCommand", "Pallette de couleur anexe .png", "Cette commande permet :  \r\n- De personnaliser les couleurs, motifs et transparence des éléments.  \r\n- D'appliquer des paramètres graphiques à plusieurs vues simultanément.  \r\n- De réinitialiser les modifications si nécessaire.  \r\n\r\nUtilité : Améliorez le rendu et la lisibilité de vos vues.  "),
                        ("ElementRenamerButton", "Organisateur", "Modification.RenameElementsCommand", "Organisateur d'éléments.png", "Cette commande permet :  \r\n- De renommer des éléments sélectionnés dans Revit avec des préfixes, suffixes, ou des numérotations personnalisées.  \r\n- De trier les éléments par niveau ou par emplacement dans la vue active.  \r\n- De réinitialiser les paramètres texte sélectionnés si nécessaire.  \r\n\r\nUtilité :  \r\nFacilite la gestion des noms d'éléments pour une organisation cohérente dans vos projets."),
                        ("Purge du plan", "Purge", "Modification.CombinedCleanupCommand", "purge.png", "Supprime les vues non placées, les familles et les nomenclatures inutilisées afin d'alléger le projet.\r\nUne fenêtre permet de choisir précisément les éléments à purger avant exécution.\r\n")
                    )),
             }),

            new RibbonPanelDefinition("Outils IA", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("GPTBotWindowButton", "Chatbot + élément", panel => AddPushButton(panel, "GPTBotWindowButton", "Chatbot\n+ élément", assemblyPath, "IA.GPTBotWindowCommand", "Image IA.png", "Cette commande permet :  \r\n- D'envoyer des questions ou des demandes d'analyse à un assistant IA basé sur DeepSeek.  \r\n- De récupérer des informations détaillées sur les éléments sélectionnés dans Revit (niveau, matériaux, surface, volume).  \r\n- D'afficher une conversation interactive avec l'IA directement dans une interface dédiée.  \r\n- De personnaliser le profil du chatbot pour s'adapter à différents contextes (BIM Manager, utilisateur Revit, etc.).  \r\n\r\nUtilité :  \r\nOptimisez votre travail dans Revit grâce à un assistant intelligent capable de fournir des conseils, des analyses, et des informations détaillées.")),
                new RibbonItemDefinition("TextCorrectionButton", "Correction de texte IA", panel => AddPushButton(panel, "TextCorrectionButton", "Correction \nde texte IA", assemblyPath, "IA.TextCorrectionCommand", "safeimagekit-correction de texte IA.png", "Cette commande permet :  \r\n- De corriger les fautes dans les textes sélectionnés dans Revit.  \r\n- De reformuler les textes dans différents styles : professionnel, cool, baratin ou personnalisé.  \r\n- D'interagir avec une interface utilisateur pour accepter, modifier ou ignorer les corrections proposées.  \r\n- D'utiliser une IA avancée (basée sur GPT) pour produire des textes plus clairs et sans erreurs.  \r\n\r\nUtilité :  \r\nAméliorez rapidement la qualité des textes dans vos annotations Revit grâce à une correction automatisée et personnalisable.")),
                new RibbonItemDefinition("ScanText", "ScanText IA", panel => AddPushButton(panel, "ScanText", "ScanText\nIA", assemblyPath, "ScanTextRevit.SelectViewsCommand", "safeimagekit-qfdfsf.png", "Corrige automatiquement les fautes d'orthographe et de grammaire dans les textes visibles sur les vues ou feuilles du projet. \r\nL'IA analyse les textes scannés par chunk et indique les erreurs ligne par ligne avec explication. \r\nLes corrections sont classées en \"Mineur\" (ponctuation, espaces) ou \"Erreur\" (grammaire, orthographe).\r\n"))
            }),

           new RibbonPanelDefinition("Analyse", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("PipeLengthByDiameterV2", "Calcul des canalisations", panel => AddPushButton(panel, "PipeLengthByDiameterV2", "Calcul des\ncanalisations", assemblyPath, "Analyse.PipeLengthByDiameterCommandV2", "Canalisation.png", "Description :\r\n- Calcule les longueurs des canalisations et gaines par diamètre (DN ou dimensions).\r\n- Compte les accessoires de type coudes et tés par diamètre.\r\n- Estime les volumes d'eau par diamètre intérieur.\r\n- Intègre un filtre par type de système pour une analyse précise.\r\n- Permet d'inclure ou non les gaines dans les calculs.\r\n- Exporte les résultats sous forme de tableau Excel détaillé.\r\n\r\nUtilité :\r\nOptimisez votre gestion des systèmes MEP en obtenant rapidement une analyse précise des longueurs, volumes et accessoires, avec possibilité d'exportation.")),
                new RibbonItemDefinition("Qui a fait ça ?", "Qui a fait ça ??", panel => AddPushButton(panel, "Qui a fait ça ?", "Qui a\nfait ça ??", assemblyPath, "Analyse.MainCommand", "Qui à fait ça.png", "Description :\r\n- **Créateur de la vue active** : Identifie qui a créé et modifié la vue actuellement affichée.\r\n- **Créateur des éléments sélectionnés** : Récupère les informations de création et de modification pour un élément sélectionné.\r\n- **Dernière synchronisation** : Affiche l'utilisateur ayant effectué la dernière synchronisation du modèle.\r\n\r\nUtilité :\r\nFacilitez le suivi des responsabilités et identifiez rapidement les auteurs ou éditeurs des éléments et des vues dans un environnement collaboratif partagé.")),
                new RibbonItemDefinition("AnalysePoidsButton", "Analyse de Poids", panel => AddPushButton(panel, "AnalysePoidsButton", "Analyse\nde Poids", assemblyPath, "Analyse.CommandAnalysePoids", "Calcule de poid1.png", "Fonctionnalités principales :\r\n1. **Analyse des Familles** :\r\n   - Taille de chaque famille (Mo).\r\n   - Nombre d'instances pour chaque famille.\r\n   - Classement par taille décroissante.\r\n\r\n2. **Analyse des Imports CAO** :\r\n   - Taille des imports (Mo).\r\n   - Types d'éléments analysés : Imports CAO, Lien Revit/IFC.\r\n\r\n3. **Export des Résultats** :\r\n   - Export vers un fichier Excel (RevitLogs/TailleFamilleRevit).\r\n   - Organisation claire par nom, type, taille et nombre d'instances.\r\n\r\nUtilité :\r\n- Identifier les éléments volumineux dans votre projet.\r\n- Optimiser la performance du modèle en réduisant les familles et les imports inutiles.")),

                new RibbonItemDefinition("Temps par projet", "Temps par projet", panel => AddSplitButton(panel, "Temps par projet", "Temps par\nprojet", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Temps par projet", "Temps par\nprojet", "BIMaestro.Dashboard.ShowTimeDashboard", "analyse de temps.png", "Affiche le temps passé par projet."),
                    ("Suivi maquette collaboratif", "Suivi\nmaquette", "Analyse.CollaborativeModelTrackerCommand", "Collaboration.png", "Crée/consulte un registre JSON + Excel pour toutes les maquettes (suivi auto ouverture/fermeture). Si la maquette n'existe pas, le créateur devient le premier ouvreur.")
                })),
               
                new RibbonItemDefinition("Clash 3D", "Clash 3D", panel => AddPushButton(panel, "Clash 3D", "Clash\n3D", assemblyPath, "Analyse.SmartClashCommand", "correction 3D.png", "Vérifie les éléments 3D sélectionnés pour détecter les incohérences."))
            }),
            
                new RibbonPanelDefinition("Spécifique aux familles", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("FamilyBrowser", "Navigateur de Familles", panel => AddSplitButton(panel, "FamilyBrowser", "Famille", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("FamilyBrowser", "Navigateur\nde Familles", "Famille.FamilyBrowserCommand", "maison famille (1).png","Cette commande permet :  \r\n- De parcourir les dossiers et charger des familles Revit depuis un emplacement centralisé. \r\n- D'afficher des aperçus d'icônes pour identifier rapidement les familles.  \r\n- De gérer des favoris pour accéder plus facilement aux familles les plus utilisées.  \r\n- D'appliquer des filtres de recherche pour une sélection rapide.  \r\n- D'ajuster le thème (mode clair/sombre) et les paramètres visuels.  \r\n\r\nUtilité :  \r\nSimplifie la gestion et le chargement des familles dans vos projets, augmentant votre efficacité.  "),
                    ("Rosace", ".","BIMaestro.UI.RadialMenuCommand", "vide.png","Rosace des familles à ajouter en raccourci clavier voir raccourci souris.")
                }, "Navigateur de Familles", "maison famille (1).png", keepDefaultCurrentButton: true, fixedDisplayText: "Famille")),
        new RibbonItemDefinition("ConvertSharedToFamily", "Convertir les paramètres partagés", panel => AddPushButton(panel, "ConvertSharedToFamily", "Convertir\nparamètres", assemblyPath, "Famille.ConvertSharedToFamilyParametersCommand", "Convertir paramètres1.png", "Convertit tous les paramètres partagés modifiables de la famille en paramètres de famille (même nom, même groupe et même type instance/type).")),                new RibbonItemDefinition("FamilyUtilitiesStack", "Outils familles", panel => AddStackedFamilyUtilities(
                    panel,
                    assemblyPath,
                    ("PurgeFamilyParameters", "Purge", "Famille.PurgeFamilyParametersCommand", "Purge famille32x32.png", "Cette commande permet :  \r\n- De supprimer les paramètres inutilisés dans une famille Revit.  \r\n- De vérifier les cotes, formules et contraintes pour déterminer si un paramètre est utilisé.  \r\n- De sauvegarder automatiquement une copie de la famille avant la purge.  \r\n\r\nUtilité :  \r\nOptimisez vos familles en éliminant les paramètres inutiles, réduisant leur complexité et taille.  \r\n"),
                    ("Familytraduction", "Trad.IA", new List<(string, string, string, string, string)>
                    {
                        ("Familytraduction", "Trad.IA", "Famille.TraduireParametresFamilleOpenAI", "Pour paramètre de famille1.png","Cette commande permet :  \r\n- De traduire les noms des paramètres utilisateur dans une famille Revit en français.  \r\n- De s'assurer que les paramètres déjà en français ne sont pas modifiés.  \r\n- D'utiliser l'API OpenAI pour garantir une traduction précise.  \r\n- De sauvegarder automatiquement les changements via une transaction.  \r\n\r\nUtilité :  \r\nFacilite l'adaptation des familles Revit à des projets nécessitant des noms de paramètres en français, améliorant la lisibilité et la conformité.  \r\n"),
                        ("FamilyViewtraduction", "Traduction\nde vues IA","Famille.TraduireVuesFamilleOpenAI", "Pour paramètre de famille1.png","Cette commande permet :  \r\n- De traduire automatiquement les noms des vues d'une famille Revit en français.  \r\n- De conserver les vues déjà en français sans modification.  \r\n- D'assurer l'unicité des noms générés même en cas de doublon potentiel.  \r\n- D'utiliser l'API OpenAI et le cache de traduction pour accélérer les traitements récurrents.  \r\n\r\nUtilité :  \r\nGarantit une nomenclature cohérente et francisée des vues de famille, améliorant la compréhension et la conformité des contenus.  ")
                    }),
                    ("Export d'unité", "Unités", new List<(string, string, string, string, string)>
                    {
                        ("Export d'unité", "Unités", "Famille.ExportProjectUnitsCommand", "export unité.png","Sauvegarde dans un fichier JSON les unités et leur précision (longueur, surface, volume, angle, etc.) du projet en cours, dans le dossier Mes Documents/RevitLogs/SauvegardePréférence."),
                        ("Import d'unité", "Import\nd'unité","Famille.ImportProjectUnitsCommand", "import unité.png","Recharge depuis le fichier JSON les unités et leur précision pour appliquer rapidement vos préférences au projet.")
                    })))
            }),

            new RibbonPanelDefinition("Couleur et information", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("Changement de couleur", "Changement de couleur", panel => AddSplitButton(panel, "Changement de couleur", "couleur\nOui/Non", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Couleur de projet", "Couleur\nOui/Non", "Couleur.ToggleCombinedColoringCommand", "bouton lumière.png","Active ou désactive les couleurs du projet (simple ou double clic)"),
                    ("Couleur de maquette", "Couleur reset", "Couleur.ResetTabItemRandomColorsCommand", "safeimagekit-bouton reset4.png","Réinitialise les couleurs appliquées"),
                    ("papa Noël", "papa\nNoël", "Couleur.PapanoelCommand", "Père Noël.png","Fait apparaître des couleurs comme des guirlandes\nDouble clic pour revenir à la normale.\n\nAttention désactiver <couleur Oui/Non> avant activation."),
                    ("Snake", "Snake", "BIMaestro.Bonus.SnakeCommand", "snake.png","Petit jeux snake :P"),
                    ("FlappyBird", "Flappy\nBird", "BIMaestro.Bonus.FlappyBirdCommand", "Flappy bird-1.png","Petit jeu Flappy Bird :P"),
                })),

                new RibbonItemDefinition("InfoStack", "Infos empilées", panel => AddStackedInfoButtons(
                    panel,
                    assemblyPath,
                    // (name, text, className, icon, tooltip)
                    ("NOTE_MAJ", "Note", "Page.MiseAJourCommand", "safeimagekit-Information.png", "Page de mise à jour"),
                    ("BIMaestro_Exemple", "Exemple", "Page.GuideCommand", "safeimagekit-Texte maj.png", "Page d'information sur le plugin"),
                    ("CustomizeRibbon", "Option", new List<(string, string, string, string, string)>
                    {
                        ("CustomizeRibbon", "Option", "BIMaestro.RibbonLayout.RibbonLayoutCommand", "roue ruban.png", "Configurer le ruban BIMaestro et les paramètres utilisateur."),
                        ("RadialMenuButtonsCommand", "Rosace\nBoutons", "BIMaestro.UI.RadialMenuButtonsCommand", "roue ruban.png", "Rosace des 16 derniers boutons BIMaestro utilisés.")
                    })
                ))
            })
        };
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
        }
        ribbonButtonRegistry.Clear();
        ribbonButtonOrder.Clear();
        ribbonButtonsByCommandClass.Clear();

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var definitions = BuildDefaultRibbonDefinitions(assemblyPath);
        var layout = RibbonLayoutConfigManager.LoadLayout(definitions);

        ribbonPanels.Clear();
        foreach (var panelConfig in OrderPanels(definitions, layout))
        {
            var panel = application.CreateRibbonPanel(tabName, panelConfig.Name);
            ribbonPanels.Add(panel);

            var definition = definitions.First(d => d.Name == panelConfig.Name);
            foreach (var item in OrderItems(definition, panelConfig))
            {
                item.Builder(panel);
            }
        }
    }

    private static IEnumerable<RibbonPanelConfig> OrderPanels(IEnumerable<RibbonPanelDefinition> definitions, RibbonLayoutConfig layout)
    {
        var defaultNames = definitions.Select(d => d.Name).ToList();
        var seen = new HashSet<string>();

        foreach (var panel in layout.Panels)
        {
            if (seen.Add(panel.Name) && defaultNames.Contains(panel.Name))
            {
                yield return panel;
            }
        }

        foreach (var name in defaultNames)
        {
            if (seen.Add(name))
            {
                yield return new RibbonPanelConfig
                {
                    Name = name,
                    Buttons = definitions.First(d => d.Name == name).Items.Select(i => i.Id).ToList()
                };
            }
        }
    }

    private static IEnumerable<RibbonItemDefinition> OrderItems(RibbonPanelDefinition definition, RibbonPanelConfig config)
    {
        var itemsById = definition.Items.ToDictionary(i => i.Id, i => i);
        var seen = new HashSet<string>();

        foreach (var id in config.Buttons)
        {
            if (itemsById.TryGetValue(id, out var item) && seen.Add(id))
            {
                yield return item;
            }
        }

        foreach (var item in definition.Items)
        {
            if (seen.Add(item.Id))
            {
                yield return item;
            }
        }
    }

    // ====== NOUVEAU : 3 petits boutons empilés (stack) ======
    private static void AddStackedPushButtons(
        RibbonPanel panel,
        string assemblyPath,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) b1,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) b2,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) b3)
    {
        RegisterButtonDefinition(b1.buttonName, b1.buttonText, b1.className, b1.resourceImageName);
        RegisterButtonDefinition(b2.buttonName, b2.buttonText, b2.className, b2.resourceImageName);
        RegisterButtonDefinition(b3.buttonName, b3.buttonText, b3.className, b3.resourceImageName);

        var d1 = CreatePushButtonData(b1.buttonName, b1.buttonText, assemblyPath, b1.className, b1.resourceImageName, b1.toolTip);
        var d2 = CreatePushButtonData(b2.buttonName, b2.buttonText, assemblyPath, b2.className, b2.resourceImageName, b2.toolTip);
        var d3 = CreatePushButtonData(b3.buttonName, b3.buttonText, assemblyPath, b3.className, b3.resourceImageName, b3.toolTip);

        // Revit stacke jusqu'à 3 items (petits) dans une colonne
        var stacked = panel.AddStackedItems(d1, d2, d3);
        if (stacked != null)
        {
            if (stacked.Count > 0 && stacked[0] is PushButton pb1)
            {
                RegisterButtonInstance(b1.buttonName, pb1);
                RegisterButtonCommandId(b1.buttonName, TryGetCommandId(pb1));
            }
            if (stacked.Count > 1 && stacked[1] is PushButton pb2)
            {
                RegisterButtonInstance(b2.buttonName, pb2);
                RegisterButtonCommandId(b2.buttonName, TryGetCommandId(pb2));
            }
            if (stacked.Count > 2 && stacked[2] is PushButton pb3)
            {
                RegisterButtonInstance(b3.buttonName, pb3);
                RegisterButtonCommandId(b3.buttonName, TryGetCommandId(pb3));
            }
        }
    }
    private static void AddStackedPushButtons(
        RibbonPanel panel,
        string assemblyPath,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) b1,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) b2)
    {
        RegisterButtonDefinition(b1.buttonName, b1.buttonText, b1.className, b1.resourceImageName);
        RegisterButtonDefinition(b2.buttonName, b2.buttonText, b2.className, b2.resourceImageName);

        var d1 = CreatePushButtonData(b1.buttonName, b1.buttonText, assemblyPath, b1.className, b1.resourceImageName, b1.toolTip);
        var d2 = CreatePushButtonData(b2.buttonName, b2.buttonText, assemblyPath, b2.className, b2.resourceImageName, b2.toolTip);

        var stacked = panel.AddStackedItems(d1, d2);
        if (stacked != null)
        {
            if (stacked.Count > 0 && stacked[0] is PushButton pb1)
            {
                RegisterButtonInstance(b1.buttonName, pb1);
                RegisterButtonCommandId(b1.buttonName, TryGetCommandId(pb1));
            }
            if (stacked.Count > 1 && stacked[1] is PushButton pb2)
            {
                RegisterButtonInstance(b2.buttonName, pb2);
                RegisterButtonCommandId(b2.buttonName, TryGetCommandId(pb2));
            }
        }
    }

    private static void AddStackedInfoButtons(
        RibbonPanel panel,
        string assemblyPath,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) noteButton,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) exampleButton,
        (string splitButtonName, string splitButtonText, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons) optionSplit)
    {
        RegisterButtonDefinition(noteButton.buttonName, noteButton.buttonText, noteButton.className, noteButton.resourceImageName);
        RegisterButtonDefinition(exampleButton.buttonName, exampleButton.buttonText, exampleButton.className, exampleButton.resourceImageName);

        var noteData = CreatePushButtonData(noteButton.buttonName, noteButton.buttonText, assemblyPath, noteButton.className, noteButton.resourceImageName, noteButton.toolTip);
        var exampleData = CreatePushButtonData(exampleButton.buttonName, exampleButton.buttonText, assemblyPath, exampleButton.className, exampleButton.resourceImageName, exampleButton.toolTip);
        var optionData = new SplitButtonData(optionSplit.splitButtonName, optionSplit.splitButtonText);

        var stacked = panel.AddStackedItems(noteData, exampleData, optionData);
        if (stacked == null)
        {
            return;
        }

        if (stacked.Count > 0 && stacked[0] is PushButton note)
        {
            RegisterButtonInstance(noteButton.buttonName, note);
            RegisterButtonCommandId(noteButton.buttonName, TryGetCommandId(note));
        }

        if (stacked.Count > 1 && stacked[1] is PushButton example)
        {
            RegisterButtonInstance(exampleButton.buttonName, example);
            RegisterButtonCommandId(exampleButton.buttonName, TryGetCommandId(example));
        }

        if (stacked.Count > 2 && stacked[2] is SplitButton option)
        {
            ConfigureSplitButton(option, assemblyPath, optionSplit.buttons, null, null);
        }
    }

    private static void AddStackedFamilyUtilities(
        RibbonPanel panel,
        string assemblyPath,
        (string buttonName, string buttonText, string className, string resourceImageName, string toolTip) purgeButton,
        (string splitButtonName, string splitButtonText, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons) traductionSplit,
        (string splitButtonName, string splitButtonText, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons) unitsSplit)
    {
        RegisterButtonDefinition(purgeButton.buttonName, purgeButton.buttonText, purgeButton.className, purgeButton.resourceImageName);

        var purgeData = CreatePushButtonData(purgeButton.buttonName, purgeButton.buttonText, assemblyPath, purgeButton.className, purgeButton.resourceImageName, purgeButton.toolTip);
        var traductionData = new SplitButtonData(traductionSplit.splitButtonName, traductionSplit.splitButtonText);
        var unitsData = new SplitButtonData(unitsSplit.splitButtonName, unitsSplit.splitButtonText);

        var stacked = panel.AddStackedItems(purgeData, traductionData, unitsData);
        if (stacked == null)
        {
            return;
        }

        if (stacked.Count > 0 && stacked[0] is PushButton purge)
        {
            RegisterButtonInstance(purgeButton.buttonName, purge);
            RegisterButtonCommandId(purgeButton.buttonName, TryGetCommandId(purge));
        }

        if (stacked.Count > 1 && stacked[1] is SplitButton traduction)
        {
            ConfigureSplitButton(traduction, assemblyPath, traductionSplit.buttons, null, null);
        }

        if (stacked.Count > 2 && stacked[2] is SplitButton units)
        {
            ConfigureSplitButton(units, assemblyPath, unitsSplit.buttons, null, null);
        }
    }

    private static void AddPushButton(RibbonPanel panel, string buttonName, string buttonText, string assemblyPath, string className, string resourceImageName, string toolTip)
    {
        RegisterButtonDefinition(buttonName, buttonText, className, resourceImageName);

        var buttonData = CreatePushButtonData(buttonName, buttonText, assemblyPath, className, resourceImageName, toolTip);
        var addedButton = panel.AddItem(buttonData) as PushButton;
        if (addedButton != null)
        {
            RegisterButtonInstance(buttonName, addedButton);
            RegisterButtonCommandId(buttonName, TryGetCommandId(addedButton));
        }
    }

    private static PushButtonData CreatePushButtonData(string buttonName, string buttonText, string assemblyPath, string className, string toolTipImageName, string toolTip)
    {
        PushButtonData buttonData = new PushButtonData(buttonName, buttonText, assemblyPath, className);

        // ToolTip / LongDescription (comme tu faisais)
        string tt = toolTip ?? "";
        string[] parts = tt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        string shortTip = parts.Length > 0 ? parts[0] : "";
        if (string.IsNullOrWhiteSpace(shortTip))
            shortTip = $"Exécuter {buttonText}";
        buttonData.ToolTip = shortTip;

        bool hasMultiLine = parts.Length > 1;
        bool isDifferent = hasMultiLine || (tt.Trim().Length > shortTip.Trim().Length + 5);
        if (!string.IsNullOrWhiteSpace(tt) && isDifferent)
            buttonData.LongDescription = tt;

        // ✅ Important :
        // - Image => 16x16 (boutons "petits" + stacked)
        // - LargeImage => 32x32 (boutons "gros")
        var small = LoadBitmapFromResource(toolTipImageName, 16);
        if (small != null) buttonData.Image = small;

        var large = LoadBitmapFromResource(toolTipImageName, 32);
        if (large != null) buttonData.LargeImage = large;

        return buttonData;
    }

    private static void AddSplitButton(
       RibbonPanel panel,
       string splitButtonName,
       string splitButtonText,
       string assemblyPath,
       List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons,
       string splitToolTip = null,
       string splitToolTipImageResource = null,
       bool keepDefaultCurrentButton = true,
       string fixedDisplayText = null)
    {
        var splitButtonData = new SplitButtonData(splitButtonName, splitButtonText);
        var splitButton = panel.AddItem(splitButtonData) as SplitButton;
        ConfigureSplitButton(splitButton, assemblyPath, buttons, splitToolTip, splitToolTipImageResource, keepDefaultCurrentButton, fixedDisplayText);
    }

    private static void ConfigureSplitButton(
        SplitButton splitButton,
        string assemblyPath,
        List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons,
        string splitToolTip,
        string splitToolTipImageResource,
        bool keepDefaultCurrentButton = true,
        string fixedDisplayText = null)
    {
        if (splitButton == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(splitToolTip))
        {
            splitButton.ToolTip = splitToolTip;

            if (!string.IsNullOrWhiteSpace(splitToolTipImageResource))
            {
                var bmp16 = LoadBitmapFromResource(splitToolTipImageResource, 16);
                if (bmp16 != null) splitButton.Image = bmp16;

                var bmp32 = LoadBitmapFromResource(splitToolTipImageResource, 32);
                if (bmp32 != null) splitButton.LargeImage = bmp32;

                if (bmp32 != null) splitButton.ToolTipImage = bmp32;
            }
        }

        if (buttons.Count > 0)
        {
            var first16 = LoadBitmapFromResource(buttons[0].resourceImageName, 16);
            if (first16 != null) splitButton.Image = first16;

            var first32 = LoadBitmapFromResource(buttons[0].resourceImageName, 32);
            if (first32 != null) splitButton.LargeImage = first32;
        }

        PushButton firstButton = null;
        foreach (var (buttonName, buttonText, className, resourceImageName, toolTip) in buttons)
        {
            RegisterButtonDefinition(buttonName, buttonText, className, resourceImageName);
            var buttonData = CreatePushButtonData(buttonName, buttonText, assemblyPath, className, resourceImageName, toolTip);
            var addedButton = splitButton.AddPushButton(buttonData);
            if (addedButton != null)
            {
                RegisterButtonInstance(buttonName, addedButton);
                RegisterButtonCommandId(buttonName, TryGetCommandId(addedButton));
            }
            if (firstButton == null)
            {
                firstButton = addedButton;
            }
        }

        if (keepDefaultCurrentButton && firstButton != null)
        {
            KeepSplitButtonDefault(splitButton, firstButton);
        }

        KeepSplitButtonText(splitButton, fixedDisplayText);
    }

    private static void KeepSplitButtonText(SplitButton splitButton, string fixedDisplayText)
    {
        if (splitButton == null || string.IsNullOrWhiteSpace(fixedDisplayText))
            return;

        TrySetRibbonItemText(splitButton, fixedDisplayText);

        try
        {
            var eventInfo = splitButton.GetType().GetEvent("CurrentButtonChanged");
            if (eventInfo != null)
            {
                EventHandler handler = (_, __) => TrySetRibbonItemText(splitButton, fixedDisplayText);
                var del = Delegate.CreateDelegate(eventInfo.EventHandlerType, handler.Target, handler.Method);
                eventInfo.AddEventHandler(splitButton, del);
            }
        }
        catch
        {
            // Ignore si l'API ne supporte pas ces hooks.
        }
    }

    private static void TrySetRibbonItemText(object ribbonItem, string text)
    {
        if (ribbonItem == null || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var itemTextProperty = ribbonItem.GetType().GetProperty("ItemText");
            if (itemTextProperty != null && itemTextProperty.CanWrite)
            {
                itemTextProperty.SetValue(ribbonItem, text, null);
                return;
            }

            var textProperty = ribbonItem.GetType().GetProperty("Text");
            if (textProperty != null && textProperty.CanWrite)
            {
                textProperty.SetValue(ribbonItem, text, null);
            }
        }
        catch
        {
            // Ignore si la propriété n'est pas modifiable selon la version Revit.
        }
    }

    private static void KeepSplitButtonDefault(SplitButton splitButton, PushButton defaultButton)
    {
        if (splitButton == null || defaultButton == null)
            return;

        splitButton.CurrentButton = defaultButton;

        try
        {
            var syncProp = splitButton.GetType().GetProperty("IsSynchronizedWithCurrentItem");
            if (syncProp != null && syncProp.CanWrite)
            {
                syncProp.SetValue(splitButton, false, null);
            }

            var eventInfo = splitButton.GetType().GetEvent("CurrentButtonChanged");
            if (eventInfo != null)
            {
                EventHandler handler = (_, __) => splitButton.CurrentButton = defaultButton;
                var del = Delegate.CreateDelegate(eventInfo.EventHandlerType, handler.Target, handler.Method);
                eventInfo.AddEventHandler(splitButton, del);
            }
        }
        catch
        {
            // Ignore si l'API ne supporte pas ces hooks.
        }
    }

    private static BitmapImage LoadBitmapFromResource(string resourceFileName, int decodeSize)
    {
        var asm = Assembly.GetExecutingAssembly();
        string resourcePath = $"BIMaestro.Resources.{resourceFileName}";

        using (var stream = asm.GetManifestResourceStream(resourcePath))
        {
            if (stream == null) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;

            // Force un chargement "petit" (utile si tu donnes une image 32/64 mais tu veux 16)
            if (decodeSize > 0)
                bmp.DecodePixelWidth = decodeSize;

            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }

    public static List<RibbonPanel> GetRibbonPanels()
    {
        return ribbonPanels;
    }

    public static IReadOnlyList<RibbonButtonInfo> GetRibbonButtonInfos()
    {
        return ribbonButtonOrder
            .Select(id => ribbonButtonRegistry.TryGetValue(id, out var info) ? info : null)
            .Where(info => info != null)
            .ToList();
    }

    public static RibbonButtonInfo GetRibbonButtonByCommandClass(string commandClass)
    {
        if (string.IsNullOrWhiteSpace(commandClass)) return null;
        ribbonButtonsByCommandClass.TryGetValue(commandClass, out var info);
        return info;
    }
    public static RibbonButtonInfo GetRibbonButtonById(string buttonId)
    {
        if (string.IsNullOrWhiteSpace(buttonId)) return null;
        ribbonButtonRegistry.TryGetValue(buttonId, out var info);
        return info;
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

    private static void RegisterButtonDefinition(string buttonId, string displayName, string commandClass, string imageResourceName)
    {
        if (string.IsNullOrWhiteSpace(buttonId)) return;

        if (!ribbonButtonRegistry.TryGetValue(buttonId, out var info))
        {
            info = new RibbonButtonInfo(buttonId, displayName, commandClass, imageResourceName);
            ribbonButtonRegistry[buttonId] = info;
            ribbonButtonOrder.Add(buttonId);
        }

        info.DisplayName = displayName;
        info.CommandClass = commandClass;
        info.ImageResourceName = imageResourceName;

        if (!string.IsNullOrWhiteSpace(commandClass) && !ribbonButtonsByCommandClass.ContainsKey(commandClass))
        {
            ribbonButtonsByCommandClass[commandClass] = info;
        }
    }

    private static void RegisterButtonCommandId(string buttonId, RevitCommandId commandId)
    {
        if (string.IsNullOrWhiteSpace(buttonId) || commandId == null) return;
        if (ribbonButtonRegistry.TryGetValue(buttonId, out var info))
        {
            info.CommandId = commandId;
        }
    }

    private static void RegisterButtonInstance(string buttonId, PushButton button)
    {
        if (string.IsNullOrWhiteSpace(buttonId) || button == null) return;
        if (ribbonButtonRegistry.TryGetValue(buttonId, out var info))
        {
            info.PushButton = button;
        }
    }

    public static RevitCommandId TryGetPushButtonCommandId(PushButton button)
    {
        return TryGetCommandId(button);
    }

    private static RevitCommandId TryGetCommandId(PushButton button)
    {
        if (button == null) return null;
        try
        {
            var props = button.GetType().GetProperties(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            foreach (var prop in props)
            {
                if (prop.PropertyType != typeof(RevitCommandId)) continue;
                return prop.GetValue(button) as RevitCommandId;
            }

            var fields = button.GetType().GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(RevitCommandId)) continue;
                return field.GetValue(button) as RevitCommandId;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

}