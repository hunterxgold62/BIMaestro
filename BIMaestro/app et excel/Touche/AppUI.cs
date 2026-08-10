using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMaestro.RibbonLayout;
using BIMaestro.Localization;
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
                new RibbonItemDefinition("HighlightElementsByCategories", "Sélection d'éléments", panel => AddPushButton(panel, "HighlightElementsByCategories", "Sélection\nd'éléments", assemblyPath, "Visualisation.HighlightElementsByCategoriesCommand", "Sélection d'éléments.png", "Met en évidence et filtre les éléments de catégories choisies.\r\nRegroupe automatiquement les éléments similaires pour accélérer la sélection et les actions répétitives.")),                
                new RibbonItemDefinition("OpenSheetFromViewButton", "Ouvrir la vue du Plan", panel => AddPushButton(panel, "OpenSheetFromViewButton", " Ouvrir \nla vue", assemblyPath, "Visualisation.OpenSheetFromView", "Ouvrir la vue.png", "Passe rapidement de la vue active à la feuille associée (et inversement).\r\nPermet aussi d'ouvrir une vue directement depuis un viewport sélectionné sur une feuille.")),
                 new RibbonItemDefinition("Export Nomenclature", "Export Nomenclature", panel => AddPushButton(panel, "Export Nomenclature", "Export de\nNomenclature", assemblyPath, "Visualisation.ExportScheduleCommand", "Export de Nomenclature.png", "Exporte les nomenclatures Revit sélectionnées en fichier Excel ou PDF.")),
                new RibbonItemDefinition("Sélection d'objet", "Sélection d'objet", panel => AddPushButton(panel, "Sélection d'objet", "Sélection\nd'objet", assemblyPath, "Visualisation.SelectSimilarCommand", "Sélection d'objet.png", "Sélectionne des éléments similaires dans le projet")),
                new RibbonItemDefinition("Boutons de Visualisation", "Boutons de Visualisation", panel => AddStackedPushButtons(
            panel,
            assemblyPath,
    
            ("ReorientViewButton", "Face 3D", "Visualisation.ReorientViewCommand", "Face 3D.png",
                "Permet de réorienter une vue 3D active en fonction de la géométrie d'une face sélectionnée."),
            ("ExportDwgBatch", "DWG Exp.", "Visualisation.ExportSheetsCommand", "DWG Exp..png",
                "Exporte automatiquement plusieurs vues ou feuilles en DWG, en nommant chaque fichier selon le projet et la vue comme pour les PDF."),
            ("GetPaintedMaterialsButton", "Peinture", "Visualisation.GetPaintedMaterialsCommand", "Peinture.png",
                "Liste les matériaux (y compris peinture) appliqués à un élément.")
    
        )),

            }),

            new RibbonPanelDefinition("Beta", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition(
                    "RevitGameCommand",
                    "Maquette MEP",
                    panel => AddPushButton(
                        panel,
                        "RevitGameCommand",
                        "Maquette\nMEP",
                       assemblyPath,
                        "BIMaestro.VideoGames.RevitGameCommand",
                       "3D.jpg",
                        "Transforme en un clic la vue 3D Revit active en maquette BIM interactive.\r\n\r\n" +
                        "Fonctionnalités :\r\n" +
                        "- conserve les éléments visibles, la boîte de coupe et les couleurs de la vue,\r\n" +
                        "- déplacement à la première personne en ZQSD/WASD,\r\n" +
                        "- collisions avec les murs et équipements,\r\n" +
                        "- ouverture et fermeture des portes proches avec E,\r\n" +
                        "- informations Revit par clic gauche court sur un objet,\r\n" +
                        "- ouverture ou fermeture contextuelle des vannes par clic droit,\r\n" +
                        "- accroupissement avec Ctrl pour inspecter les zones basses,\r\n" +
                        "- montée automatique des escaliers et rampes,\r\n" +
                        "- saut, course, gravité et mode vol libre.\r\n\r\n" +
                        "Conseil : préparez une vue 3D en Couleurs uniformes, puis cliquez sur ce bouton."))
            }),

             new RibbonPanelDefinition("Modification", new List<RibbonItemDefinition>
            {
                
                // new RibbonItemDefinition("ResérvationAuto2", "Auto Réservation2", panel => AddPushButton(panel, "ResérvationAuto2", "Auto\nRéservation2", assemblyPath, "Modification.ReservationAutoMultiVoidCommandV2", "safeimagekit-Réservation.png", "Crée des réservations automatiques")),
                new RibbonItemDefinition("ResérvationAuto", "Auto Réservation", panel => AddPushButton(panel, "ResérvationAuto", " Auto \nRéservation", assemblyPath, "Modification.ReservationAutoV3Command", "Auto réservation.png", "Crée des réservations automatiques")),
                //new RibbonItemDefinition("ResérvationAuto", "Auto Réservation", panel => AddPushButton(panel, "ResérvationAuto", "Auto\nRéservation", assemblyPath, "Modification.ReservationAutoMultiCommand", "safeimagekit-Réservation.png", "Crée des réservations automatiques")),
                new RibbonItemDefinition("Bride auto", "Bride auto", panel => AddSplitButton(panel, "Bride auto", "Bride\nauto", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Bride auto", "Bride\nauto", "Modification.AddFlangesAtEnds", "Bride auto (2).png","Ajoute automatiquement des brides aux extrémités sélectionnées"),
                    ("Choix bride", "Choix\nbride", "Modification.PickDefaultFlange", "reset.png","Permet de choisir la bride par défaut"),
                    ("Suppression de brides", "Suppression\nde brides", "Modification.RemoveFlangesReconnect", "Suppression de brides.png","Permet de supprimer les brides")
                })),
                new RibbonItemDefinition("Dynamo auto", "Dynamo Auto", panel => AddSplitButton(panel, "Dynamo auto", "Dynamo\nAuto", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("dynamo 1", DynamoSettings.GetLabel(0), "Modification.RunDynamo1Command", "Dynamo 1 (2).png","Lance le script Dynamo n°1."),
                    ("dynamo 2", DynamoSettings.GetLabel(1), "Modification.RunDynamo2Command", "Dynamo 2 (2).png","Lance le script Dynamo n°2."),
                    ("dynamo 3", DynamoSettings.GetLabel(2), "Modification.RunDynamo3Command", "Dynamo 3 (2).png","Lance le script Dynamo n°3."),
                    ("dynamo 4", DynamoSettings.GetLabel(3), "Modification.RunDynamo4Command", "Dynamo 4 (2).png","Lance le script Dynamo n°4."),
                    ("dynamo 5", DynamoSettings.GetLabel(4), "Modification.RunDynamo5Command", "Dynamo 5 (2).png","Lance le script Dynamo n°5."),
                    ("dynamo réglage", "Auto dynamo\nréglage", "Modification.ConfigureDynamoButtonCommand", "paramétre.png","Configure les paramètres Dynamo"),
                })),
                new RibbonItemDefinition("GestionExcelCmd", "Gestion Excel", panel => AddPushButton(panel, "GestionExcelCmd", "Gestion\nExcel", assemblyPath, "ScheduleIO.ScheduleExcelIOCommand", "Gestion Excel.png", "Exporter ou importer une nomenclature au format Excel")),
                new RibbonItemDefinition("PhaseQuickEditButton", "Phases rapides", panel => AddPushButton(panel, "PhaseQuickEditButton", "Phases\nrapides", assemblyPath, "Modification.PhaseQuickEditCommand", "paramétre.png", "Modifie rapidement la phase de creation et la phase de demolition des objets selectionnes.")),
                //new RibbonItemDefinition("SafeMoveButton", "Déplacement protégé", panel => AddPushButton(panel, "SafeMoveButton", "Déplacement\nprotégé", assemblyPath, "Modification.SafeMoveCommand", "Déplacement protégé.png", "Déplace précisément les objets entre deux points sans les dissocier ni les recréer.\r\nAnnule intégralement l'opération si Revit détecte une contrainte ou un risque pour une étiquette ou une cotation.")),
                new RibbonItemDefinition("ModificationQuickTools", "Outils rapides", panel => AddStackedPushButtons(
                        panel,
                        assemblyPath,
                        ("Surcharges vues", "Surcharges", "Modification.OverrideColorCommand", "Couleur.png", "Applique ou réinitialise rapidement la demi-teinte, la transparence ou le masquage des éléments sélectionnés dans les vues choisies. Si une feuille est sélectionnée, BIMaestro applique l’action aux vues placées sur cette feuille."),
                        ("ElementRenamerButton", "Organisateur", "Modification.RenameElementsCommand", "Organisateur.png", "Renomme les éléments sélectionnés avec préfixes, suffixes et numérotation.\r\nSur une feuille, numérote les fenêtres de vue de haut en bas puis de gauche à droite.\r\nTrie aussi les éléments par niveau/emplacement et peut réinitialiser le paramètre texte ciblé."),
                        ("Purge du plan", "Purge", "Modification.CombinedCleanupCommand", "Purge.png", "Supprime les vues non placées, les familles et les nomenclatures inutilisées afin d'alléger le projet.\r\nUne fenêtre permet de choisir précisément les éléments à purger avant exécution.\r\n")
                    )),
             }),

            new RibbonPanelDefinition("Outils IA", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("GPTBotWindowButton", "Chatbot + élément", panel => AddPushButton(panel, "GPTBotWindowButton", "Chatbot\n+ élément", assemblyPath, "IA.GPTBotWindowCommand", "Chatbot + élémet.png", "Ouvre un assistant IA conversationnel connecté à votre contexte Revit.\r\nAnalyse les éléments sélectionnés et répond selon le profil choisi (Basique, Revit, BIM Manager).")),
                new RibbonItemDefinition("TextCorrectionButton", "Correction de texte IA", panel => AddPushButton(panel, "TextCorrectionButton", "Correction \nde texte IA", assemblyPath, "IA.TextCorrectionCommand", "Correction de texte IA (2).png", "Corrige et reformule les textes Revit sélectionnés avec l'IA.\r\nPropose plusieurs styles et laisse valider, modifier ou ignorer chaque suggestion.")),
                new RibbonItemDefinition("ScanText", "Audit texte IA", panel => AddPushButton(panel, "ScanText", "Audit texte\nIA", assemblyPath, "ScanTextRevit.SelectViewsCommand", "Audit texte IA.png", "Analyse les textes des vues/feuilles sélectionnées pour détecter les fautes d'orthographe, de grammaire et de ponctuation. \r\n\r\nPourquoi ce bouton est utile :\r\n- évite les oublis avant envoi client,\r\n- classe les anomalies par gravité (Mineur / Erreur),\r\n- propose des corrections détaillées ligne par ligne.\r\n\r\nConseil : sélectionne seulement les vues/feuilles à contrôler pour accélérer l'analyse.")),
                new RibbonItemDefinition("RealisticViewImage", "Rendu plan IA", panel => AddPushButton(panel, "RealisticViewImage", "Rendu\nplan IA", assemblyPath, "IA.RealisticViewImageCommand", "rendu plan IA.png", "Génère un rendu réaliste à partir d'une vue Plan/Coupe/3D via gpt-image-2.\r\n\r\nCe que fait le bouton :\r\n- conserve le cadrage et la géométrie de la vue source,\r\n- optimise l'image avant envoi ,\r\n- crée une variante visuelle rapide pour présentation client.\r\n\r\nConseil : lancez-le sur une vue propre (annotations masquées) pour obtenir un résultat plus lisible."))            }),
           new RibbonPanelDefinition("Analyse", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("PipeLengthByDiameterV2", "Calcul des canalisations", panel => AddPushButton(panel, "PipeLengthByDiameterV2", "Calcul des\ncanalisations", assemblyPath, "Analyse.PipeLengthByDiameterCommandV2", "Calcul de canalisation.png", "Description :\r\n- Calcule les longueurs des canalisations et gaines par diamètre (DN ou dimensions).\r\n- Compte les accessoires de type coudes et tés par diamètre.\r\n- Estime les volumes d'eau par diamètre intérieur.\r\n- Intègre un filtre par type de système pour une analyse précise.\r\n- Permet d'inclure ou non les gaines dans les calculs.\r\n- Exporte les résultats sous forme de tableau Excel détaillé.\r\n\r\nUtilité :\r\nOptimisez votre gestion des systèmes MEP en obtenant rapidement une analyse précise des longueurs, volumes et accessoires, avec possibilité d'exportation.")),
                new RibbonItemDefinition("Qui a fait ça ?", "Qui a fait ça ??", panel => AddPushButton(panel, "Qui a fait ça ?", "Qui a\nfait ça ??", assemblyPath, "Analyse.MainCommand", "qui à fait ça (2).png", "BETA - Qui a fait ça ? Version historique visuel.\r\n\r\nDans l'esprit du bouton original, cet outil aide à répondre à des questions simples :\r\n- qui a créé ou modifié un élément ?\r\n- qui a créé ou modifié la vue active ?\r\n- qu'est-ce qui a été supprimé, déplacé ou ajouté dans la maquette ?\r\n\r\nCe que fait cette version bêta :\r\n- affiche les suppressions, créations, déplacements, changements de type et modifications de paramètres enregistrés,\r\n- regroupe automatiquement les actions répétées en clusters lisibles,\r\n- permet de filtrer par action, utilisateur ou recherche,\r\n- permet de focaliser un élément, un cluster ou un élément précis dans un cluster,\r\n- visualise les suppressions et déplacements via des aperçus temporaires dans la maquette.\r\n\r\nImportant : l'historique utilise le même dossier actif que le suivi maquette. Si le dossier partagé n'est pas disponible, les journaux peuvent être stockés localement.")),
                new RibbonItemDefinition("AnalysePoidsButton", "Analyse de Poids", panel => AddPushButton(panel, "AnalysePoidsButton", "Analyse\nde Poids", assemblyPath, "Analyse.CommandAnalysePoids", "Analyse de poid.png", "Fonctionnalités principales :\r\n1. **Analyse des Familles** :\r\n   - Taille de chaque famille (Mo).\r\n   - Nombre d'instances pour chaque famille.\r\n   - Classement par taille décroissante.\r\n\r\n2. **Analyse des Imports CAO** :\r\n   - Taille des imports (Mo).\r\n   - Types d'éléments analysés : Imports CAO, Lien Revit/IFC.\r\n\r\n3. **Export des Résultats** :\r\n   - Export vers un fichier Excel (RevitLogs/TailleFamilleRevit).\r\n   - Organisation claire par nom, type, taille et nombre d'instances.\r\n\r\nUtilité :\r\n- Identifier les éléments volumineux dans votre projet.\r\n- Optimiser la performance du modèle en réduisant les familles et les imports inutiles.")),

                new RibbonItemDefinition("Temps par projet", "Temps par projet", panel => AddSplitButton(panel, "Temps par projet", "Temps par\nprojet", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Temps par projet", "Temps par\nprojet", "BIMaestro.Dashboard.ShowTimeDashboard", "Temps par projet.png", "Affiche le temps passé par projet."),
                   ("Suivi maquette collaboratif", "Suivi\nmaquette", "Analyse.CollaborativeModelTrackerCommand", "suivie maquette.png", "BETA - Suivi collaboratif des maquettes.\r\n\r\nCe que fait le bouton :\r\n- crée et consulte un registre JSON + Excel des maquettes ouvertes,\r\n- journalise automatiquement les ouvertures/fermetures quand BIMaestro est chargé,\r\n- identifie le premier ouvreur comme créateur de référence si la maquette n'est pas encore connue,\r\n- affiche le dossier de stockage actif et permet de choisir un dossier commun serveur si le chemin partagé est indisponible.\r\n\r\nImportant : si aucun dossier commun n'est configuré et que le lecteur partagé est inaccessible, les données sont enregistrées en local dans Documents/RevitLogs/SuiviMaquettesCollaboratif.")
                })),

                new RibbonItemDefinition("Clash 3D", "Clash 3D", panel => AddPushButton(panel, "Clash 3D", "Clash\n3D", assemblyPath, "Analyse.SmartClashCommand", "Clash 3D.png", "Vérifie les éléments 3D sélectionnés pour détecter les incohérences."))
            }),
            
                new RibbonPanelDefinition("Spécifique aux familles", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("FamilyBrowser", "Navigateur de Familles", panel => AddSplitButton(panel, "FamilyBrowser", "Famille", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("FamilyBrowser", "Navigateur\nde Familles", "Famille.FamilyBrowserCommand", "Famille.png","Parcourt vos dossiers de familles Revit et charge les contenus en quelques clics.\r\nInclut aperçu visuel, favoris, recherche et options d'affichage pour accélérer le travail."), 
                    ("Rosace", ".","BIMaestro.UI.RadialMenuCommand", "vide.png","Rosace des familles à ajouter en raccourci clavier voir raccourci souris.")
                }, "Navigateur de Familles", "maison famille (1).png", keepDefaultCurrentButton: true, fixedDisplayText: "Famille")),
        new RibbonItemDefinition("ConvertSharedToFamily", "Convertir les paramètres partagés", panel => AddPushButton(panel, "ConvertSharedToFamily", "Convertir\nparamètres", assemblyPath, "Famille.ConvertSharedToFamilyParametersCommand", "Convertir paramètres (2).png", "Convertit tous les paramètres partagés modifiables de la famille en paramètres de famille (même nom, même groupe et même type instance/type).")),                new RibbonItemDefinition("FamilyUtilitiesStack", "Outils familles", panel => AddStackedFamilyUtilities(
                    panel,
                    assemblyPath,
                    ("PurgeFamilyParameters", "Purge", "Famille.PurgeFamilyParametersCommand", "Purge famille.png", "Supprime les paramètres inutilisés d'une famille Revit après vérification des dépendances.\r\nCrée automatiquement une sauvegarde avant nettoyage."),
                    ("Familytraduction", "Trad.IA", new List<(string, string, string, string, string)>
                    {
                        ("Familytraduction", "Trad.IA", "Famille.TraduireParametresFamilleOpenAI", "Tard IA.png","Traduit en français les noms de paramètres utilisateur d'une famille.\r\nIgnore les paramètres déjà francisés et enregistre les changements de façon sécurisée."),
                        ("FamilyViewtraduction", "Traduction\nde vues IA","Famille.TraduireVuesFamilleOpenAI", "Tard IA.png","Cette commande permet :  \r\n- De traduire automatiquement les noms des vues d'une famille Revit en français.  \r\n- De conserver les vues déjà en français sans modification.  \r\n- D'assurer l'unicité des noms générés même en cas de doublon potentiel.  \r\n- D'utiliser l'API OpenAI et le cache de traduction pour accélérer les traitements récurrents.  \r\n\r\nUtilité :  \r\nGarantit une nomenclature cohérente et francisée des vues de famille, améliorant la compréhension et la conformité des contenus.  ")
                    }),
                    ("Export d'unité", "Unités", new List<(string, string, string, string, string)>
                    {
                        ("Export d'unité", "Unités", "Famille.ExportProjectUnitsCommand", "Unités.png","Sauvegarde dans un fichier JSON les unités et leur précision (longueur, surface, volume, angle, etc.) du projet en cours, dans le dossier Mes Documents/RevitLogs/SauvegardePréférence."),
                        ("Import d'unité", "Import\nd'unité","Famille.ImportProjectUnitsCommand", "Import unités.png","Recharge depuis le fichier JSON les unités et leur précision pour appliquer rapidement vos préférences au projet.")
                    })))
            }),

           new RibbonPanelDefinition("Couleur et information", new List<RibbonItemDefinition>
            {
                new RibbonItemDefinition("Changement de couleur", "Changement de couleur", panel => AddSplitButton(panel, "Changement de couleur", "couleur\nOui/Non", assemblyPath, new List<(string, string, string, string, string)>
                {
                    ("Couleur de projet", "Couleur\nOui/Non", "Couleur.ToggleCombinedColoringCommand", "Couleur oui non.png","Active ou désactive les couleurs du projet (simple ou double clic)"),
                    ("Couleur de maquette", "Couleur reset", "Couleur.ResetTabItemRandomColorsCommand", "reset.png","Réinitialise les couleurs appliquées"),
                    ("papa Noël", "papa\nNoël", "Couleur.PapanoelCommand", "papa noel.png","Fait apparaître des couleurs comme des guirlandes\nDouble clic pour revenir à la normale.\n\nAttention désactiver <couleur Oui/Non> avant activation."),
                    ("Personnaliser les couleurs", "Palette", "Couleur.CustomizeRibbonColorsCommand", "Couleur.png","Choisit une couleur unie, un dégradé ou un thème prédéfini, ainsi que la couleur du texte de chaque panneau du ruban BIMaestro."),
                    ("BIMaestro_Exemple", "Exemple", "Page.GuideCommand", "Exemple.png", "Page d'information sur le plugin"),
                })),

                new RibbonItemDefinition("InfoStack", "Infos empilées", panel => AddStackedInfoButtons(
                    panel,
                    assemblyPath,
                    // (name, text, className, icon, tooltip)
                    ("NOTE_MAJ", "Note", "Page.MiseAJourCommand", "Information (2).png", "Page de mise à jour"),
                    ("JeuxSplit", "Snake", new List<(string, string, string, string, string)>
                    {
                        ("Snake", "Snake", "BIMaestro.Bonus.SnakeCommand", "snake.png", "Petit jeu Snake :P"),
                        ("FlappyBird", "Flappy\nBird", "BIMaestro.Bonus.FlappyBirdCommand", "flappy bird.png", "Petit jeu Flappy Bird :P")
                    }),
                    ("CustomizeRibbon", "Option", new List<(string, string, string, string, string)>
                    {
                        ("CustomizeRibbon", "Option", "BIMaestro.RibbonLayout.RibbonLayoutCommand", "Option.png", "Configurer le ruban BIMaestro et les paramètres utilisateur."),
                        ("ContactCommand", "Contact", "Page.ContactCommand", "Information (2).png", "Ouvre le LinkedIn de Paul Lemert pour envoyer un retour, signaler un bouton qui bloque ou proposer une idée."),
                        ("RadialMenuButtonsCommand", "Rosace\nBoutons", "BIMaestro.UI.RadialMenuButtonsCommand", "Option.png", "Rosace des 16 derniers boutons BIMaestro utilisés.")
                    })
                )),

                new RibbonItemDefinition(
                    "SupportCommand",
                    "Soutenir",
                    panel => AddPushButton(
                        panel,
                        "SupportCommand",
                        "Soutenir",
                        assemblyPath,
                        "Page.SupportCommand",
                        RibbonIconAssets.SupportHeartFileName,
                        "Vous appréciez BIMaestro ? Ouvrez la page Ko-fi pour soutenir volontairement son développement autour d’un petit café."))
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
            var panel = application.CreateRibbonPanel(tabName, UiLanguage.T(panelConfig.Name));
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
         (string splitButtonName, string splitButtonText, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons) gamesSplit,
         (string splitButtonName, string splitButtonText, List<(string buttonName, string buttonText, string className, string resourceImageName, string toolTip)> buttons) optionSplit)
    {
        RegisterButtonDefinition(noteButton.buttonName, noteButton.buttonText, noteButton.className, noteButton.resourceImageName);

        var noteData = CreatePushButtonData(noteButton.buttonName, noteButton.buttonText, assemblyPath, noteButton.className, noteButton.resourceImageName, noteButton.toolTip);
        var gamesData = new SplitButtonData(gamesSplit.splitButtonName, UiLanguage.T(gamesSplit.splitButtonText));
        var optionData = new SplitButtonData(optionSplit.splitButtonName, UiLanguage.T(optionSplit.splitButtonText));

        var stacked = panel.AddStackedItems(noteData, gamesData, optionData);
        if (stacked == null)
        {
            return;
        }

        if (stacked.Count > 0 && stacked[0] is PushButton note)
        {
            RegisterButtonInstance(noteButton.buttonName, note);
            RegisterButtonCommandId(noteButton.buttonName, TryGetCommandId(note));
        }

        if (stacked.Count > 1 && stacked[1] is SplitButton games)
        {
            ConfigureSplitButton(games, assemblyPath, gamesSplit.buttons, "Snake", "snake.png");
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
        var traductionData = new SplitButtonData(traductionSplit.splitButtonName, UiLanguage.T(traductionSplit.splitButtonText));
        var unitsData = new SplitButtonData(unitsSplit.splitButtonName, UiLanguage.T(unitsSplit.splitButtonText));

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
        buttonText = UiLanguage.T(buttonText);
        toolTip = UiLanguage.T(toolTip);
        PushButtonData buttonData = new PushButtonData(buttonName, buttonText, assemblyPath, className);

        // ToolTip / LongDescription (comme tu faisais)
        string tt = toolTip ?? "";
        string[] parts = tt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        string shortTip = parts.Length > 0 ? parts[0] : "";
        if (string.IsNullOrWhiteSpace(shortTip))
            shortTip = UiLanguage.T($"Exécuter {buttonText}", $"Run {buttonText}");
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
        var splitButtonData = new SplitButtonData(splitButtonName, UiLanguage.T(splitButtonText));
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
            splitButton.ToolTip = UiLanguage.T(splitToolTip);

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

        TrySetRibbonItemText(splitButton, UiLanguage.T(fixedDisplayText));

        try
        {
            var eventInfo = splitButton.GetType().GetEvent("CurrentButtonChanged");
            if (eventInfo != null)
            {
                EventHandler handler = (_, __) => TrySetRibbonItemText(splitButton, UiLanguage.T(fixedDisplayText));
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
        if (string.Equals(resourceFileName, RibbonIconAssets.SupportHeartFileName, StringComparison.OrdinalIgnoreCase))
        {
            return RibbonIconAssets.LoadSupportHeart(decodeSize);
        }

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

internal static class RibbonIconAssets
{
    internal const string SupportHeartFileName = "SupportHeart.png";

    private const string SupportHeartPngBase64 =
        "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";

    internal static byte[] GetSupportHeartPngBytes()
    {
        return Convert.FromBase64String(SupportHeartPngBase64);
    }

    internal static BitmapImage LoadSupportHeart(int decodeSize)
    {
        using (var stream = new MemoryStream(GetSupportHeartPngBytes()))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (decodeSize > 0)
                bitmap.DecodePixelWidth = decodeSize;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
