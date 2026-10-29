using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;
using BIMaestro.Localization;

namespace BIMaestro.Tutorials
{
    // One attempt survives window/ribbon hand-offs; explicit exits end it.
    internal static class TutorialAnalytics
    {
        private sealed class Attempt
        {
            internal string Id = Guid.NewGuid().ToString("D");
            internal int Step;
            internal string Title;
            internal bool Ended;
        }
        private static readonly Dictionary<string, Attempt> Attempts = new Dictionary<string, Attempt>();
        internal static readonly string[] Catalog = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };

        internal static void Ensure(string id)
        {
            if (!Catalog.Contains(id)) return;
            if (Attempts.TryGetValue(id, out Attempt existing) && !existing.Ended) return;
            var attempt = new Attempt();
            Attempts[id] = attempt;
            Emit(id, attempt, "started");
        }
        internal static void Step(string id, int index, string title)
        {
            Ensure(id);
            if (!Attempts.TryGetValue(id, out Attempt attempt)) return;
            attempt.Step = index + 1;
            attempt.Title = title;
            Emit(id, attempt, "step_viewed");
        }
        internal static void End(string id, string outcome)
        {
            if (!Attempts.TryGetValue(id, out Attempt attempt) || attempt.Ended) return;
            Emit(id, attempt, outcome);
            attempt.Ended = true;
        }
        internal static void Complete(string id) => End(id, "completed");
        internal static void Shutdown()
        {
            foreach (string id in Attempts.Keys.ToArray()) End(id, "interrupted");
        }
        private static void Emit(string id, Attempt attempt, string action)
        {
            Telemetry.TrackButton("Tutorial.Event", true, new {
                schema_version = 1, catalog_version = "2", tutorial_id = id,
                attempt_id = attempt.Id, event_id = Guid.NewGuid().ToString("D"),
                action, step_index = attempt.Step, step_title = attempt.Title
            });
        }
    }

    internal static class DemoTourPalette
    {
        internal static readonly Brush Accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 81, 50));
        internal static readonly Brush Highlight = new SolidColorBrush(System.Windows.Media.Color.FromRgb(236, 253, 245));
    }

    internal sealed class DemoStep
    {
        internal readonly string Title, Text, Target;
        internal readonly bool IsExplanation;
        internal readonly string CompletionEvent;
        internal DemoStep(string title, string text, string target, bool isExplanation = false,
            string completionEvent = null)
        { Title = title; Text = text; Target = target; IsExplanation = isExplanation;
            CompletionEvent = completionEvent; }
    }

    // New tours only add metadata and a window hook. The controls keep their native handlers.
    internal static class DemoTourCatalog
    {
        internal static IReadOnlyDictionary<string, string> EstimatedDurations => new Dictionary<string, string>
        {
            ["reservation"] = UiLanguage.T("4 à 6 min", "4 to 6 min"),
            ["history"] = UiLanguage.T("6 à 10 min", "6 to 10 min"),
            ["colors"] = UiLanguage.T("8 à 12 min", "8 to 12 min"),
            ["pipe-calculation"] = UiLanguage.T("2 à 4 min", "2 to 4 min"),
            ["organizer"] = UiLanguage.T("5 à 8 min", "5 to 8 min"),
            ["view-template"] = UiLanguage.T("3 à 5 min", "3 to 5 min"),
            ["excel"] = UiLanguage.T("5 à 8 min", "5 to 8 min"),
            ["family-browser"] = UiLanguage.T("12 à 20 min", "12 to 20 min"),
            ["mep-booster"] = UiLanguage.T("3 à 5 min", "3 to 5 min"),
            ["clash-3d"] = UiLanguage.T("6 à 10 min", "6 to 10 min")
        };

        internal static readonly IReadOnlyDictionary<string, string> Views = new Dictionary<string, string>
        {
            ["reservation"] = "BIMaestro - 01 Auto réservation",
            ["history"] = "BIMaestro - 02 Qui a fait ça",
            ["colors"] = "BIMaestro - 03 Couleurs et vues",
            ["pipe-calculation"] = "BIMaestro - 04 Calcul des canalisations",
            ["organizer"] = "BIMaestro - 05 Organisateur",
            ["view-template"] = "BIMaestro - 06 Gabarit source",
            ["excel"] = "BIMaestro - 08 Gestion Excel",
            ["clash-3d"] = DemoClashExercise.ViewName
        };

        internal static IReadOnlyDictionary<string, DemoStep[]> Steps => new Dictionary<string, DemoStep[]>
        {
            ["clash-3d"] = new[]
            {
                new DemoStep(UiLanguage.T("Trois tuyaux, deux conflits", "Three pipes, two clashes"), UiLanguage.T("La scène contient un tuyau traversant un mur et deux tuyaux qui se croisent sans raccord. Les trois tuyaux sont sélectionnés. Le périmètre Sélection limite les objets de départ ; le mur voisin reste recherché comme obstacle.", "The scene contains a pipe passing through a wall and two pipes that cross without a fitting. All three pipes are selected. The Selection scope limits the source elements; the nearby wall is still checked as an obstacle."), "ScopeCombo", true),
                new DemoStep(UiLanguage.T("Choisir les contrôles", "Choose the checks"), UiLanguage.T("Tuyaux et Collisions dans la maquette sont préparés pour cet essai. Les connecteurs ouverts sont désactivés : les extrémités libres sont volontaires. Dans ton projet, Collisions avec les liens compare aussi les liens chargés et cochés ; cet exercice ne contient pas de lien.", "Pipes and Clashes in the model are set up for this exercise. Open connectors are disabled: the free ends are intentional. In your own project, Clashes with links also compares loaded and checked links; this exercise contains no link."), "LocalCheck", true),
                new DemoStep(UiLanguage.T("Un seuil de volume", "A volume threshold"), UiLanguage.T("Garde 10 mm³. Ce seuil ignore les très petites intersections de solides : ce n'est ni une distance de sécurité ni un jeu autour du tuyau. Une simple touche sans volume n'est pas une collision confirmée. Une suspicion de maillage exige une vérification visuelle.", "Keep 10 mm³. This threshold ignores very small solid intersections: it is neither a safety distance nor a clearance around the pipe. A simple touch with no volume is not a confirmed clash. A suspected mesh clash requires a visual check."), "VolumeBox", true),
                new DemoStep(UiLanguage.T("Vérifier les deux intersections", "Check both intersections"), UiLanguage.T("Clique sur Analyser et attends la fin. Bulbizarre attend les deux intersections confirmées de cette scène, pas seulement un clic. Si tu changes les options, rétablis Sélection, Tuyaux, Collisions dans la maquette et 10 mm³.", "Click Analyze and wait for it to finish. Bulbasaur is waiting for the two confirmed intersections in this scene, not just a click. If you change the options, restore Selection, Pipes, Clashes in the model and 10 mm³."), "AnalyzeButton", completionEvent: "clash-scan-verified"),
                new DemoStep(UiLanguage.T("Comprendre le détail", "Understand the details"), UiLanguage.T("Le conflit entre les deux tuyaux est présélectionné. Clique sur Détail sélection pour examiner les deux objets et tourner l'aperçu avec la souris. Ferme le détail, puis clique sur Suivant. « Traité » enregistre une décision humaine : il ne déplace aucun objet et ne prouve pas que le conflit est résolu.", "The clash between the two pipes is preselected. Click Selection details to examine both elements and rotate the preview with the mouse. Close the details, then click Next. “Resolved” records a human decision: it does not move any element and does not prove the clash is fixed."), "TutorialInspectButton", completionEvent: "clash-detail-opened"),
                new DemoStep(UiLanguage.T("Observer dans Revit", "Look at it in Revit"), UiLanguage.T("Clique sur Voir sélection en 3D. La fenêtre s'effacera après le cadrage pour te laisser observer le croisement : objet contrôlé orange, obstacle bleu. Confirme ensuite l'observation dans la petite carte pour revenir au guide.", "Click View selection in 3D. The window will step aside after zooming so you can look at the crossing: checked element in orange, obstacle in blue. Then confirm in the small card to return to the guide."), "TutorialFocusButton", completionEvent: "clash-focus-observed"),
                new DemoStep(UiLanguage.T("Corriger le tuyau d'essai", "Fix the test pipe"), UiLanguage.T("Clique sur Relever le tuyau d'essai : seul le tuyau transversal de cette scène sera déplacé de 300 mm vers le haut. Ce bouton sert à la formation. Dans ton projet, corrige le réseau avec les outils Revit en tenant compte de la pente et des raccordements.", "Click Raise the test pipe: only the crossing pipe in this scene will be moved 300 mm up. This button is for training only. In your own project, fix the system with Revit tools, taking slope and connections into account."), "TutorialFixButton", completionEvent: "clash-corrected"),
                new DemoStep(UiLanguage.T("Prouver la disparition du conflit", "Prove the clash is gone"), UiLanguage.T("Relance Analyser avec les mêmes réglages et attends. Le conflit entre tuyaux doit disparaître ; la traversée du mur doit rester. Une traversée n'indique pas à elle seule qu'une réservation manque ou est incorrecte : examine la paroi et l'ouverture réelle avant de décider.", "Run Analyze again with the same settings and wait. The pipe-to-pipe clash should disappear; the wall penetration should remain. A penetration alone does not mean an opening is missing or wrong: check the wall and the actual opening before deciding."), "AnalyzeButton", completionEvent: "clash-rescan-verified"),
                new DemoStep(UiLanguage.T("Observer la traversée du mur", "Look at the wall penetration"), UiLanguage.T("La traversée restante est présélectionnée. Clique sur Voir sélection en 3D, observe le tuyau et le mur, puis confirme dans la petite carte. Nous allons créer une réservation autour de ce tuyau.", "The remaining penetration is preselected. Click View selection in 3D, look at the pipe and the wall, then confirm in the small card. We will now create an opening around this pipe."), "TutorialFocusButton", completionEvent: "clash-wall-observed"),
                new DemoStep(UiLanguage.T("Créer la réservation", "Create the opening"), UiLanguage.T("Clique sur Créer la réservation. Clash 3D place réellement la famille rectangulaire murale fournie autour du tuyau ; Bulbizarre attend sa création. Cet essai utilise des réglages temporaires. Dans ton projet, le bouton reprend la famille et les réglages d’Auto réservation.", "Click Create opening. Clash 3D actually places the supplied rectangular wall family around the pipe; Bulbasaur waits for it to be created. This exercise uses temporary settings. In your own project, the button uses the family and settings from Auto Opening."), "ReservationButton", completionEvent: "clash-reservation-created"),
                new DemoStep(UiLanguage.T("Contrôler après la réservation", "Check after the opening"), UiLanguage.T("Relance Analyser et attends la fin. Contrôle ensuite la position, les dimensions et l’ouverture réelle dans Revit. Une réservation créée ne garantit pas que la famille découpe le mur : si la traversée reste signalée, examine ce résultat avant de la déclarer traitée.", "Run Analyze again and wait for it to finish. Then check the position, dimensions and actual opening in Revit. A created opening does not guarantee that the family cuts the wall: if the penetration is still reported, examine this result before marking it as resolved."), "AnalyzeButton", completionEvent: "clash-reservation-verified"),
                new DemoStep(UiLanguage.T("Exporter le bilan avant / après", "Export the before / after report"), UiLanguage.T("Garde Tous dans les statuts et aucune recherche. Clique sur Exporter et choisis HTML pour voir les deux intersections de départ avec leurs aperçus, les actions réalisées et le bilan final. Le CSV distingue aussi Avant et Après correction. Même si aucun conflit ne reste, ton rapport conserve l’exercice. Bulbizarre attend l’enregistrement du fichier.", "Keep All in the statuses and no search. Click Export and choose HTML to see the two initial intersections with their previews, the actions taken and the final summary. The CSV also separates Before and After correction. Even if no clash remains, your report keeps the exercise. Bulbasaur waits for the file to be saved."), "ExportButton", completionEvent: "clash-export-written")
            },
            ["reservation"] = new[]
            {
                new DemoStep(UiLanguage.T("Le support", "The host"), UiLanguage.T("Choisis « Mur » : la réservation sera placée dans le mur traversé. « Sol » correspond à une autre configuration de famille et de dimensions.", "Choose “Wall”: the opening will be placed in the wall being crossed. “Floor” uses a different family and dimension setup."), "hostMurCard"),
                new DemoStep(UiLanguage.T("La forme", "The shape"), UiLanguage.T("Choisis « Rectangulaire ». Cette forme détermine les paramètres de dimensions à renseigner dans la famille : longueur, hauteur et profondeur pour un mur.", "Choose “Rectangular”. The shape determines which dimension parameters the family needs: length, height and depth for a wall."), "shapeRectCard"),
                new DemoStep(UiLanguage.T("Le réseau", "The system"), UiLanguage.T("Choisis « Canalisation ». BIMaestro calcule l'ouverture autour du réseau sélectionné ; une gaine ou un autre réseau exige le cas adapté.", "Choose “Pipe”. BIMaestro calculates the opening around the selected system; a duct or another system requires the matching case."), "objPipeCard"),
                new DemoStep(UiLanguage.T("Manuel ou automatique ?", "Manual or automatic?"), UiLanguage.T("Le mode manuel te fait choisir le réseau et le mur : idéal pour comprendre et contrôler le résultat. Le mode automatique scanne les croisements, uniquement pour les murs. Laisse-le désactivé pour cet exercice.", "Manual mode lets you pick the system and the wall: ideal for understanding and checking the result. Automatic mode scans for crossings, for walls only. Leave it disabled for this exercise."), "chkAutomatique", true),
                new DemoStep(UiLanguage.T("Source des éléments", "Element source"), UiLanguage.T("« Maquette » travaille sur les éléments du projet ouvert. Les options de liens servent lorsque le réseau ou le mur vient d'une maquette liée IFC/RVT ; « Double lien » restreint les deux choix aux liens.", "“Model” works on elements of the open project. The link options are used when the system or the wall comes from a linked IFC/RVT model; “Double link” limits both picks to links."), "chkDoubleLink", true),
                new DemoStep(UiLanguage.T("Configurer sa famille", "Set up your family"), UiLanguage.T("L'onglet Familles permet d'utiliser ta propre famille RFA. Nous allons parcourir les réglages sans modifier la famille déjà prête dans cette maquette.", "The Families tab lets you use your own RFA family. We will go through the settings without changing the family already set up in this model."), "ReservationFamiliesTab", true),
                new DemoStep(UiLanguage.T("Choisir le bon cas", "Choose the right case"), UiLanguage.T("Dans Familles, choisis Support, Forme et Hébergement. Chaque combinaison a son propre type de réservation. Le type choisi ici doit correspondre à la façon dont ta famille est construite et hébergée.", "In Families, choose Host, Shape and Hosting. Each combination has its own opening type. The type chosen here must match how your family is built and hosted."), "cbConfigSupport", true),
                new DemoStep(UiLanguage.T("Famille déjà chargée", "Family already loaded"), UiLanguage.T("Si ton type est déjà dans le projet, sélectionne-le ici. Les Modèles génériques compatibles apparaissent en premier. Vérifie le type avant d'enregistrer le mapping.", "If your type is already in the project, select it here. Compatible Generic Models appear first. Check the type before saving the mapping."), "cbLoadedType", true),
                new DemoStep(UiLanguage.T("Importer un RFA", "Import an RFA"), UiLanguage.T("Si la famille manque, clique sur « Parcourir… », choisis ton fichier .RFA, puis « Charger dans le projet ». L'import est facultatif ici : la maquette de formation possède déjà sa famille.", "If the family is missing, click “Browse…”, choose your .RFA file, then “Load into project”. Importing is optional here: the training model already has its family."), "btnBrowseRfa", true),
                new DemoStep(UiLanguage.T("Associer les dimensions", "Map the dimensions"), UiLanguage.T("Ces champs relient les dimensions calculées par BIMaestro aux paramètres de ta famille. Pour une réservation rectangulaire murale : longueur dans l'axe du mur, hauteur et profondeur. Les noms doivent exister dans le type choisi.", "These fields link the dimensions calculated by BIMaestro to your family parameters. For a rectangular wall opening: length along the wall, height and depth. The names must exist in the chosen type."), "panelMapRectWall", true),
                new DemoStep(UiLanguage.T("Position verticale", "Vertical position"), UiLanguage.T("La référence verticale règle où se place la famille par rapport au croisement ; le décalage corrige un écart propre à sa construction. Commence par « Automatique » et 0 mm, puis ajuste seulement si la réservation est décalée.", "The vertical reference sets where the family is placed relative to the crossing; the offset corrects a shift specific to how it is built. Start with “Automatic” and 0 mm, then adjust only if the opening is offset."), "cbVerticalReference", true),
                new DemoStep(UiLanguage.T("Enregistrer le mapping", "Save the mapping"), UiLanguage.T("Pour une nouvelle famille, « Enregistrer cette famille » mémorise le type, les paramètres et la position pour ce cas. Ne clique pas ici pendant la démo : nous gardons la configuration fournie.", "For a new family, “Save this family” stores the type, parameters and position for this case. Do not click it during the demo: we keep the supplied configuration."), "btnApplyMapping", true),
                new DemoStep(UiLanguage.T("Arrondi des dimensions", "Dimension rounding"), UiLanguage.T("Dans Réglages, l'arrondi aux 50 mm supérieurs transforme par exemple 232 mm en 250 mm. Il n'ajoute pas d'abord un jeu de 50 mm ; adapte ce choix à ta convention de projet.", "In Settings, rounding up to the next 50 mm turns 232 mm into 250 mm, for example. It does not add a 50 mm clearance first; adapt this choice to your project conventions."), "chkDefaultNorme", true),
                new DemoStep(UiLanguage.T("Dynamo est facultatif", "Dynamo is optional"), UiLanguage.T("Un script Dynamo peut enrichir les paramètres après la création, par exemple avec des hauteurs NGF. Il n'est pas nécessaire pour créer la réservation : laisse-le désactivé si tu n'as pas de script adapté.", "A Dynamo script can fill in parameters after creation, for example with survey datum elevations. It is not needed to create the opening: leave it disabled if you do not have a suitable script."), "chkDefaultDynamo", true),
                new DemoStep(UiLanguage.T("Revenir à l'exécution", "Back to Run"), UiLanguage.T("La famille et les réglages sont compris. Reviens à Exécution pour créer ta réservation sans toucher aux paramètres de la maquette de formation.", "You now understand the family and settings. Go back to Run to create your opening without changing the training model settings."), "ReservationExecutionTab", true),
                new DemoStep(UiLanguage.T("Placer ta réservation", "Place your opening"), UiLanguage.T("Clique sur « Lancer », puis sélectionne la canalisation et le mur dans la vue 01. Après création, contrôle la position et les dimensions dans les Propriétés de Revit.", "Click “Run”, then select the pipe and the wall in view 01. After creation, check the position and dimensions in Revit Properties."), "DemoRunReservationButton")
            },
            ["pipe-calculation"] = new[]
            {
                new DemoStep(UiLanguage.T("Le périmètre", "The scope"), UiLanguage.T("Dans la vue 04, Bulbizarre a sélectionné les canalisations DN100 et DN50, leurs coudes et deux vannes papillon, ainsi qu’une gaine avec ses coudes. Les vannes sont comptées comme accessoires, sans être ajoutées à la longueur des tuyaux. Passe à l’étape suivante avant de modifier les options.", "In view 04, Bulbasaur has selected the DN100 and DN50 pipes, their elbows and two butterfly valves, plus a duct with its elbows. Valves are counted as accessories and are not added to the pipe length. Go to the next step before changing the options."), "CalculationOptionsTitle", true),
                new DemoStep(UiLanguage.T("Inclure les gaines", "Include ducts"), UiLanguage.T("Coche « Inclure les gaines » pour compter aussi la gaine rectangulaire et ses coudes. Si la case est déjà cochée, laisse-la ainsi et clique sur Suivant.", "Check “Include ducts” to also count the rectangular duct and its elbows. If the box is already checked, leave it and click Next."), "IncludeDuctsCheckBox"),
                new DemoStep(UiLanguage.T("Filtrer par système", "Filter by system"), UiLanguage.T("Le filtre limite le résultat à certains types de systèmes. Laisse-le désactivé pour voir les deux diamètres de canalisations et la gaine ensemble.", "The filter limits the result to certain system types. Leave it disabled to see both pipe diameters and the duct together."), "EnableSystemTypeFilterCheckBox", true),
                new DemoStep(UiLanguage.T("Voir aussi le fichier Excel", "See the Excel file too"), UiLanguage.T("Garde « Exporter les résultats vers Excel » coché. Après le récapitulatif dans Revit, BIMaestro créera le fichier et te proposera de l'ouvrir. Si la case est déjà cochée, clique sur Suivant.", "Keep “Export results to Excel” checked. After the summary in Revit, BIMaestro will create the file and offer to open it. If the box is already checked, click Next."), "ExportToExcelCheckBox"),
                new DemoStep(UiLanguage.T("Lancer le calcul", "Run the calculation"), UiLanguage.T("Clique sur OK. Lis le récapitulatif dans Revit, puis accepte l'ouverture du fichier Excel pour examiner les mêmes résultats. Bulbizarre vérifiera ce calcul unique.", "Click OK. Read the summary in Revit, then accept to open the Excel file to review the same results. Bulbasaur will check this single calculation."), "OkButton")
            },
            ["organizer"] = new[]
            {
                new DemoStep(UiLanguage.T("Deux niveaux à traiter", "Two levels to process"), UiLanguage.T("Huit places CML_Parking sont sélectionnées : quatre sur le niveau de base et quatre sur « BIMaestro - Niveau 1 ». Organisateur modifiera uniquement ces places.", "Eight CML_Parking spaces are selected: four on the base level and four on “BIMaestro - Niveau 1”. Organizer will only change these spaces."), "OrganizerHeaderTitle", true),
                new DemoStep(UiLanguage.T("Le bon paramètre", "The right parameter"), UiLanguage.T("Choisis « CML_Numéros de place » dans la liste : ce paramètre texte d'instance porte le numéro visible de chaque place.", "Choose “CML_Numéros de place” in the list: this instance text parameter holds the visible number of each space."), "ParameterComboBox"),
                new DemoStep(UiLanguage.T("Préfixe", "Prefix"), UiLanguage.T("Saisis « PK- » : il sera ajouté avant chaque numéro.", "Type “PK-”: it will be added before each number."), "PrefixTextBox"),
                new DemoStep(UiLanguage.T("Format", "Format"), UiLanguage.T("Choisis « 001,002,003... » pour numéroter les places sur trois chiffres.", "Choose “001,002,003...” to number the spaces with three digits."), "NumberFormatComboBox"),
                new DemoStep(UiLanguage.T("Hauteur de bande", "Band height"), UiLanguage.T("La hauteur de bande regroupe les éléments par lignes dans la vue. Garde 1 m pour distinguer les places de chaque rangée.", "The band height groups elements into rows in the view. Keep 1 m to separate the spaces in each row."), "BandHeightTextBox", true),
                new DemoStep(UiLanguage.T("D'abord par niveau", "Level first"), UiLanguage.T("Coche « Trier par niveau ». Le niveau de base recevra les quatre premiers numéros, puis le niveau supérieur les quatre suivants.", "Check “Sort by level”. The base level will get the first four numbers, then the upper level the next four."), "SortByLevelCheckBox"),
                new DemoStep(UiLanguage.T("Premier passage", "First pass"), UiLanguage.T("Clique sur Renommer. Bulbizarre vérifiera les huit numéros visibles, puis tournera automatiquement cette vue 3D de 90° pour un second essai rapide.", "Click Rename. Bulbasaur will check the eight visible numbers, then automatically rotate this 3D view by 90° for a quick second try."), "DemoRenameButton")
            },
            ["family-browser"] = new[]
            {
                new DemoStep(UiLanguage.T("Ton catalogue d'essai", "Your test catalog"), UiLanguage.T("Clique sur « Familles » dans la colonne de gauche. Bulbizarre a ouvert 35 familles de mobilier réparties en dossiers. Ce catalogue temporaire ne remplace pas les chemins que tu as enregistrés pour ta bibliothèque.", "Click “Families” in the left column. Bulbasaur has opened 35 furniture families sorted into folders. This temporary catalog does not replace the paths you saved for your library."), "AllFamiliesButton", completionEvent: "families-root-open"),
                new DemoStep(UiLanguage.T("Entrer dans Mobilier", "Open Mobilier"), UiLanguage.T("Dans l'arborescence, ouvre « Mobilier ». Le guide attend que ce dossier soit réellement affiché.", "In the tree, open “Mobilier” (Furniture). The guide waits until this folder is actually displayed."), "FolderTreeView", completionEvent: "folder-mobilier"),
                new DemoStep(UiLanguage.T("Ouvrir Bureau", "Open Bureau"), UiLanguage.T("Ouvre le dossier « Bureau ». Il contient cinq familles et un sous-dossier ; observe aussi les cartes de dossiers à droite.", "Open the “Bureau” (Office) folder. It contains five families and a subfolder; also look at the folder cards on the right."), "FolderTreeView", completionEvent: "folder-bureau"),
                new DemoStep(UiLanguage.T("Lire les fiches", "Read the cards"), UiLanguage.T("Les cartes affichent le nom et un aperçu lorsqu'il existe. Une image PNG portant le même nom qu'une famille RFA peut être retrouvée dans un dossier miroir ; sans image, le navigateur peut utiliser une vignette native.", "Cards show the name and a preview when one exists. A PNG image with the same name as an RFA family can be found in a mirror folder; without an image, the browser can use a native thumbnail."), "FamilyListView", true),
                new DemoStep(UiLanguage.T("Autres actions d'une carte", "Other card actions"), UiLanguage.T("Sur une carte, le clic droit propose « Rentrer dans la famille » pour ouvrir le RFA source, « Charger la dernière version » pour recharger le fichier depuis le disque et « Ajouter à la collection active » pour classer plusieurs familles. Aucune de ces actions n'est nécessaire pour le placement d'essai.", "On a card, right-click offers “Edit family” to open the source RFA, “Load latest version” to reload the file from disk, and “Add to active collection” to group several families. None of these actions is needed for the test placement."), "FamilyListView", true),
                new DemoStep(UiLanguage.T("Explorer le sous-dossier", "Explore the subfolder"), UiLanguage.T("Clique sur la carte « Salle de réunion » dans la zone principale. Ses cinq familles montrent comment conserver ton classement actuel, même à plusieurs niveaux. Elles n'ont pas encore de photo : tu les créeras plus loin avec les photos automatiques.", "Click the “Salle de réunion” (Meeting room) card in the main area. Its five families show how to keep your current organization, even across several levels. They have no picture yet: you will create them later with automatic pictures."), "TutorialMeetingFolderCard", completionEvent: "folder-reunion"),
                new DemoStep(UiLanguage.T("Chercher dans le dossier", "Search the folder"), UiLanguage.T("Saisis « Table » dans la recherche. Le mode « Dossier » limite les résultats au dossier ouvert.", "Type “Table” in the search box. “Folder” mode limits results to the open folder."), "SearchBox", completionEvent: "search-table"),
                new DemoStep(UiLanguage.T("Étendre la recherche", "Broaden the search"), UiLanguage.T("Clique sur « Tout ». BIMaestro efface la recherche « Table » et passe à l'ensemble du catalogue, y compris les autres sous-dossiers.", "Click “All”. BIMaestro clears the “Table” search and switches to the whole catalog, including the other subfolders."), "SearchAllButton", completionEvent: "search-all"),
                new DemoStep(UiLanguage.T("Retrouver une famille", "Find a family"), UiLanguage.T("Saisis « Chaise ». Les résultats doivent maintenant inclure des familles rangées hors de Salle de réunion.", "Type “Chaise” (chair). The results should now include families stored outside Salle de réunion."), "SearchBox", completionEvent: "search-chaise"),
                new DemoStep(UiLanguage.T("Mettre une famille en favori", "Add a family to favorites"), UiLanguage.T("Deux chaises sont déjà en favoris pour l’exemple : leurs étoiles sont orange. Clique sur l’étoile bleue encadrée en vert d’une autre chaise pour l’ajouter. Elle devient orange. Un second clic retire le favori. Ces favoris d’essai restent temporaires.", "Two chairs are already favorites for the example: their stars are orange. Click the blue star outlined in green on another chair to add it. It turns orange. A second click removes the favorite. These test favorites are temporary."), "TutorialFavoriteStar", completionEvent: "favorite-added"),
                new DemoStep(UiLanguage.T("Retrouver ses favoris", "Find your favorites"), UiLanguage.T("Ouvre l'onglet « Favoris ». La famille étoilée doit apparaître dans la collection Favoris. Dans ta vraie bibliothèque, les favoris sont enregistrés et restent disponibles à la prochaine ouverture.", "Open the “Favorites” tab. The starred family should appear in the Favorites collection. In your real library, favorites are saved and remain available next time you open it."), "FavoritesTabItem", completionEvent: "favorites-open"),
                new DemoStep(UiLanguage.T("Collections et chargement", "Collections and loading"), UiLanguage.T("La liste « Collection » permet de regrouper plusieurs familles ; « Nouv. », « Ren. » et « Suppr. » gèrent ces groupes. Le bouton « Charger la collection » charge toutes ses familles dans Revit. La croix d'une carte retire seulement cette famille de la collection. La rosace peut aussi ouvrir une collection choisie, mais ses pages par défaut montrent les familles récentes et utilisées.", "The “Collection” list lets you group several families; “New”, “Ren.” and “Del.” manage these groups. The “Load collection” button loads all its families into Revit. The cross on a card only removes that family from the collection. The radial menu can also open a chosen collection, but its default pages show recent and used families."), "CollectionCombo", true),
                new DemoStep(UiLanguage.T("Revenir aux dossiers", "Back to folders"), UiLanguage.T("Rouvre l'onglet des dossiers pour poursuivre l'exercice. Le favori d'essai sera retiré automatiquement quand tu quitteras le guide.", "Reopen the folders tab to continue the exercise. The test favorite will be removed automatically when you leave the guide."), "FoldersTabItem", completionEvent: "folders-open"),
                new DemoStep(UiLanguage.T("Voir une famille en 3D", "View a family in 3D"), UiLanguage.T("Sur une carte de famille, clique sur le bouton « 3D », puis ferme l'aperçu pour revenir ici. Tu peux examiner le modèle avant de le charger.", "On a family card, click the “3D” button, then close the preview to come back here. You can examine the model before loading it."), "GroupedFamilyListView", completionEvent: "preview-3d"),
                new DemoStep(UiLanguage.T("Ouvrir les paramètres", "Open the settings"), UiLanguage.T("Clique sur « Paramètres » pour découvrir où se règlent les dossiers et où se créent les aperçus 3D en série.", "Click “Settings” to see where folders are configured and where 3D previews are created in batch."), "SettingsTabItem", completionEvent: "settings-open"),
                new DemoStep(UiLanguage.T("Photos automatiques", "Automatic pictures"), UiLanguage.T("Pour l'essai, les cinq familles de « Salle de réunion » sont présélectionnées. « Lancer l'export 3D » ouvre le choix A/B : Bulbizarre t'expliquera comment garder la vue existante ou la vue normalisée. Valide ce choix, puis attends la fin de l'export pour revenir au guide. L'opération peut prendre du temps ; tu peux aussi la passer avec « Suivant ».", "For this exercise, the five families in “Salle de réunion” are preselected. “Start 3D export” opens the A/B choice: Bulbasaur will explain how to keep the existing view or the standardized view. Confirm your choice, then wait for the export to finish to return to the guide. This can take a while; you can also skip it with “Next”."), "StartPreviewButton", true, completionEvent: "preview-export-complete"),
                new DemoStep(UiLanguage.T("Adapter à ta bibliothèque", "Adapt it to your library"), UiLanguage.T("« Modifier les chemins… » permet de choisir ton propre dossier de familles RFA, puis un dossier d'images PNG. Leurs sous-dossiers doivent se correspondre. Pendant ce guide, tes chemins enregistrés sont protégés : quitte le tutoriel avant de choisir ta bibliothèque personnelle.", "“Change paths…” lets you choose your own RFA family folder, then a PNG image folder. Their subfolders must match. During this guide, your saved paths are protected: leave the tutorial before choosing your personal library."), "ChangePathsButton", true),
                new DemoStep(UiLanguage.T("Découvrir l’assistant IA", "Discover the AI assistant"), UiLanguage.T("Clique sur « Ouvrir l’assistant… » dans Mots-clés intelligents. Dans la fenêtre, coche une ou deux familles puis utilise le bouton « Proposer avec l’IA » encadré en vert. Relis les propositions : elles restent modifiables. Ferme ensuite l’assistant pour poursuivre le parcours. Tu peux passer cette étape si l’IA n’est pas disponible.", "Click “Open assistant…” in Smart keywords. In the window, check one or two families, then use the “Suggest with AI” button outlined in green. Review the suggestions: you can still edit them. Then close the assistant to continue the walkthrough. You can skip this step if AI is not available."), "SearchMetadataAssistantButton", true, completionEvent: "search-assistant-closed"),
                new DemoStep(UiLanguage.T("Charger et placer une famille", "Load and place a family"), UiLanguage.T("Bulbizarre revient au dossier Bureau. Trois favoris temporaires sont préparés pour la suite : un bureau, une table et une chaise. Ils seront disponibles dans la collection Favoris de la rosace. Double-clique sur la carte « Bureau commun » : le simple clic sélectionne la carte, le double-clic charge la famille dans Revit et lance son placement. Clique ensuite dans la vue pour la poser, puis appuie sur Échap pour sortir du mode placement.", "Bulbasaur goes back to the Bureau folder. Three temporary favorites are prepared for what follows: a desk, a table and a chair. They will be available in the radial menu's Favorites collection. Double-click the “Bureau commun” card: a single click selects the card, a double-click loads the family into Revit and starts placing it. Then click in the view to place it, and press Esc to exit placement mode."), "FamilyListView", completionEvent: "load-bureau-commun"),
                new DemoStep(UiLanguage.T("Ouvrir Famille dans Revit", "Open Family in Revit"), UiLanguage.T("Le navigateur se ferme et Bulbizarre revient dans Revit. Termine le placement précédent avec Échap, ouvre l'onglet BIMaestro si besoin, puis clique sur la petite flèche du bouton « Famille ». Le contour vert suit le bon bouton.", "The browser closes and Bulbasaur returns to Revit. Finish the previous placement with Esc, open the BIMaestro tab if needed, then click the small arrow on the “Family” button. The green outline follows the right button."), "RevitFamilySplit"),
                new DemoStep(UiLanguage.T("Choisir « Rosace familles »", "Choose “Family radial menu”"), UiLanguage.T("Dans la liste ouverte par la flèche de « Famille », clique sur l'entrée « Rosace familles ». Cette rosace s'ouvre près de la souris avec les 16 familles récentes simulées de l'exercice. Bulbizarre attend son ouverture avant de poursuivre.", "In the list opened by the “Family” arrow, click “Family radial menu”. This radial menu opens near the mouse with the exercise's 16 simulated recent families. Bulbasaur waits for it to open before continuing."), "RevitRosace", completionEvent: "radial-opened"),
                new DemoStep(UiLanguage.T("Comprendre les familles récentes", "Understand recent families"), UiLanguage.T("La rosace montre ici 16 familles d'exemple sur deux pages. Hors tutoriel, elle affiche tes familles les plus utilisées et tes récentes.\nTourne la molette sur la rosace pour changer de page, survole les cases pour lire les noms, puis clique sur Suivant.", "Here the radial menu shows 16 sample families on two pages. Outside the tutorial, it shows your most used and recent families.\nTurn the wheel over the radial menu to change page, hover over the slots to read the names, then click Next."), "RevitUseShortcut", true),
                new DemoStep(UiLanguage.T("Passer à l'onglet Vue", "Go to the View tab"), UiLanguage.T("Ferme la rosace avec Échap, puis clique sur l'onglet « Vue » du ruban Revit. Bulbizarre encadre l'onglet et suit ton choix.", "Close the radial menu with Esc, then click the “View” tab on the Revit ribbon. Bulbasaur outlines the tab and follows your choice."), "RevitViewTab"),
                new DemoStep(UiLanguage.T("Ouvrir Interface utilisateur", "Open User Interface"), UiLanguage.T("Dans Vue, ouvre « Interface utilisateur » dans le panneau Fenêtres. Le contour vert passe du ruban BIMaestro au ruban Revit.", "In View, open “User Interface” in the Windows panel. The green outline moves from the BIMaestro ribbon to the Revit ribbon."), "RevitUserInterface"),
                new DemoStep(UiLanguage.T("Créer le raccourci dans Revit", "Create the shortcut in Revit"), UiLanguage.T("Clique sur « Raccourcis clavier », puis :\n1. Cherche « Rosace familles » et sélectionne sa ligne.\n2. Clique dans « Appuyer sur de nouvelles touches » et tape B puis F (ou une autre combinaison libre).\n3. Clique sur « Attribuer », puis « OK ». Bulbizarre avance ensuite seul.", "Click “Keyboard Shortcuts”, then:\n1. Search for “Family radial menu” and select its row.\n2. Click in “Press new keys” and type B then F (or another free combination).\n3. Click “Assign”, then “OK”. Bulbasaur then moves on by itself."), "RevitKeyboardShortcuts", true),
                new DemoStep(UiLanguage.T("Essayer ton raccourci", "Try your shortcut"), UiLanguage.T("Reviens dans la vue Revit, place la souris et tape BF, ou ton raccourci choisi. Bulbizarre attend l’ouverture de la rosace.", "Go back to the Revit view, place the mouse and type BF, or the shortcut you chose. Bulbasaur waits for the radial menu to open."), "RevitUseShortcut", completionEvent: "radial-opened"),
                new DemoStep(UiLanguage.T("Afficher les favoris dans la rosace", "Show favorites in the radial menu"), UiLanguage.T("Fais un clic droit au centre de la rosace, puis choisis « Charger une collection » > « Favoris ». La rosace affiche les favoris d'essai : un bureau, une table et une chaise, ainsi que la chaise que tu as étoilée si elle est différente. Survole les cases pour identifier les familles. Bulbizarre attend que tu choisisses réellement Favoris. Tu placeras ensuite une chaise. Ces favoris d'exemple ne sont pas enregistrés dans ta bibliothèque personnelle.", "Right-click the center of the radial menu, then choose “Load a collection” > “Favorites”. The radial menu shows the test favorites: a desk, a table and a chair, plus the chair you starred if it is different. Hover over the slots to identify the families. Bulbasaur waits until you actually choose Favorites. You will then place a chair. These example favorites are not saved to your personal library."), "RevitUseShortcut", completionEvent: "radial-tutorial-favorites-selected"),
                new DemoStep(UiLanguage.T("Utiliser ton raccourci", "Use your shortcut"), UiLanguage.T("Reviens dans une vue Revit, place la souris où tu veux ouvrir la rosace et tape BF, ou le raccourci que tu as choisi. Fais un clic droit au centre de la rosace, puis « Charger une collection » > « Favoris ». Clique sur une chaise des favoris d'essai pour lancer son placement. Comme dans le navigateur, clique ensuite dans la vue pour la poser, puis appuie sur Échap. Bulbizarre attend la création réelle d'une chaise dans la maquette.", "Go back to a Revit view, place the mouse where you want the radial menu to open and type BF, or the shortcut you chose. Right-click the center of the radial menu, then “Load a collection” > “Favorites”. Click a chair among the test favorites to start placing it. As in the browser, then click in the view to place it, and press Esc. Bulbasaur waits for a chair to actually be created in the model."), "RevitUseShortcut", completionEvent: "radial-tutorial-chaise-placed")
            },
            ["history"] = new[]
            {
                new DemoStep(UiLanguage.T("Deux suppressions et une modification", "Two deletions and one change"), UiLanguage.T("Bulbizarre a supprimé deux objets de la scène, puis passé le troisième de 4 à 2 chaises. Nous allons faire réapparaître les deux objets, puis retrouver les anciennes valeurs du témoin. Dans Action, choisis « Suppressions ».", "Bulbasaur deleted two elements from the scene, then changed the third from 4 to 2 chairs. We will bring back both elements, then recover the reference element's previous values. In Action, choose “Deletions”."), "ActionFilterCombo"),
                new DemoStep(UiLanguage.T("Filtrer par utilisateur", "Filter by user"), UiLanguage.T("Ce filtre isole les actions d'une personne. Il sert à comprendre qui a modifié la maquette, mais une absence de résultat peut aussi venir de la période chargée. Ne change rien pour retrouver les objets de la démo.", "This filter isolates one person's actions. It helps understand who changed the model, but an empty result can also come from the loaded period. Do not change anything to find the demo elements."), "UserFilterCombo", true),
                new DemoStep(UiLanguage.T("Recherche et période", "Search and period"), UiLanguage.T("La recherche cible un élément ou une information précise. Les dates « Du » et « Au » limitent les événements chargés ; « Charger période » relit alors l'historique. Garde les filtres actuels pour l'exercice.", "Search targets a specific element or piece of information. The “From” and “To” dates limit the loaded events; “Load period” then rereads the history. Keep the current filters for the exercise."), "SearchBox", true),
                new DemoStep(UiLanguage.T("Commencer en mode Simple", "Start in Simple mode"), UiLanguage.T("Choisis « Simple ». Ce mode affiche des volumes rouges pour repérer les objets supprimés, avec moins de détails à charger que le mode Détaillé. Garde-le par défaut.", "Choose “Simple”. This mode shows red volumes to locate deleted elements, with less detail to load than Detailed mode. Keep it as the default."), "SimpleMeshModeRadio"),
                new DemoStep(UiLanguage.T("Choisir le cluster", "Choose the cluster"), UiLanguage.T("Sélectionne la carte contenant les deux meubles supprimés. Nous allons observer le même cluster dans les deux modes.", "Select the card containing the two deleted furniture items. We will look at the same cluster in both modes."), "VisualCardsList"),
                new DemoStep(UiLanguage.T("Observer l’aperçu Simple", "Look at the Simple preview"), UiLanguage.T("Clique sur « Visualiser cluster ». La fenêtre se mettra de côté pour te laisser observer les volumes rouges dans Revit.", "Click “Visualize cluster”. The window will step aside so you can look at the red volumes in Revit."), "VisualizeDeletedButton", completionEvent: "history-preview-simple-observed"),
                new DemoStep(UiLanguage.T("Passer en mode Détaillé", "Switch to Detailed mode"), UiLanguage.T("Choisis maintenant « Détaillé ». Il montre mieux la forme des meubles lorsque les données sont disponibles, mais peut ralentir le chargement et demander davantage de ressources au PC. Le tuto reviendra automatiquement en Simple après la comparaison et à sa fermeture.", "Now choose “Detailed”. It shows the furniture shape better when the data is available, but can slow down loading and use more PC resources. The tutorial will automatically switch back to Simple after the comparison and when it closes."), "DetailedMeshModeRadio"),
                new DemoStep(UiLanguage.T("Comparer l’aperçu Détaillé", "Compare the Detailed preview"), UiLanguage.T("Garde le même cluster sélectionné, puis clique à nouveau sur « Visualiser cluster ». L’aperçu détaillé remplacera les volumes simples : compare la forme des meubles dans Revit.", "Keep the same cluster selected, then click “Visualize cluster” again. The detailed preview will replace the simple volumes: compare the furniture shape in Revit."), "VisualizeDeletedButton", completionEvent: "history-preview-detailed-observed"),
                new DemoStep(UiLanguage.T("Retirer l’aperçu rouge", "Remove the red preview"), UiLanguage.T("Tu as observé l’emplacement des objets supprimés. Clique maintenant sur « Nettoyer previews » pour retirer les silhouettes rouges avant de restaurer les objets.", "You have seen where the deleted elements were. Now click “Clean previews” to remove the red silhouettes before restoring the elements."), "CleanPreviewsButton", completionEvent: "history-preview-cleaned"),
                new DemoStep(UiLanguage.T("Sélectionner le mobilier à restaurer", "Select the furniture to restore"), UiLanguage.T("Reste dans la vue visuelle. Sélectionne la carte du cluster contenant les deux suppressions de mobilier. Si elles apparaissent séparément, restaure une carte puis la seconde.", "Stay in the visual view. Select the cluster card containing the two furniture deletions. If they appear separately, restore one card, then the other."), "VisualCardsList", true),
                new DemoStep(UiLanguage.T("Faire réapparaître les deux objets", "Bring back both elements"), UiLanguage.T("Clique sur « Restaurer les éléments » et confirme. Si tu n'as restauré qu'un objet, sélectionne la seconde suppression et recommence. Bulbizarre ne poursuivra que lorsque les deux objets seront présents dans la maquette.", "Click “Restore elements” and confirm. If you only restored one element, select the second deletion and do it again. Bulbasaur will only continue once both elements are in the model."), "RestoreDeletedButton"),
                new DemoStep(UiLanguage.T("Retrouver la modification des paramètres", "Find the parameter change"), UiLanguage.T("Dans Action, choisis « Modification paramètres ». Retrouve la ligne du mobilier témoin dont le paramètre « Nombre chaises » est passé de 4 à 2. Garde la recherche vide et la période de l'exercice pour retrouver cet événement.", "In Action, choose “Parameter change”. Find the row for the reference furniture whose “Nombre chaises” parameter went from 4 to 2. Keep the search empty and the exercise period to find this event."), "ActionFilterCombo", true),
                new DemoStep(UiLanguage.T("Sélectionner la modification", "Select the change"), UiLanguage.T("Dans la vue visuelle, sélectionne la carte du témoin dont « Nombre chaises » est passé de 4 à 2. Nous allons lui rendre ses 4 chaises avec la restauration des paramètres.", "In the visual view, select the card of the reference element whose “Nombre chaises” went from 4 to 2. We will give it back its 4 chairs by restoring the parameters."), "VisualCardsList"),
                new DemoStep(UiLanguage.T("Revenir aux anciens paramètres", "Go back to the previous parameters"), UiLanguage.T("Avec la ligne de modification du témoin sélectionnée, clique sur « Restaurer » et confirme. Ce bouton réapplique les anciennes valeurs de paramètres enregistrées pour cette ligne. Il est différent de « Restaurer les éléments », utilisé pour recréer les objets supprimés. Bulbizarre vérifiera le retour du témoin à 4 chaises avant de terminer.", "With the reference element's change row selected, click “Restore” and confirm. This button reapplies the previous parameter values recorded for this row. It is different from “Restore elements”, which recreates deleted elements. Bulbasaur will check that the reference element is back to 4 chairs before finishing."), "RestoreParametersButton")
            }
        };

        internal static DemoStep[] OrganizerRotatedSteps => new[]
        {
            new DemoStep(UiLanguage.T("Comparer sans tri par niveau", "Compare without sorting by level"), UiLanguage.T("La vue de dessus a tourné de 90°. « Trier par niveau » est maintenant décoché pour numéroter selon l’ordre visible. Vérifie cette option puis clique sur Suivant.", "The top view has rotated by 90°. “Sort by level” is now unchecked so numbering follows the visible order. Check this option, then click Next."), "SortByLevelCheckBox", true),
            new DemoStep(UiLanguage.T("Relancer depuis la vue tournée", "Run again from the rotated view"), UiLanguage.T("CML_Numéros de place, PK- et le format 001 sont déjà préparés. Garde « Trier par niveau » décoché et clique sur Renommer pour comparer l'ordre dicté par la vue aux numéros du premier passage.", "CML_Numéros de place, PK- and the 001 format are already set. Keep “Sort by level” unchecked and click Rename to compare the order given by the view with the numbers from the first pass."), "DemoRenameButton")
        };

        // A TUTO button may be pressed from an ordinary project, without the
        // prepared model scene. These shorter tours explain the interface and
        // never claim that training elements have already been selected.
        internal static IReadOnlyDictionary<string, DemoStep[]> StandaloneSteps =>
            new Dictionary<string, DemoStep[]>
            {
                ["clash-3d"] = new[]
                {
                    new DemoStep(UiLanguage.T("Définir le périmètre", "Define the scope"), UiLanguage.T("Maquette entière, Vue active ou Sélection définit les objets de départ. Les obstacles sont recherchés autour d'eux. Actualiser reprend la vue et la sélection actuelles. Pour un exercice avec deux conflits connus, lance Clash 3D depuis Parcours guidés dans la maquette de formation.", "Entire model, Active view or Selection defines the source elements. Obstacles are searched around them. Refresh picks up the current view and selection. For an exercise with two known clashes, launch Clash 3D from Guided tours in the training model."), "ScopeCombo", true),
                    new DemoStep(UiLanguage.T("Choisir les contrôles", "Choose the checks"), UiLanguage.T("Déplie Personnaliser l'analyse. Choisis les catégories et les contrôles utiles. Collisions avec les liens nécessite des liens chargés et cochés. Les connexions physiques directes entre réseaux sont exclues ; Connecteurs ouverts et Supports de murs sont des contrôles complémentaires.", "Expand Customize analysis. Choose the relevant categories and checks. Clashes with links requires loaded and checked links. Direct physical connections between systems are excluded; Open connectors and Wall supports are additional checks."), "LocalCheck", true),
                    new DemoStep(UiLanguage.T("Comprendre le seuil", "Understand the threshold"), UiLanguage.T("Le seuil est en mm³ : il filtre un volume d'intersection, pas une distance de sécurité. Les contacts sans volume sont exclus. Une géométrie maillée ou indécidable peut produire une suspicion à examiner.", "The threshold is in mm³: it filters an intersection volume, not a safety distance. Contacts with no volume are excluded. Mesh or undecidable geometry can produce a suspected clash to review."), "VolumeBox", true),
                    new DemoStep(UiLanguage.T("Examiner les résultats", "Review the results"), UiLanguage.T("Analyser travaille par étapes et peut être annulé. Attends la fin pour un bilan complet. Détail ouvre un aperçu manipulable ; Voir en 3D cadre les objets orange et bleu dans Revit. Une intersection de paroi ne valide pas automatiquement une réservation.", "Analyze works in steps and can be canceled. Wait for it to finish for a complete summary. Details opens an interactive preview; View in 3D zooms to the orange and blue elements in Revit. A wall intersection does not automatically validate an opening."), "AnalyzeButton", true),
                    new DemoStep(UiLanguage.T("Créer une réservation depuis un conflit", "Create an opening from a clash"), UiLanguage.T("Pour une intersection confirmée entre un objet et un mur ou un sol, ouvre Voir en 3D ou Détail puis clique sur Créer la réservation lorsque le bouton est proposé. Il utilise la famille et les réglages d’Auto réservation. Contrôle les dimensions et l’ouverture réelle, puis relance l’analyse.", "For a confirmed intersection between an element and a wall or floor, open View in 3D or Details, then click Create opening when the button is offered. It uses the family and settings from Auto Opening. Check the dimensions and the actual opening, then run the analysis again."), "ResultsList", true),
                    new DemoStep(UiLanguage.T("Corriger, puis contrôler", "Fix, then check"), UiLanguage.T("Corrige les objets avec les outils Revit, puis relance la même analyse. Le statut Traité est une décision manuelle. Une modification rend les anciens résultats périmés ; seule une nouvelle analyse vérifie la géométrie actuelle.", "Fix the elements with Revit tools, then run the same analysis again. The Resolved status is a manual decision. A change makes previous results outdated; only a new analysis checks the current geometry."), "ResultsList", true),
                    new DemoStep(UiLanguage.T("Exporter", "Export"), UiLanguage.T("Exporter produit un rapport HTML autonome ou un CSV à partir des filtres affichés. Vérifie les statuts, le contrôle et la recherche avant d'enregistrer. Revenir à ma vue rétablit la vue et la sélection conservées avant le cadrage.", "Export produces a standalone HTML report or a CSV from the displayed filters. Check the statuses, the check type and the search before saving. Back to my view restores the view and selection saved before zooming."), "ExportButton", true)
                },
                ["reservation"] = new[]
                {
                    new DemoStep(UiLanguage.T("Choisir le support", "Choose the host"), UiLanguage.T("Mur crée une réservation dans un mur traversé ; Sol correspond à un autre cas de famille. Pour pratiquer avec une scène prête, ouvre Parcours guidés puis la maquette de formation.", "Wall creates an opening in a crossed wall; Floor is a different family case. To practice with a ready-made scene, open Guided tours, then the training model."), "hostMurCard", true),
                    new DemoStep(UiLanguage.T("Choisir la forme et le réseau", "Choose the shape and the system"), UiLanguage.T("La forme et le support déterminent les dimensions à associer à ta famille. Choisis Canalisation, Gaine, Porte, Fenêtre ou Autre objet 3D selon l’élément à traiter. Le parcours de formation utilise une canalisation et une réservation rectangulaire murale.", "The shape and host determine the dimensions to map to your family. Choose Pipe, Duct, Door, Window or Other 3D element depending on the element to process. The training walkthrough uses a pipe and a rectangular wall opening."), "shapeRectCard", true),
                    new DemoStep(UiLanguage.T("Configurer ta famille", "Set up your family"), UiLanguage.T("L'onglet Familles relie un type RFA à chaque combinaison de support, forme et hébergement. Vérifie les paramètres de dimensions avant d'enregistrer un nouveau mapping.", "The Families tab links an RFA type to each combination of host, shape and hosting. Check the dimension parameters before saving a new mapping."), "ReservationFamiliesTab", true),
                    new DemoStep(UiLanguage.T("Choisir les sources", "Choose the sources"), UiLanguage.T("Maquette utilise les objets et supports locaux. Lien IFC ou Lien RVT donne accès à la maquette et aux liens du type choisi. Double lien impose deux éléments liés, qui peuvent appartenir au même lien. Une famille avec hôte nécessite un support local.", "Model uses local elements and hosts. IFC link or RVT link gives access to the model and links of the chosen type. Double link requires two linked elements, which can belong to the same link. A hosted family needs a local host."), "chkDoubleLink", true),
                    new DemoStep(UiLanguage.T("Manuel ou automatique", "Manual or automatic"), UiLanguage.T("Le scan automatique est limité aux murs et aux éléments locaux. En mode manuel, une famille sans hôte permet de sélectionner l’objet et le mur ou le sol dans l’ordre voulu. Vérifie les options avant de lancer.", "The automatic scan is limited to walls and local elements. In manual mode, a non-hosted family lets you select the element and the wall or floor in any order. Check the options before running."), "chkAutomatique", true),
                    new DemoStep(UiLanguage.T("Lancer dans ton projet", "Run it in your project"), UiLanguage.T("Clique sur Lancer, sélectionne les éléments demandés puis contrôle la position, les dimensions et l’ouverture réelle de la réservation. La maquette de formation propose un exercice vérifié pas à pas.", "Click Run, select the requested elements, then check the position, dimensions and actual opening of the result. The training model offers a step-by-step verified exercise."), "DemoRunReservationButton", true)
                },
                ["pipe-calculation"] = new[]
                {
                    new DemoStep(UiLanguage.T("Définir le périmètre", "Define the scope"), UiLanguage.T("Sélectionne dans la vue les canalisations et raccords à calculer. Le résultat dépend de cette sélection ; l'exercice guidé prépare un réseau complet dans la maquette de formation.", "Select the pipes and fittings to calculate in the view. The result depends on this selection; the guided exercise prepares a complete system in the training model."), "CalculationOptionsTitle", true),
                    new DemoStep(UiLanguage.T("Inclure les gaines", "Include ducts"), UiLanguage.T("Coche cette option si ta sélection contient aussi des gaines. Le calcul les ajoutera aux canalisations sélectionnées.", "Check this option if your selection also contains ducts. The calculation will add them to the selected pipes."), "IncludeDuctsCheckBox", true),
                    new DemoStep(UiLanguage.T("Lire et exporter", "Read and export"), UiLanguage.T("Garde l'export Excel coché pour voir d'abord le récapitulatif dans Revit, puis recevoir un fichier que BIMaestro te proposera d'ouvrir.", "Keep Excel export checked to first see the summary in Revit, then get a file that BIMaestro will offer to open."), "ExportToExcelCheckBox", true),
                    new DemoStep(UiLanguage.T("Calculer", "Calculate"), UiLanguage.T("Clique sur OK quand ta sélection et tes options sont prêtes. Le parcours de formation vérifie ensuite les résultats sur sa scène dédiée.", "Click OK when your selection and options are ready. The training walkthrough then checks the results on its dedicated scene."), "OkButton", true)
                },
                ["organizer"] = new[]
                {
                    new DemoStep(UiLanguage.T("Choisir les éléments", "Choose the elements"), UiLanguage.T("Organisateur agit sur les éléments sélectionnés dans la vue. Pour l'essai vérifié, la maquette de formation utilise huit places CML_Parking sur deux niveaux.", "Organizer works on the elements selected in the view. For the verified exercise, the training model uses eight CML_Parking spaces on two levels."), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.T("Choisir le paramètre", "Choose the parameter"), UiLanguage.T("Sélectionne le paramètre d'instance à modifier. Sur les parkings de formation, il s'appelle « CML_Numéros de place » ; dans ton projet, le nom peut être différent.", "Select the instance parameter to change. On the training parking spaces, it is called “CML_Numéros de place”; in your project, the name may be different."), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.T("Régler le nom", "Set the name"), UiLanguage.T("Le préfixe, le format et le sens de lecture déterminent les nouvelles valeurs. « Trier par niveau » sépare les étages ; sans cette coche, l'ordre suit l'orientation de la vue.", "The prefix, format and reading direction determine the new values. “Sort by level” separates the floors; without this option, the order follows the view orientation."), "PrefixTextBox", true),
                    new DemoStep(UiLanguage.T("Appliquer", "Apply"), UiLanguage.T("Renommer écrit les valeurs dans le projet. Pour voir Bulbizarre contrôler chaque place, lance le parcours Organisateur depuis la maquette de formation.", "Rename writes the values to the project. To see Bulbasaur check each space, launch the Organizer walkthrough from the training model."), "DemoRenameButton", true)
                },
                ["history"] = new[]
                {
                    new DemoStep(UiLanguage.T("Retrouver les actions", "Find actions"), UiLanguage.T("Filtre l'historique par type d'action, utilisateur ou période. La maquette de formation prépare deux suppressions pour l'exercice complet.", "Filter the history by action type, user or period. The training model prepares two deletions for the full exercise."), "ActionFilterCombo", true),
                    new DemoStep(UiLanguage.T("Examiner un élément", "Examine an element"), UiLanguage.T("Sélectionne une carte dans la vue visuelle pour identifier l’objet et son action.", "Select a card in the visual view to identify the element and its action."), "VisualCardsList", true),
                    new DemoStep(UiLanguage.T("Choisir l’aperçu", "Choose the preview"), UiLanguage.T("Simple utilise des volumes pour situer les objets supprimés. Détaillé restitue mieux leur forme lorsque les données sont disponibles, avec davantage de ressources nécessaires. La maquette de formation permet de comparer ces deux modes.", "Simple uses volumes to locate deleted elements. Detailed renders their shape better when the data is available, but needs more resources. The training model lets you compare both modes."), "SimpleMeshModeRadio", true),
                    new DemoStep(UiLanguage.T("Visualiser", "Visualize"), UiLanguage.T("Visualiser cluster affiche l’aperçu du cluster sélectionné sans recréer les éléments supprimés dans la maquette.", "Visualize cluster shows a preview of the selected cluster without recreating the deleted elements in the model."), "VisualizeDeletedButton", true),
                    new DemoStep(UiLanguage.T("Nettoyer l’aperçu", "Clean the preview"), UiLanguage.T("Après avoir observé les objets dans Revit, clique sur Nettoyer previews pour retirer les silhouettes temporaires.", "After looking at the elements in Revit, click Clean previews to remove the temporary silhouettes."), "CleanPreviewsButton", true),
                    new DemoStep(UiLanguage.T("Restaurer les éléments supprimés", "Restore deleted elements"), UiLanguage.T("Restaurer les éléments recrée les objets supprimés. Vérifie la suppression et la disponibilité de la famille correspondante avant de confirmer, puis contrôle le résultat dans Revit.", "Restore elements recreates deleted elements. Check the deletion and that the matching family is available before confirming, then check the result in Revit."), "RestoreDeletedButton", true),
                    new DemoStep(UiLanguage.T("Restaurer les paramètres", "Restore parameters"), UiLanguage.T("Pour une modification de paramètres, sélectionne l’événement correspondant puis utilise Restaurer. Cette action réapplique les anciennes valeurs enregistrées ; elle ne recrée pas un objet supprimé. Le parcours de formation fait essayer les deux restaurations.", "For a parameter change, select the matching event, then use Restore. This reapplies the previous recorded values; it does not recreate a deleted element. The training walkthrough lets you try both kinds of restoration."), "RestoreParametersButton", true)
                }
            };

    }

    internal static class DemoHistoryScene
    {
        internal const string ChairCountParameter = "Nombre chaises";
        internal static Parameter ChairCount(FamilyInstance instance)
        {
            Parameter parameter = instance?.LookupParameter(ChairCountParameter);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
                throw new InvalidOperationException(UiLanguage.T("Crée une nouvelle maquette avec la famille mise à jour : le paramètre d’occurrence entier « Nombre chaises » doit être modifiable.", "Create a new model with the updated family: the integer instance parameter “Nombre chaises” must be editable."));
            return parameter;
        }
        internal static string ModifiedWitnessMark => DemoProjectBuilder.HistoryFurnitureMark(0) + "_MODIFIE";

        private static bool IsLearningDocument(Document doc) =>
            doc != null && System.IO.Path.GetFileName(doc.PathName)
                .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase);

        private static FamilyInstance FindWitness(IEnumerable<FamilyInstance> furniture) =>
            furniture.FirstOrDefault(instance =>
            {
                string mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                return mark == DemoProjectBuilder.HistoryFurnitureMark(0) || mark == ModifiedWitnessMark;
            });

        internal static bool HasRestoredFurniture(Document doc)
        {
            if (!IsLearningDocument(doc)) return false;
            var tables = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(instance => instance.SuperComponent == null &&
                    instance.Symbol?.Family?.Name == "CML_Table ronde + chaise").ToList();
            // Reconstruction deliberately omits Mark; identify the restored parent tables
            // by their family and exercise positions instead of expecting copied marks.
            return Enumerable.Range(1, 2).All(index => tables.Any(instance =>
                (instance.Location as LocationPoint)?.Point.DistanceTo(DemoProjectBuilder.HistoryFurniturePosition(index)) <
                    UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters)));

        }

        internal static bool IsNestedExerciseDeletion(Document doc, Analyse.ElementHistoryEvent item)
        {
            return IsLearningDocument(doc) && item?.Action == "delete" &&
                (item.Tx ?? "").StartsWith("BIMaestro - Exercice historique", StringComparison.Ordinal) &&
                item.Delta != null && item.Delta.TryGetValue("superComponentUniqueId", out object parent) &&
                !string.IsNullOrWhiteSpace(Convert.ToString(parent));
        }

        internal static bool HasRestoredParameters(Document doc)
        {
            if (!IsLearningDocument(doc)) return false;
            FamilyInstance witness = FindWitness(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>());
            return witness != null &&
                witness.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ==
                    DemoProjectBuilder.HistoryFurnitureMark(0) &&
                witness.LookupParameter(ChairCountParameter)?.AsInteger() == 4;
        }

        internal static int Reset(Document doc, out int removedReservations)
        {
            removedReservations = 0;
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiLanguage.T("Ouvre d'abord une maquette BIMaestro_Apprentissage pour recommencer les exercices.", "First open a BIMaestro_Apprentissage model to restart the exercises."));

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = FindWitness(furniture);
            if (witness == null)
                throw new InvalidOperationException(UiLanguage.T("Cette maquette n'a pas la scène de mobilier. Crée une nouvelle maquette de formation.", "This model does not have the furniture scene. Create a new training model."));
            Level level = doc.GetElement(witness.LevelId) as Level;
            if (level == null) throw new InvalidOperationException(UiLanguage.T("Le niveau du mobilier de démonstration est introuvable.", "The level of the demo furniture cannot be found."));

            var reservations = furniture.Where(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.DemoPrefix + "RESERVATION_CREEE")
                .Select(instance => instance.Id).ToList();
            int restoredFurniture = 0;
            using (var tx = new Transaction(doc, "BIMaestro - Recommencer les exercices"))
            {
                tx.Start();
                witness.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(DemoProjectBuilder.HistoryFurnitureMark(0));
                ChairCount(witness).Set(4);
                if (reservations.Count > 0) doc.Delete(reservations);
                removedReservations = reservations.Count;

                for (int index = 1; index <= 2; index++)
                {
                    XYZ target = DemoProjectBuilder.HistoryFurniturePosition(index);
                    string mark = DemoProjectBuilder.HistoryFurnitureMark(index);
                    FamilyInstance instance = furniture.FirstOrDefault(candidate =>
                        candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                        (candidate.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "") == mark);
                    if (instance == null)
                        instance = furniture.FirstOrDefault(candidate =>
                            candidate.Id != witness.Id && !reservations.Contains(candidate.Id) &&
                            candidate.GetTypeId().Equals(witness.GetTypeId()) &&
                            (candidate.Location as LocationPoint)?.Point.DistanceTo(target) <
                                UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters));
                    if (instance == null)
                    {
                        instance = doc.Create.NewFamilyInstance(target, witness.Symbol, level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        restoredFurniture++;
                    }
                    else if (instance.Location is LocationPoint point)
                        ElementTransformUtils.MoveElement(doc, instance.Id, target - point.Point);

                    Parameter parameter = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (parameter != null && !parameter.IsReadOnly) parameter.Set(mark);
                    ChairCount(instance).Set(4);
                }
                tx.Commit();
            }
            doc.Save();
            return restoredFurniture;
        }

        internal static int Prepare(Document doc)
        {
            // Only touch the generated learning model, and only its marked furniture.
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiLanguage.T("Ouvre d'abord une maquette BIMaestro_Apprentissage pour préparer cet exercice.", "First open a BIMaestro_Apprentissage model to prepare this exercise."));

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = FindWitness(furniture);
            if (witness == null)
                throw new InvalidOperationException(UiLanguage.T("Cette maquette utilise l'ancien scénario. Crée une nouvelle maquette de formation pour l'exercice de restauration.", "This model uses the old scenario. Create a new training model for the restoration exercise."));
            var toRemove = furniture
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoProjectBuilder.DemoPrefix + "HISTORIQUE_A_RESTAURER_", StringComparison.Ordinal))
                .Select(instance => instance.Id).ToList();
            Parameter chairCount = ChairCount(witness);

            // Normalize the exercise before taking the deletion/parameter snapshots.
            using (var tx = new Transaction(doc, "BIMaestro - État initial du mobilier témoin"))
            {
                tx.Start();
                chairCount.Set(4);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            Analyse.ElementHistoryTracker.ScheduleDeferredPrime(doc);
            Analyse.ElementHistoryTracker.CaptureSelectedElementDetails(doc, toRemove.Concat(new[] { witness.Id }).ToList());
            if (chairCount.AsInteger() != 4)
                throw new InvalidOperationException(UiLanguage.T("Le mobilier témoin doit avoir 4 chaises avant de préparer son historique.", "The reference furniture must have 4 chairs before its history is prepared."));
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : supprimer le mobilier"))
            {
                tx.Start();
                if (toRemove.Count > 0) doc.Delete(toRemove);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            DateTime changeStartedUtc = DateTime.UtcNow;
            // A separate transaction records both before/after values on the surviving instance.
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : modifier les paramètres du témoin"))
            {
                tx.Start();
                chairCount.Set(2);
                tx.Commit();
            }
            if (ChairCount(witness).AsInteger() != 2)
                throw new InvalidOperationException(UiLanguage.T("Le mobilier témoin n’a pas conservé le passage à 2 chaises.", "The reference furniture did not keep the change to 2 chairs."));
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            doc.Save();
            bool recorded = Analyse.ElementHistoryTracker.LoadElementHistory(
                Analyse.ElementHistoryTracker.GetDocumentKeysForHistory(doc), witness.UniqueId, 20)
                .Any(item => item.Ts >= changeStartedUtc && item.Action == "param_change" &&
                    item.Delta != null && item.Delta.TryGetValue("parameters", out object values) &&
                    Newtonsoft.Json.Linq.JArray.FromObject(values).OfType<Newtonsoft.Json.Linq.JObject>().Any(change =>
                        string.Equals((string)(change["Name"] ?? change["name"]), ChairCountParameter, StringComparison.OrdinalIgnoreCase) &&
                        (string)(change["OldValue"] ?? change["oldValue"]) == "4" &&
                        (string)(change["NewValue"] ?? change["newValue"]) == "2"));
            if (!recorded)
                throw new InvalidOperationException(UiLanguage.T("La modification Nombre chaises : 4 → 2 n’a pas été retrouvée dans l’historique. Le parcours ne peut pas démarrer sans cette information de restauration.", "The Nombre chaises change 4 → 2 was not found in the history. The walkthrough cannot start without this restoration information."));
            return toRemove.Count;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class DemoToursCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "DemoTours";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
            => ShowChoice(data.Application, ref message);

        internal static Result ShowChoice(UIApplication app, ref string message)
        {
            if (!DemoProjectBuilder.IsLearningDocument(app.ActiveUIDocument?.Document))
                return Result.Cancelled;
            var choice = new DemoTrainingChoiceWindow();
            new WindowInteropHelper(choice).Owner = app.MainWindowHandle;
            choice.ShowDialog();
            return RunChoice(app, choice.SelectedTour, ref message);
        }

        internal static Result RunChoice(UIApplication uiApp, string selectedChoice, ref string message)
        {
            if (selectedChoice == "create")
            {
                try
                {
                    DemoProjectBuilder.Create(uiApp);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                        UiLanguage.T("Création impossible : ", "Unable to create: ") + ex.Message);
                    return Result.Failed;
                }
            }
            else if (!string.IsNullOrEmpty(selectedChoice))
            {
                UIDocument activeDocument = uiApp.ActiveUIDocument;
                if (activeDocument == null)
                {
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre attend une maquette", "Bulbasaur is waiting for a model"),
                        UiLanguage.T("Ouvre d'abord la maquette de formation ou un projet Revit.", "First open the training model or a Revit project."));
                    return Result.Cancelled;
                }
                if (selectedChoice == "reset")
                {
                    try
                    {
                        int restored = DemoHistoryScene.Reset(activeDocument.Document, out int removed);
                        DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bravo ! Exercices prêts", "Well done! Exercises ready"),
                            UiLanguage.T("Exercices prêts à recommencer : ", "Exercises ready to restart: ") + restored + UiLanguage.T(" meuble(s) remis en place et ", " furniture item(s) put back and ") +
                            removed + UiLanguage.T(" réservation(s) du parcours retirée(s). Les couleurs personnelles restent inchangées.", " walkthrough opening(s) removed. Your personal colors are unchanged."));
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                            UiLanguage.T("Réinitialisation impossible : ", "Unable to reset: ") + ex.Message);
                        return Result.Failed;
                    }
                }
                DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                if (selectedChoice == "clash-3d")
                {
                    try
                    {
                        View3D view = DemoClashExercise.Prepare(activeDocument.Document);
                        activeDocument.Selection.SetElementIds(DemoClashExercise.Sources(activeDocument.Document));
                        if (activeDocument.ActiveView.Id != view.Id)
                        {
                            activeDocument.RequestViewChange(view);
                            DemoTourPendingSelection.Begin(activeDocument.Document, view.Id, selectedChoice);
                            return Result.Succeeded;
                        }
                        return DemoTourPendingSelection.PrepareNow(uiApp, selectedChoice) ? Result.Succeeded : Result.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre prépare Clash 3D", "Bulbasaur is preparing Clash 3D"), ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "mep-booster")
                {
                    try
                    {
                        View3D view = DemoProjectBuilder.EnsureBoosterScene(activeDocument.Document);
                        activeDocument.Selection.SetElementIds(new List<ElementId>());
                        activeDocument.RequestViewChange(view);
                        DemoBoosterExercise.Begin(uiApp);
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d’aide", "Bulbasaur needs help"), UiLanguage.T("Préparation MEP Booster impossible : ", "Unable to prepare MEP Booster: ") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "excel")
                {
                    try
                    {
                        ViewSchedule schedule = DemoProjectBuilder.EnsureExcelScene(activeDocument.Document);
                        DemoExcelExercise.Begin(activeDocument.Document, schedule);
                        if (activeDocument.ActiveView.Id != schedule.Id)
                            activeDocument.RequestViewChange(schedule);
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                            UiLanguage.T("Préparation de Gestion Excel impossible : ", "Unable to prepare Excel Manager: ") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (selectedChoice == "pipe-calculation")
                    DemoCalculationExercise.Begin(activeDocument.Document);
                if (selectedChoice == "organizer")
                {
                    try
                    {
                        DemoProjectBuilder.EnsureOrganizerScene(activeDocument.Document);
                        DemoProjectBuilder.ResetOrganizerViewOrientation(activeDocument.Document);
                        DemoOrganizerExercise.Begin(activeDocument.Document);
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                            UiLanguage.T("Préparation d'Organisateur impossible : ", "Unable to prepare Organizer: ") + ex.Message);
                        return Result.Failed;
                    }
                }
                if (DemoTourCatalog.Views.TryGetValue(selectedChoice, out string viewName))
                {
                    View tourView = new FilteredElementCollector(activeDocument.Document)
                        .OfClass(typeof(View3D)).Cast<View3D>()
                        .FirstOrDefault(view => view.Name == viewName);
                    if (tourView == null && (selectedChoice == "pipe-calculation" ||
                        selectedChoice == "organizer" || selectedChoice == "view-template"))
                    {
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre cherche la bonne vue", "Bulbasaur is looking for the right view"),
                            UiLanguage.T("Cette maquette ne contient pas encore la scène de ce parcours. Crée une nouvelle maquette de formation avec le bouton dédié.", "This model does not contain the scene for this walkthrough yet. Create a new training model with the dedicated button."));
                        return Result.Cancelled;
                    }
                    if (tourView != null && activeDocument.ActiveView.Id != tourView.Id)
                    {
                        if (selectedChoice == "view-template") activeDocument.ActiveView = tourView;
                        else activeDocument.RequestViewChange(tourView);
                        if (selectedChoice == "pipe-calculation" || selectedChoice == "organizer")
                        {
                            DemoTourPendingSelection.Begin(activeDocument.Document, tourView.Id, selectedChoice);
                            return Result.Succeeded;
                        }
                    }
                }
                if (selectedChoice == "pipe-calculation" || selectedChoice == "organizer")
                {
                    return DemoTourPendingSelection.PrepareNow(uiApp, selectedChoice)
                        ? Result.Succeeded : Result.Cancelled;
                }
                if (selectedChoice == "view-template")
                {
                    try { DemoViewTemplateGuide.Begin(activeDocument.Document); }
                    catch (Exception ex) { message = ex.ToString(); DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Gabarit de vue", "View Template"), UiLanguage.T("Préparation impossible : ", "Unable to prepare: ") + ex.Message); return Result.Failed; }
                    DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bravo ! Gabarit de vue", "Well done! View Template"),
                        UiLanguage.T("Observe la vue source : canalisations et parkings rouges, traits épais et pointillés. Clique sur Gabarit de vue : les choix à suivre seront encadrés en vert. Après l’export, nous ouvrirons une cible bleue pour voir la différence.", "Look at the source view: red pipes and parking spaces, thick dashed lines. Click View Template: the choices to follow will be outlined in green. After the export, we will open a blue target to see the difference."));
                }
                if (selectedChoice == "history")
                {
                    try
                    {
                        int removed = DemoHistoryScene.Prepare(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre prépare l'enquête", "Bulbasaur is preparing the investigation"), removed > 0
                            ? removed + UiLanguage.T(" objets ont été supprimés, puis le troisième est passé de 4 à 2 chaises. Fais réapparaître les deux objets, puis rétablis les anciennes valeurs du témoin avec Qui a fait ça.", " elements were deleted, then the third went from 4 to 2 chairs. Bring back both elements, then restore the reference element's previous values with Who Did That.")
                            : UiLanguage.T("Les suppressions sont déjà préparées et les paramètres du témoin ont été modifiés. Retrouve les deux suppressions, puis restaure les anciennes valeurs du témoin.", "The deletions are already prepared and the reference element's parameters have been changed. Find both deletions, then restore the reference element's previous values."));
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                            UiLanguage.T("Préparation de l'exercice impossible : ", "Unable to prepare the exercise: ") + ex.Message);
                        return Result.Failed;
                    }
                }
                Couleur.AppearanceOnboarding.StartIntro(uiApp.MainWindowHandle, selectedChoice);
            }
            return Result.Succeeded;
        }
    }

    internal static class DemoTourPendingSelection
    {
        private static string _documentPath;
        private static ElementId _viewId;
        private static string _tourId;
        private static DateTime _deadlineUtc;

        internal static void Begin(Document document, ElementId viewId, string tourId)
        {
            _documentPath = document.PathName;
            _viewId = viewId;
            _tourId = tourId;
            _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }

        internal static bool ProcessIdling(UIApplication app)
        {
            if (_tourId == null) return false;
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (uiDocument == null || !string.Equals(uiDocument.Document.PathName,
                _documentPath, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return false;
            }
            if (!uiDocument.ActiveView.Id.Equals(_viewId))
            {
                if (DateTime.UtcNow < _deadlineUtc) return true;
                Clear();
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre attend la vue", "Bulbasaur is waiting for the view"),
                    UiLanguage.T("Revit n'a pas activé la vue de formation. Ouvre-la dans l'arborescence, puis relance ce parcours.", "Revit did not activate the training view. Open it in the Project Browser, then restart this walkthrough."));
                return true;
            }
            string tourId = _tourId;
            Clear();
            PrepareNow(app, tourId);
            return true;
        }

        internal static bool PrepareNow(UIApplication app, string tourId)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            string marker = DemoProjectBuilder.DemoPrefix +
                (tourId == "clash-3d" ? "CLASH_" : tourId == "organizer" ? "ORGANISATEUR_" : "CALCUL_");
            var ids = new FilteredElementCollector(uiDocument.Document)
                .WhereElementIsNotElementType()
                .Where(element => (tourId != "clash-3d" || element is Autodesk.Revit.DB.Plumbing.Pipe) && (element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(marker, StringComparison.Ordinal))
                .Select(element => element.Id).ToList();
            if (ids.Count == 0)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre cherche la scène", "Bulbasaur is looking for the scene"),
                    UiLanguage.T("Les éléments de cet exercice manquent. Crée une nouvelle maquette de formation avec le bouton « Créer et ouvrir la maquette ».", "The elements for this exercise are missing. Create a new training model with the “Create and open the model” button."));
                return false;
            }
            uiDocument.Selection.SetElementIds(ids);
            if (tourId == "organizer")
                try { uiDocument.ShowElements(ids); } catch { /* La sélection reste active. */ }
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bravo ! Sélection prête", "Well done! Selection ready"),
                ids.Count + (tourId == "organizer"
                    ? UiLanguage.T(" places sur deux niveaux sont sélectionnées. Suis Bulbizarre jusqu'au bouton Organisateur, puis teste le tri par niveau.", " spaces on two levels are selected. Follow Bulbasaur to the Organizer button, then try sorting by level.")
                    : UiLanguage.T(" éléments de la vue sont sélectionnés. Suis Bulbizarre jusqu'au bouton, puis dans la fenêtre de la commande.", " elements in the view are selected. Follow Bulbasaur to the button, then into the command window.")));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, tourId);
            return true;
        }

        private static void Clear()
        {
            _documentPath = null;
            _viewId = null;
            _tourId = null;
            _deadlineUtc = DateTime.MinValue;
        }
    }

    internal static class DemoCalculationExercise
    {
        private static string _documentPath;
        private static bool _active;

        internal static void Begin(Document document)
        {
            DemoProjectBuilder.EnsureCalculationValves(document);
            _documentPath = document.PathName;
            _active = true;
        }

        internal static void Verify(UIApplication app, ICollection<ElementId> selectedIds,
            bool includeDucts, bool exportToExcel, string excelFilePath,
            Dictionary<double, double> pipeLengths,
            Dictionary<string, double> ductLengths, Dictionary<string, double> ductFittingLengths,
            Dictionary<string, int> elbowCounts, Dictionary<string, int> pipeAccessoryCounts)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (!_active || uiDocument == null ||
                !string.Equals(uiDocument.Document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase))
                return;

            var demoIds = DemoExerciseElements.Find(uiDocument.Document, "CALCUL_");
            bool completeSelection = demoIds.Count >= 12 &&
                demoIds.All(id => selectedIds.Contains(id));
            bool bothDiameters = pipeLengths.Any(pair => Math.Abs(pair.Key - 100) < 1 && pair.Value > 0) &&
                pipeLengths.Any(pair => Math.Abs(pair.Key - 50) < 1 && pair.Value > 0);
            int elbowCount = elbowCounts.Values.Sum();
            int valveCount = pipeAccessoryCounts.Where(pair => pair.Key == "Vanne papillon - 50-300 mm")
                .Sum(pair => pair.Value);
            if (!completeSelection || !bothDiameters || elbowCount < 5 || valveCount < 2)
            {
                Retry(app, uiDocument, UiLanguage.T("Le résultat doit contenir les deux diamètres, leurs cinq coudes et les deux vannes papillon. Bulbizarre resélectionne tous les éléments pour réessayer.", "The result must contain both diameters, their five elbows and the two butterfly valves. Bulbasaur is reselecting all elements so you can try again."));
                return;
            }

            double ductTotal = ductLengths.Values.Sum();
            double ductFittingsTotal = ductFittingLengths.Values.Sum();
            if (!includeDucts || ductTotal <= 0 || ductFittingLengths.Count == 0)
            {
                Retry(app, uiDocument, UiLanguage.T("Le résultat doit contenir les gaines et leurs raccords. Coche « Inclure les gaines », puis relance le calcul.", "The result must contain the ducts and their fittings. Check “Include ducts”, then run the calculation again."));
                return;
            }
            if (!exportToExcel || string.IsNullOrEmpty(excelFilePath) ||
                !System.IO.File.Exists(excelFilePath))
            {
                Retry(app, uiDocument, UiLanguage.T("Le fichier Excel manque. Garde « Exporter les résultats vers Excel » coché, puis relance le calcul.", "The Excel file is missing. Keep “Export results to Excel” checked, then run the calculation again."));
                return;
            }
            _active = false;
            Reselect(uiDocument, demoIds);
            DemoTourCompletion.Show(app.MainWindowHandle, "pipe-calculation",
                UiLanguage.T($"Bulbizarre a vérifié les canalisations DN100 et DN50, leurs {elbowCount} coudes, les {valveCount} vannes papillon, {ductTotal:F2} m de gaine et environ {ductFittingsTotal:F2} m de raccords de gaine. Le récapitulatif Revit et le fichier Excel viennent du même calcul.\n\nFichier : {excelFilePath}", $"Bulbasaur checked the DN100 and DN50 pipes, their {elbowCount} elbows, the {valveCount} butterfly valves, {ductTotal:F2} m of duct and about {ductFittingsTotal:F2} m of duct fittings. The Revit summary and the Excel file come from the same calculation.\n\nFile: {excelFilePath}"));
        }

        private static void Retry(UIApplication app, UIDocument uiDocument, string message)
        {
            Reselect(uiDocument, DemoExerciseElements.Find(uiDocument.Document, "CALCUL_"));
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'un essai", "Bulbasaur needs another try"), message);
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "pipe-calculation");
        }

        private static void Reselect(UIDocument uiDocument, List<ElementId> ids)
        {
            if (ids.Count > 0) uiDocument.Selection.SetElementIds(ids);
        }
    }

    internal static class DemoExerciseElements
    {
        internal static List<ElementId> Find(Document document, string suffix)
        {
            string marker = DemoProjectBuilder.DemoPrefix + suffix;
            return new FilteredElementCollector(document).WhereElementIsNotElementType()
                .Where(element => (element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(marker, StringComparison.Ordinal))
                .Select(element => element.Id).ToList();
        }

    }

    internal static class DemoExcelExercise
    {
        private static string _documentPath;
        private static ElementId _scheduleId;
        private static ElementId _parkingViewId;
        private static string _workbookPath;
        private static string _fieldName;
        private static string _referenceFieldName;
        private static Dictionary<string, string> _initialNumbers;
        private static Dictionary<string, string> _references;
        private static Dictionary<string, string> _expectedNumbers;
        private static string _verifiedChanges;
        private static int _stage; // 1 export, 2 edit/import, 3 verified
        private static int _pendingAction; // 1 introduction, 2 focus the edited places
        private static DateTime _deadlineUtc;

        internal static bool IsActive(Document document) => _stage > 0 && document != null &&
            string.Equals(document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase);
        internal static bool NeedsExport(Document document) => IsActive(document) && _stage == 1;
        internal static bool NeedsImport(Document document) => IsActive(document) && _stage == 2;

        internal static void Begin(Document document, ViewSchedule schedule)
        {
            var places = DemoProjectBuilder.FindExcelParking(document);
            if (places.Count != 4)
                throw new InvalidOperationException(UiLanguage.T("Les quatre places du parcours Excel sont introuvables.", "The four spaces of the Excel walkthrough cannot be found."));
            _fieldName = DemoProjectBuilder.ExcelValueField(schedule);
            _referenceFieldName = DemoProjectBuilder.ExcelReferenceField(schedule);
            using (var tx = new Transaction(document, "BIMaestro - Synchroniser les numéros visibles"))
            {
                tx.Start();
                DemoProjectBuilder.SynchronizeExcelParkingNumbers(places, _fieldName,
                    recoverLegacyValue: true);
                tx.Commit();
            }
            _documentPath = document.PathName;
            _scheduleId = schedule.Id;
            _parkingViewId = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => view.Name == "BIMaestro - 08 Parking Excel")?.Id;
            _workbookPath = null;
            _references = places.ToDictionary(place => place.UniqueId,
                place => place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "");
            _initialNumbers = places.ToDictionary(place => place.UniqueId,
                place => DemoProjectBuilder.ExcelValueParameter(place, _fieldName)?.AsString() ?? "");
            _expectedNumbers = null;
            _verifiedChanges = null;
            _stage = 1;
            _pendingAction = 1;
            _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }

        internal static bool ProcessIdling(UIApplication app)
        {
            if (_pendingAction == 0) return false;
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (uiDocument == null || !IsActive(uiDocument.Document)) return false;
            ElementId expected = _pendingAction == 1 ? _scheduleId : _parkingViewId;
            if (expected == null || !uiDocument.ActiveView.Id.Equals(expected))
            {
                if (DateTime.UtcNow < _deadlineUtc) return true;
                _pendingAction = 0;
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre attend la bonne vue", "Bulbasaur is waiting for the right view"),
                    UiLanguage.T("La vue du parcours Excel ne s'est pas ouverte. Relance le tutoriel depuis BIMaestro.", "The Excel walkthrough view did not open. Restart the tutorial from BIMaestro."));
                return true;
            }
            int action = _pendingAction;
            _pendingAction = 0;
            if (action == 1)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bravo ! Gestion Excel", "Well done! Excel Manager"),
                    UiLanguage.T("Voici la nomenclature des quatre places. « ", "Here is the schedule of the four spaces. “") + _fieldName + UiLanguage.T(" » contient le numéro modifiable, visible sur chaque place en 3D. « ", "” contains the editable number, visible on each space in 3D. “") +
                    _referenceFieldName + UiLanguage.T(" » est la référence fixe : ne la change pas. Suis Bulbizarre jusqu'à « Gestion Excel » et choisis « Exporter ».", "” is the fixed reference: do not change it. Follow Bulbasaur to “Excel Manager” and choose “Export”."));
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
            }
            else
            {
                var ids = DemoProjectBuilder.FindExcelParking(uiDocument.Document)
                    .Select(place => place.Id).ToList();
                uiDocument.Selection.SetElementIds(ids);
                try { uiDocument.ShowElements(ids); } catch { }
                DemoTourCompletion.Show(app.MainWindowHandle, "excel",
                    UiLanguage.T("Bulbizarre a relu les quatre places dans Revit. Modifications vérifiées :\n", "Bulbasaur reread the four spaces in Revit. Verified changes:\n") + _verifiedChanges +
                    UiLanguage.T("\n\nLa référence « ", "\n\nThe reference “") + _referenceFieldName + UiLanguage.T(" » est restée identique. Le numéro modifié est aussi affiché sur les places sélectionnées dans la vue 3D.", "” stayed the same. The changed number is also shown on the selected spaces in the 3D view."));
            }
            return true;
        }

        internal static void Exported(UIApplication app, string path)
        {
            if (!NeedsExport(app.ActiveUIDocument?.Document)) return;
            _workbookPath = path;
            _stage = 2;
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bravo ! À toi dans Excel", "Well done! Your turn in Excel"),
                UiLanguage.T("Le classeur est ici :\n", "The workbook is here:\n") + path +
                UiLanguage.T("\n\nDans l'onglet « Nomenclature », modifie un ou plusieurs numéros dans « ", "\n\nIn the “Nomenclature” sheet, change one or more numbers in “") + _fieldName +
                UiLanguage.T(" » (par exemple XL-003 → XL-103). La colonne « ", "” (for example XL-003 → XL-103). The “") + _referenceFieldName +
                UiLanguage.T(" » est une référence fixe : garde-la intacte. L'onglet « Edition » reprend tes modifications par formule pour l'import ; ne touche pas à ses identifiants cachés. Enregistre et ferme le fichier, puis reviens sur « Gestion Excel » et choisis « Importer ». Bulbizarre affichera les nouveaux numéros sur les places en 3D.", "” column is a fixed reference: leave it unchanged. The “Edition” sheet picks up your changes through formulas for the import; do not touch its hidden IDs. Save and close the file, then go back to “Excel Manager” and choose “Import”. Bulbasaur will show the new numbers on the spaces in 3D."));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
        }

        internal static bool ValidateWorkbook(UIApplication app, string path,
            List<Dictionary<string, string>> rows)
        {
            Document document = app.ActiveUIDocument?.Document;
            if (!IsActive(document)) return true;
            if (_stage != 2 || !string.Equals(path, _workbookPath, StringComparison.OrdinalIgnoreCase))
                return RejectWorkbook(app, UiLanguage.T("Bulbizarre attend l'export", "Bulbasaur is waiting for the export"),
                    UiLanguage.T("Exporte d'abord la nomenclature du parcours, puis modifie et enregistre le classeur avant de l'importer.", "First export the walkthrough schedule, then edit and save the workbook before importing it."));

            // Contrôler les quatre identifiants ET le repère avant d'ouvrir une transaction.
            // Une ligne supplémentaire ou dupliquée ne doit jamais pouvoir modifier une autre place.
            if (rows == null || _references == null || _references.Count != 4 || rows.Count != 4)
                return RejectWorkbook(app, UiLanguage.T("Bulbizarre vérifie les places", "Bulbasaur is checking the spaces"),
                    UiLanguage.T("Le classeur doit contenir exactement les quatre places exportées. Repars de l'export du parcours, sans ajouter ni supprimer de ligne.", "The workbook must contain exactly the four exported spaces. Start again from the walkthrough export, without adding or deleting rows."));

            var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null || !row.TryGetValue("UniqueId", out string uid) ||
                    !_references.TryGetValue(uid ?? "", out string reference) || numbers.ContainsKey(uid))
                    return RejectWorkbook(app, UiLanguage.T("Bulbizarre vérifie les places", "Bulbasaur is checking the spaces"),
                        UiLanguage.T("Un identifiant de place est absent, inconnu ou en double dans l'onglet « Edition ». Repars du classeur exporté.", "A space ID is missing, unknown or duplicated in the “Edition” sheet. Start again from the exported workbook."));
                if (!row.TryGetValue(_referenceFieldName, out string workbookReference) ||
                    !string.Equals(workbookReference, reference, StringComparison.Ordinal))
                    return RejectWorkbook(app, UiLanguage.T("Bulbizarre protège le repère", "Bulbasaur is protecting the mark"),
                        UiLanguage.T("La colonne « ", "The “") + _referenceFieldName + UiLanguage.T(" » est une référence fixe. Restaure sa valeur d'origine dans « Nomenclature », puis enregistre le classeur avant l'import.", "” column is a fixed reference. Restore its original value in “Nomenclature”, then save the workbook before importing."));
                if (!row.TryGetValue(_fieldName, out string number))
                    return RejectWorkbook(app, UiLanguage.T("Bulbizarre cherche les numéros", "Bulbasaur is looking for the numbers"),
                        UiLanguage.T("La colonne « ", "The “") + _fieldName + UiLanguage.T(" » manque. Repars du classeur exporté par le parcours.", "” column is missing. Start again from the workbook exported by the walkthrough."));
                numbers.Add(uid, number ?? "");
            }

            bool changed = numbers.Any(pair =>
                !string.Equals(pair.Value, _initialNumbers[pair.Key], StringComparison.Ordinal));
            if (!changed)
                return RejectWorkbook(app, UiLanguage.T("Bulbizarre attend une modification", "Bulbasaur is waiting for a change"),
                    UiLanguage.T("Modifie au moins un numéro dans « Nomenclature » (par exemple XL-003 → XL-103), enregistre et ferme Excel, puis réessaie. « ", "Change at least one number in “Nomenclature” (for example XL-003 → XL-103), save and close Excel, then try again. “") + _referenceFieldName + UiLanguage.T(" » reste fixe.", "” stays fixed."));

            _expectedNumbers = numbers;
            var changes = new List<string>();
            foreach (var pair in _references.OrderBy(pair => pair.Value))
            {
                string uid = pair.Key;
                string label = pair.Value.Substring((DemoProjectBuilder.DemoPrefix + "EXCEL_").Length);
                if (_initialNumbers[uid] != numbers[uid])
                    changes.Add(UiLanguage.T("Place ", "Space ") + label + " · " + _fieldName + " : " +
                        DisplayExcelValue(_initialNumbers[uid]) + " → " + DisplayExcelValue(numbers[uid]));
            }
            _verifiedChanges = string.Join("\n", changes);
            return true;
        }

        private static string DisplayExcelValue(string value) => string.IsNullOrEmpty(value) ? UiLanguage.T("(vide)", "(empty)") : value;

        private static bool RejectWorkbook(UIApplication app, string title, string text)
        {
            DemoTourMessage.Show(app.MainWindowHandle, title, text);
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
            return false;
        }

        // L'import guidé accepte chaque numéro modifié, jamais le repère fixe.
        internal static Parameter ImportParameter(FamilyInstance place, string header)
        {
            if (place == null || !NeedsImport(place.Document)) return null;
            if (string.Equals(header, _fieldName, StringComparison.OrdinalIgnoreCase))
                return DemoProjectBuilder.ExcelValueParameter(place, _fieldName);
            return null;
        }

        internal static void Imported(UIApplication app)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (!NeedsImport(uiDocument?.Document)) return;
            var places = DemoProjectBuilder.FindExcelParking(uiDocument.Document);
            bool valid = places.Count == 4 && _expectedNumbers != null && places.All(place =>
                _expectedNumbers.TryGetValue(place.UniqueId, out string expectedNumber) &&
                string.Equals(DemoProjectBuilder.ExcelValueParameter(place, _fieldName)?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal) &&
                _references.TryGetValue(place.UniqueId, out string reference) &&
                string.Equals(place.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    reference, StringComparison.Ordinal));
            if (!valid)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre vérifie l'import", "Bulbasaur is checking the import"),
                    UiLanguage.T("L'import s'est terminé, mais Revit ne correspond pas encore aux valeurs du classeur. Vérifie les numéros et la référence fixe, puis réessaie l'import.", "The import finished, but Revit does not match the workbook values yet. Check the numbers and the fixed reference, then try the import again."));
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
                return;
            }
            try
            {
                using (var tx = new Transaction(uiDocument.Document,
                    "BIMaestro - Afficher les numéros importés sur les parkings"))
                {
                    tx.Start();
                    DemoProjectBuilder.SynchronizeExcelParkingNumbers(places, _fieldName);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre vérifie l'affichage", "Bulbasaur is checking the display"),
                    UiLanguage.T("Les valeurs du classeur sont dans Revit, mais les numéros visibles n'ont pas pu être actualisés : ", "The workbook values are in Revit, but the visible numbers could not be updated: ") +
                    ex.Message);
                return;
            }
            if (!places.All(place => _expectedNumbers.TryGetValue(place.UniqueId, out string expectedNumber) &&
                string.Equals(place.LookupParameter("CML_Numéros de place")?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal) &&
                string.Equals(place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal)))
            {
                DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bulbizarre vérifie l'affichage", "Bulbasaur is checking the display"),
                    UiLanguage.T("Le numéro importé ne correspond pas au texte affiché sur chaque place. Vérifie les paramètres des places avant de conclure l'exercice.", "The imported number does not match the text shown on each space. Check the space parameters before finishing the exercise."));
                return;
            }
            _stage = 3;
            View targetView = _parkingViewId == null ? null : uiDocument.Document.GetElement(_parkingViewId) as View;
            if (targetView != null && uiDocument.ActiveView.Id != targetView.Id)
            {
                _pendingAction = 2;
                _deadlineUtc = DateTime.UtcNow.AddSeconds(30);
                uiDocument.RequestViewChange(targetView);
            }
            else
            {
                _pendingAction = 2;
                ProcessIdling(app);
            }
        }
    }

    internal static class DemoViewTemplateGuide
    {
        private static string _documentPath;
        private static string _exportedFile;
        private static bool _waitingForImport;

        internal static string ExportedFileFor(Document document) =>
            IsCurrent(document) && _waitingForImport ? _exportedFile : null;

        internal static bool IsGuided(Document document) => IsCurrent(document);
        internal static bool IsImportStage(Document document) => IsCurrent(document) && _waitingForImport;

        internal static void Begin(Document document)
        {
            PrepareVisualComparison(document);
            _documentPath = document?.PathName;
            _exportedFile = null;
            _waitingForImport = false;
        }

        private static void PrepareVisualComparison(Document document)
        {
            var views = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(view => view.Name == "BIMaestro - 06 Gabarit source" || view.Name == "BIMaestro - 07 Gabarit cible").ToList();
            if (views.Count != 2) throw new InvalidOperationException(UiLanguage.T("Les vues source et cible du tutoriel sont introuvables.", "The tutorial's source and target views cannot be found."));
            using (var tx = new Transaction(document, "BIMaestro - Comparaison des gabarits"))
            {
                tx.Start();
                const string patternName = "BIMaestro - Formation tirets";
                var pattern = new FilteredElementCollector(document).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                    .FirstOrDefault(item => item.Name == patternName);
                if (pattern == null)
                {
                    var definition = new LinePattern(patternName);
                    definition.SetSegments(new List<LinePatternSegment> {
                        new LinePatternSegment(LinePatternSegmentType.Dash, UnitUtils.ConvertToInternalUnits(4, UnitTypeId.Millimeters)),
                        new LinePatternSegment(LinePatternSegmentType.Space, UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters)) });
                    pattern = LinePatternElement.Create(document, definition);
                }
                var solidFill = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(item => item.GetFillPattern().IsSolidFill && item.GetFillPattern().Target == FillPatternTarget.Drafting);
                foreach (View3D view in views)
                {
                    bool source = view.Name == "BIMaestro - 06 Gabarit source";
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.FlatColors;
                    var color = source ? new Autodesk.Revit.DB.Color(220, 35, 45) : new Autodesk.Revit.DB.Color(25, 95, 220);
                    var surface = source ? new Autodesk.Revit.DB.Color(255, 190, 190) : new Autodesk.Revit.DB.Color(175, 215, 255);
                    var graphics = new OverrideGraphicSettings();
                    graphics.SetProjectionLineColor(color);
                    graphics.SetCutLineColor(color);
                    graphics.SetProjectionLineWeight(source ? 5 : 2);
                    graphics.SetCutLineWeight(source ? 5 : 2);
                    graphics.SetProjectionLinePatternId(source ? pattern.Id : LinePatternElement.GetSolidPatternId());
                    if (solidFill != null)
                    {
                        graphics.SetSurfaceForegroundPatternId(solidFill.Id);
                        graphics.SetSurfaceForegroundPatternColor(surface);
                        graphics.SetSurfaceForegroundPatternVisible(true);
                    }
                    foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                        BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_Parking })
                    {
                        var id = new ElementId(category);
                        if (view.IsCategoryOverridable(id)) view.SetCategoryOverrides(id, graphics);
                    }
                }
                tx.Commit();
            }
        }

        internal static void OnCommandOpened(Document document)
        {
            if (IsCurrent(document)) Couleur.AppearanceOnboarding.ConsumeTourClick("view-template");
        }

        internal static void OnExportCompleted(UIApplication app, string path)
        {
            UIDocument uiDocument = app?.ActiveUIDocument;
            if (!IsCurrent(uiDocument?.Document) || _waitingForImport) return;
            View3D target = new FilteredElementCollector(uiDocument.Document).OfClass(typeof(View3D))
                .Cast<View3D>().FirstOrDefault(view => view.Name == "BIMaestro - 07 Gabarit cible");
            if (target == null) return;
            uiDocument.ActiveView = target;
            _exportedFile = path;
            _waitingForImport = true;
            DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Bravo ! Export réussi", "Well done! Export succeeded"),
                UiLanguage.T("La vue cible est maintenant bleue, avec des traits continus fins. Compare-la à la source rouge et pointillée. Relance « Gabarit de vue », puis choisis l’import encadré en vert. Le fichier exporté sera prérempli :\n", "The target view is now blue, with thin solid lines. Compare it with the red, dashed source. Run “View Template” again, then choose the import outlined in green. The exported file will be prefilled:\n") +
                path + UiLanguage.T("\n\nChoisis ensuite « Créer / mettre à jour un vrai gabarit nommé ».", "\n\nThen choose “Create / update a real named template”."));
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "view-template");
        }

        internal static void OnImportCompleted(Document document, IntPtr owner)
        {
            if (!IsCurrent(document) || !_waitingForImport) return;
            var target = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => view.Name == "BIMaestro - 07 Gabarit cible");
            View template = target == null ? null : document.GetElement(target.ViewTemplateId) as View;
            var settings = template?.GetCategoryOverrides(new ElementId(BuiltInCategory.OST_PipeCurves));
            var color = settings?.ProjectionLineColor;
            if (template == null || color == null || !color.IsValid || color.Red != 220 || color.Green != 35 || color.Blue != 45 ||
                settings.ProjectionLineWeight != 5 || settings.ProjectionLinePatternId == LinePatternElement.GetSolidPatternId())
            {
                DemoTourMessage.Show(owner, UiLanguage.T("Vérifie le gabarit importé", "Check the imported template"), UiLanguage.T("La cible doit recevoir un vrai gabarit avec les traits rouges, épais et pointillés de la source. Relance l’import du fichier de cet exercice et choisis l’option encadrée en vert.", "The target must receive a real template with the source's thick, red, dashed lines. Run the import of this exercise's file again and choose the option outlined in green."));
                return;
            }
            using (var tx = new Transaction(document, "BIMaestro - Afficher le gabarit importé"))
            {
                tx.Start();
                foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                    BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_Parking })
                {
                    var id = new ElementId(category);
                    if (target.IsCategoryOverridable(id)) target.SetCategoryOverrides(id, new OverrideGraphicSettings());
                }
                tx.Commit();
            }
            new UIDocument(document).RefreshActiveView();
            _documentPath = null;
            _exportedFile = null;
            _waitingForImport = false;
            DemoTourCompletion.Show(owner, "view-template",
                UiLanguage.T("La cible doit maintenant afficher les couleurs rouges et les traits pointillés épais de la source, au lieu du bleu et des traits continus. Dans Propriétés, retrouve le gabarit affecté. Compare les vues 06 et 07 pour constater le transfert.", "The target should now show the source's red colors and thick dashed lines instead of blue and solid lines. In Properties, find the assigned template. Compare views 06 and 07 to see the transfer."));
        }

        private static bool IsCurrent(Document document) => document != null &&
            !string.IsNullOrWhiteSpace(_documentPath) &&
            string.Equals(document.PathName, _documentPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static class DemoTrainingInvitation
    {
        private static string _pendingPath;
        private static DateTime _readyAfterUtc;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        internal static void OnDocumentOpened(Document document)
        {
            string path = document?.PathName;
            if (!DemoProjectBuilder.IsLearningDocument(document))
                return;
            _pendingPath = path;
            _readyAfterUtc = DateTime.UtcNow.AddMilliseconds(600);
        }

        // Return true while this invitation owns the active learning model, so other
        // first-run prompts wait until the learner has made a choice.
        internal static bool ProcessIdling(UIApplication uiApp)
        {
            if (DemoTourPendingSelection.ProcessIdling(uiApp)) return true;
            if (DemoTourCompletion.ProcessIdling(uiApp)) return true;
            if (DemoExcelExercise.ProcessIdling(uiApp)) return true;
            if (string.IsNullOrEmpty(_pendingPath) || uiApp?.ActiveUIDocument?.Document == null)
                return false;
            string activePath = uiApp.ActiveUIDocument.Document.PathName;
            if (!string.Equals(activePath, _pendingPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (DateTime.UtcNow < _readyAfterUtc || GetForegroundWindow() != uiApp.MainWindowHandle ||
                BIMaestro.Welcome.WelcomeManager.HasVisibleBIMaestroWindow())
                return true;

            _pendingPath = null;
            var invitation = new DemoTrainingChoiceWindow();
            new WindowInteropHelper(invitation).Owner = uiApp.MainWindowHandle;
            invitation.ShowDialog();
            if (string.IsNullOrEmpty(invitation.SelectedTour)) return true;
            try
            {
                string message = null;
                DemoToursCommand.RunChoice(uiApp, invitation.SelectedTour, ref message);
            }
            catch (Exception ex)
            {
                DemoTourMessage.Show(uiApp.MainWindowHandle, UiLanguage.T("Bulbizarre a besoin d'aide", "Bulbasaur needs help"),
                    UiLanguage.T("Le tutoriel n'a pas pu démarrer : ", "The tutorial could not start: ") + ex.Message);
            }
            return true;
        }
    }

    internal sealed class DemoTrainingChoiceWindow : DemoDiscoveryWindow
    {
        internal DemoTrainingChoiceWindow() { Title = UiLanguage.T("BIMaestro · Choisir un tutoriel", "BIMaestro · Choose a tutorial"); }
    }

    internal static class DemoTourService
    {
        private static readonly Dictionary<Window, DemoWindowGuide> ActiveGuides = new Dictionary<Window, DemoWindowGuide>();
        private static FamilyRibbonTutorialGuide _familyRibbonGuide;
        internal static bool TutorialFavoritesSelected { get; private set; }
        private static Famille.Collection _familyTutorialFavorites;
        private static List<string> _familyTutorialRecents;
        private static Autodesk.Revit.ApplicationServices.Application _placementApplication;
        private static EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> _placementWatcher;

        internal static List<string> GetTutorialRecentFamiliesForRosace()
            => _familyRibbonGuide == null || _familyTutorialRecents == null
                ? null : new List<string>(_familyTutorialRecents);

        internal static bool IsTutorialFamilyForRosace(string path)
            => _familyRibbonGuide != null && !string.IsNullOrWhiteSpace(path) &&
                ((_familyTutorialRecents?.Contains(path, StringComparer.OrdinalIgnoreCase) ?? false) ||
                 (_familyTutorialFavorites?.Paths.Contains(path, StringComparer.OrdinalIgnoreCase) ?? false));

        private static void StopTutorialPlacementWatch()
        {
            if (_placementApplication != null && _placementWatcher != null)
                _placementApplication.DocumentChanged -= _placementWatcher;
            _placementWatcher = null;
            _placementApplication = null;
        }

        internal static void WatchTutorialFamilyPlacement(Document document, FamilySymbol symbol)
        {
            StopTutorialPlacementWatch();
            if (_familyRibbonGuide == null || document == null || symbol == null) return;
            ElementId symbolId = symbol.Id;
            _placementApplication = document.Application;
            _placementWatcher = (sender, args) =>
            {
                try
                {
                    Document changed = args.GetDocument();
                    if (!ReferenceEquals(changed, document) && !changed.Equals(document)) return;
                    bool placed = args.GetAddedElementIds().Any(id =>
                        changed.GetElement(id) is FamilyInstance instance &&
                        instance.Symbol.Id.Equals(symbolId));
                    if (!placed) return;
                    StopTutorialPlacementWatch();
                    ReportExternalAction("radial-tutorial-chaise-placed");
                }
                catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                {
                    StopTutorialPlacementWatch();
                }
            };
            _placementApplication.DocumentChanged += _placementWatcher;
        }

        internal static bool AttachIfRequested(string id, Window window)
        {
            if (!Couleur.AppearanceOnboarding.ConsumeTourClick(id)) return false;
            return StartInWindowCore(id, window, preparedExercise: true);
        }

        // A TUTO button can restart its guide in an already open dialog. The
        // ribbon onboarding uses the same path after its command has opened it.
        internal static bool StartInWindow(string id, Window window)
            => StartInWindowCore(id, window, preparedExercise: false);

        private static bool StartInWindowCore(string id, Window window, bool preparedExercise)
        {
            if (window?.Content is not Grid) return false;
            if (!DemoTourCatalog.Steps.TryGetValue(id, out DemoStep[] steps)) return false;
            if (id == "family-browser") _familyRibbonGuide?.Close();
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide previous))
            {
                if (previous.TourId == id && previous.IsPreparedExercise)
                    preparedExercise = true;
                TutorialAnalytics.End(previous.TourId, "interrupted");
                previous.Close();
            }
            if (id == "family-browser" && window is Famille.FamilyBrowserWindow browser &&
                !browser.BeginTutorialCatalog()) return false;
            if (!preparedExercise && DemoTourCatalog.StandaloneSteps.TryGetValue(id, out DemoStep[] standalone))
                steps = standalone;
            if (id == "organizer" && preparedExercise && DemoOrganizerExercise.IsRotatedPass)
                steps = DemoTourCatalog.OrganizerRotatedSteps;
            if (id == "organizer" && preparedExercise && DemoOrganizerExercise.IsSheetPass)
                steps = new[] {
                    new DemoStep(UiLanguage.T("Les feuilles sélectionnées", "The selected sheets"), UiLanguage.T("Les trois feuilles d’essai A, B et C seront traitées. La feuille témoin 6 doit rester inchangée.", "The three test sheets A, B and C will be processed. Reference sheet 6 must stay unchanged."), "OrganizerHeaderTitle", true),
                    new DemoStep(UiLanguage.T("Numéro de feuille", "Sheet Number"), UiLanguage.T("Choisis Numéro de feuille. Garde le préfixe et le suffixe vides, le format 1,2,3 et le départ à 1. Trier par niveau doit rester décoché.", "Choose Sheet Number. Keep the prefix and suffix empty, the 1,2,3 format and the start at 1. Sort by level must stay unchecked."), "ParameterComboBox", true),
                    new DemoStep(UiLanguage.T("Renuméroter les feuilles", "Renumber the sheets"), UiLanguage.T("Clique sur Renommer : les feuilles 3, 4 et 5 deviennent 1, 2 et 3, dans l’ordre de leurs anciens numéros. Bulbizarre vérifiera aussi la feuille témoin.", "Click Rename: sheets 3, 4 and 5 become 1, 2 and 3, in the order of their old numbers. Bulbasaur will also check the reference sheet."), "DemoRenameButton")
                };
            if (id == "pipe-calculation")
            {
                window.Width = Math.Max(window.Width, 760);
                window.Height = Math.Max(window.Height, 520);
            }
            if (id == "clash-3d" && window is Analyse.SmartCheckWindow clash)
                clash.ConfigureTutorial(preparedExercise);
            ActiveGuides[window] = new DemoWindowGuide(window, steps, id, preparedExercise);
            return true;
        }

        internal static void ObserveHistoryPreview(Window historyWindow, IntPtr owner)
        {
            if (!ActiveGuides.TryGetValue(historyWindow, out DemoWindowGuide guide)) return;
            bool detailed = guide.WaitingFor("history-preview-detailed-observed");
            if (!detailed && !guide.WaitingFor("history-preview-simple-observed")) return;
            string observedAction = detailed ? "history-preview-detailed-observed" : "history-preview-simple-observed";
            historyWindow.Hide();
            var card = new Window { Title = UiLanguage.T("BIMaestro — Observer l’aperçu", "BIMaestro — Look at the preview"), Width = 390,
                SizeToContent = SizeToContent.Height, ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.White,
                Left = SystemParameters.WorkArea.Right - 420, Top = SystemParameters.WorkArea.Bottom - 260 };
            new WindowInteropHelper(card).Owner = owner;
            var panel = new StackPanel { Margin = new Thickness(18) };
            card.Content = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2), Child = panel };
            panel.Children.Add(new TextBlock { Text = detailed ? UiLanguage.T("Aperçu Détaillé", "Detailed preview") : UiLanguage.T("Aperçu Simple", "Simple preview"), FontSize = 18, Foreground = DemoTourPalette.Accent });
            panel.Children.Add(new TextBlock { Text = detailed
                ? UiLanguage.T("Compare les tables et les chaises rouges aux volumes du mode Simple. Détaillé peut ralentir le chargement et solliciter davantage le PC ; Simple a un impact généralement imperceptible. Après confirmation, le tuto repassera en Simple. Ces objets restent des aperçus.", "Compare the red tables and chairs with the Simple mode volumes. Detailed can slow down loading and put more load on the PC; Simple usually has an unnoticeable impact. After you confirm, the tutorial will switch back to Simple. These elements are only previews.")
                : UiLanguage.T("Observe les volumes rouges : ils indiquent l’emplacement et l’encombrement des meubles supprimés. Simple est rapide et léger. Nous allons ensuite les comparer au mode Détaillé.", "Look at the red volumes: they show the location and footprint of the deleted furniture. Simple is fast and light. Next we will compare them with Detailed mode."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var seen = new Button { Content = UiLanguage.T("J’ai vu l’aperçu · Continuer", "I have seen the preview · Continue"), Padding = new Thickness(10),
                Background = DemoTourPalette.Accent, Foreground = Brushes.White };
            panel.Children.Add(seen);
            bool confirmed = false;
            seen.Click += (_, __) => { confirmed = true; card.Close(); };
            card.Closed += (_, __) => {
                if (!ActiveGuides.ContainsKey(historyWindow)) return;
                historyWindow.Show(); historyWindow.Activate();
                if (confirmed)
                {
                    if (detailed && historyWindow is Analyse.ElementHistoryWindow) Analyse.ElementHistoryTracker.CaptureDetailedDeletedMesh = false;
                    ReportAction(historyWindow, observedAction);
                }
            };
            historyWindow.Closed += (_, __) => card.Close();
            card.Show();
        }

        internal static Window ObserveHistoryResult(Window historyWindow, IntPtr owner, string heading, string text, Action confirmed)
        {
            if (!IsActive(historyWindow)) return null;
            historyWindow.Hide();
            var card = new Window { Title = UiLanguage.T("BIMaestro — Observer le résultat", "BIMaestro — Look at the result"), Width = 390,
                SizeToContent = SizeToContent.Height, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.White, Left = SystemParameters.WorkArea.Right - 420, Top = SystemParameters.WorkArea.Bottom - 250 };
            new WindowInteropHelper(card).Owner = owner;
            var body = new StackPanel { Margin = new Thickness(18) };
            card.Content = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2), Child = body };
            body.Children.Add(new TextBlock { Text = heading, FontSize = 18, Foreground = DemoTourPalette.Accent, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var seen = new Button { Content = UiLanguage.T("J’ai vu le résultat · Continuer", "I have seen the result · Continue"), Padding = new Thickness(10), Background = DemoTourPalette.Accent, Foreground = Brushes.White };
            body.Children.Add(seen);
            bool accepted = false;
            seen.Click += (_, __) => { accepted = true; card.Close(); };
            card.Closed += (_, __) => {
                if (!IsActive(historyWindow)) return;
                historyWindow.Show(); historyWindow.Activate();
                if (accepted) confirmed();
            };
            historyWindow.Closed += (_, __) => card.Close();
            card.Show();
            return card;
        }

        internal static void ReportAction(Window window, string action)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteAction(action);
        }

        // Revit ribbon commands are outside the browser WPF tree. Keep the
        // browser guide alive while the learner assigns and tries a shortcut.
        internal static void ReportExternalAction(string action)
        {
            if (action == "radial-tutorial-favorites-selected") TutorialFavoritesSelected = true;
            _familyRibbonGuide?.CompleteAction(action);
            foreach (var guide in ActiveGuides.Values.Where(g => g.TourId == "family-browser").ToArray())
                guide.CompleteAction(action);
        }

        internal static Famille.Collection GetTutorialFavoritesForRosace()
        {
            if (_familyRibbonGuide == null || _familyTutorialFavorites == null) return null;
            return new Famille.Collection
            {
                Id = _familyTutorialFavorites.Id,
                Name = _familyTutorialFavorites.Name,
                Paths = new List<string>(_familyTutorialFavorites.Paths)
            };
        }

        internal static bool IsTutorialChaiseForRosace(string familyPath)
        {
            if (_familyRibbonGuide == null || _familyTutorialFavorites?.Paths == null ||
                string.IsNullOrWhiteSpace(familyPath)) return false;
            string catalogPart = System.IO.Path.DirectorySeparatorChar + "NavigateurFamilles" +
                System.IO.Path.DirectorySeparatorChar + "Familles" +
                System.IO.Path.DirectorySeparatorChar;
            return familyPath.IndexOf(catalogPart, StringComparison.OrdinalIgnoreCase) >= 0 &&
                System.IO.Path.GetFileNameWithoutExtension(familyPath)
                    .IndexOf("chaise", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _familyTutorialFavorites.Paths.Any(path =>
                    string.Equals(path, familyPath, StringComparison.OrdinalIgnoreCase));
        }

        private static void ContinueFamilyGuideInRevit(DemoStep[] steps, Famille.FamilyBrowserWindow browser)
        {
            IntPtr owner = Famille.FamilyBrowserCommand.uiapp?.MainWindowHandle ?? IntPtr.Zero;
            if (owner == IntPtr.Zero) return;
            _familyRibbonGuide?.Close();
            TutorialFavoritesSelected = false;
            _familyTutorialFavorites = browser.TryGetTutorialFavoritesForRosace();
            _familyTutorialRecents = browser.GetTutorialRecentFamilyPaths();
            int ribbonStartIndex = Array.FindIndex(steps, step => step.Target == "RevitFamilySplit");
            if (ribbonStartIndex < 0) return;
            var guide = new FamilyRibbonTutorialGuide(owner, steps, ribbonStartIndex, () =>
            {
                StopTutorialPlacementWatch();
                _familyTutorialFavorites = null;
                _familyTutorialRecents = null;
                _familyRibbonGuide = null;
            });
            _familyRibbonGuide = guide;
            // The browser keeps the exercise favourites only in memory. Snapshot
            // them first, then restore the learner's paths before closing it.
            browser.EndTutorialCatalog();
            browser.Close();
            guide.Show();
        }

        internal static void ReportRestoration(Window window, bool furnitureRestored)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteRestoration(furnitureRestored);
        }

        internal static void ReportParameterRestoration(Window window, bool exerciseRestored)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteParameterRestoration(exerciseRestored);
        }

        internal static bool IsActive(Window window) => ActiveGuides.ContainsKey(window);

        private sealed class DemoWindowGuide
        {
            internal readonly string TourId;
            internal readonly bool IsPreparedExercise;
            private readonly Window _window;
            private readonly DemoStep[] _steps;
            private readonly Grid _root;
            private readonly Border _card;
            private readonly TextBlock _title, _text;
            private readonly Button _previous, _next;
            private AdornerLayer _layer;
            private TargetAdorner _adorner;
            private Action _detachAction;
            private int _index;
            private bool _completed;
            private bool _windowClosed, _closed;

            internal DemoWindowGuide(Window window, DemoStep[] steps, string tourId, bool preparedExercise)
            {
                TourId = tourId;
                IsPreparedExercise = preparedExercise;
                _window = window; _steps = steps;
                _root = window.Content as Grid;
                if (_root == null) return;
                _card = new Border
                {
                    Width = 320, Padding = new Thickness(14), Margin = new Thickness(12),
                    CornerRadius = new CornerRadius(12), Background = Brushes.White,
                    BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetRowSpan(_card, Math.Max(1, _root.RowDefinitions.Count));
                Grid.SetColumnSpan(_card, Math.Max(1, _root.ColumnDefinitions.Count));
                Panel.SetZIndex(_card, 1000);
                if (tourId == "clash-3d") Grid.SetColumn(_card, 1);
                var stack = new StackPanel(); _card.Child = stack;
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
                _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15,
                    Width = 245, TextWrapping = TextWrapping.Wrap };
                heading.Children.Add(_title); stack.Children.Add(heading);
                _text = new TextBlock { Margin = new Thickness(0, 9, 0, 12), TextWrapping = TextWrapping.Wrap };
                var textScroll = new ScrollViewer { Content = _text,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 280 };
                stack.Children.Add(textScroll);
                window.SizeChanged += (_, __) => textScroll.MaxHeight = Math.Max(80, Math.Min(280, window.ActualHeight - 200));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right };
                stack.Children.Add(buttons);
                _previous = AddButton(buttons, UiLanguage.T("Précédent", "Previous"), () => { _index--; ShowStep(); });
                _next = AddButton(buttons, UiLanguage.T("Suivant", "Next"), () =>
                {
                    if (!_completed) return;
                    if (_index + 1 == _steps.Length)
                    {
                        TutorialAnalytics.Complete(TourId);
                        Close();
                        DemoTourCompletion.Show(new WindowInteropHelper(_window).Owner, TourId,
                            UiLanguage.T("Tu as terminé ce tutoriel.", "You have finished this tutorial."), () => _window.Close());
                        return;
                    }
                    _index++; ShowStep();
                });
                AddButton(buttons, UiLanguage.T("Quitter", "Quit"), () => { TutorialAnalytics.End(TourId, "quit"); Close(); });
                _root.Children.Add(_card);
                if (window.IsLoaded)
                    window.Dispatcher.BeginInvoke(new Action(ShowStep), DispatcherPriority.Loaded);
                else
                    window.Loaded += OnLoaded;
                window.Closed += (_, __) => { _windowClosed = true; Close(); ActiveGuides.Remove(window); };
            }

            private static Button AddButton(Panel panel, string text, Action action)
            {
                var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(4, 0, 0, 0) };
                button.Click += (_, __) => action(); panel.Children.Add(button);
                return button;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _window.Loaded -= OnLoaded;
                ShowStep();
            }

            private void ShowStep()
            {
                if (_closed) return;
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_index < 0 || _index >= _steps.Length) return;
                TutorialAnalytics.Step(TourId, _index, _steps[_index].Title);
                if (TourId == "family-browser" && _steps[_index].Target == "RevitFamilySplit" &&
                    _window is Famille.FamilyBrowserWindow familyBrowser)
                {
                    ContinueFamilyGuideInRevit(_steps, familyBrowser);
                    return;
                }
                _completed = false;
                DemoStep step = _steps[_index];
                if (_window is Analyse.SmartCheckWindow clash) clash.PrepareTutorialStep(step.Target, step.CompletionEvent);
                if (_window is Famille.FamilyBrowserWindow browser)
                    browser.PrepareTutorialStep(step.Target, step.CompletionEvent);
                if (_window.FindName("ReservationFamiliesTab") is TabItem familiesTab &&
                    (step.Target == "ReservationFamiliesTab" || step.Target == "cbConfigSupport" ||
                     step.Target == "cbLoadedType" || step.Target == "btnBrowseRfa" ||
                     step.Target == "panelMapRectWall" || step.Target == "cbVerticalReference" ||
                     step.Target == "btnApplyMapping"))
                    familiesTab.IsSelected = true;
                else if (_window.FindName("ReservationSettingsTab") is TabItem settingsTab &&
                    (step.Target == "chkDefaultNorme" || step.Target == "chkDefaultDynamo"))
                    settingsTab.IsSelected = true;
                else if (_window.FindName("ReservationExecutionTab") is TabItem executionTab)
                    executionTab.IsSelected = true;
                // The reservation launch button is in the lower-right footer.
                // Keep the guide opposite it so the learner can actually click it.
                _card.HorizontalAlignment = step.Target == "DemoRunReservationButton" ||
                    step.Target == "cbVerticalReference" || step.Target == "btnApplyMapping" ||
                    (step.Target == "OkButton" && TourId != "pipe-calculation") ||
                    step.Target == "DemoRenameButton" ||
                    step.Target == "SearchMetadataAssistantButton" || step.Target == "FamilyListView" || step.Target == "GroupedFamilyListView"
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _card.VerticalAlignment = step.Target == "SearchMetadataAssistantButton" ? VerticalAlignment.Top : TourId == "pipe-calculation"
                    ? VerticalAlignment.Center
                    : TourId == "organizer" && step.Target == "SortByLevelCheckBox"
                    ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                _title.Text = $"{_index + 1}/{_steps.Length} · {step.Title}";
                if (TourId == "clash-3d")
                {
                    _card.HorizontalAlignment = HorizontalAlignment.Center;
                    _card.VerticalAlignment = VerticalAlignment.Top;
                }
                _text.Text = step.Text;
                _previous.IsEnabled = _index > 0;
                _next.Content = _index + 1 == _steps.Length ? UiLanguage.T("Terminer", "Finish") : UiLanguage.T("Suivant", "Next");
                _completed = step.IsExplanation;
                _next.IsEnabled = step.IsExplanation;
                if (_window is Analyse.SmartCheckWindow completedClash && completedClash.HasCompletedTutorialAction(step.CompletionEvent))
                { _completed = true; _next.IsEnabled = true; }
                if (step.CompletionEvent == "load-bureau-commun" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += UiLanguage.T(" Aucun projet n'est ouvert : ouvre-en un dans Revit pour essayer le chargement, ou termine ici et reviens plus tard avec le bouton TUTO.", " No project is open: open one in Revit to try loading, or finish here and come back later with the TUTO button.");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "favorite-added" &&
                    _window is Famille.FamilyBrowserWindow favoriteBrowser &&
                    favoriteBrowser.HasTutorialChaiseFavorite())
                {
                    _text.Text += UiLanguage.T(" Une chaise est déjà dans Favoris : ouvre l'onglet Favoris avec « Suivant ».", " A chair is already in Favorites: open the Favorites tab with “Next”.");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "radial-tutorial-chaise-placed" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += UiLanguage.T(" Aucun projet Revit n'est ouvert : ouvre une maquette pour essayer la rosace et placer une famille. Tu peux terminer ici et relancer TUTO plus tard.", " No Revit project is open: open a model to try the radial menu and place a family. You can finish here and run TUTO again later.");
                    _completed = true;
                    _next.IsEnabled = true;
                }
                int current = _index;
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed || _index != current) return;
                    Highlight(step.Target, current);
                    if (!step.IsExplanation && step.CompletionEvent == null)
                        WatchAction(step.Target, current);
                }), DispatcherPriority.Loaded);
            }

            internal bool WaitingFor(string action) => _index >= 0 && _index < _steps.Length &&
                _steps[_index].CompletionEvent == action && !_completed;

            internal void CompleteAction(string action)
            {
                if (_index >= 0 && _index < _steps.Length &&
                    _steps[_index].CompletionEvent == "radial-tutorial-chaise-placed" &&
                    action == "radial-opened")
                {
                    _text.Text = UiLanguage.T("✓ La rosace est ouverte. Fais un clic droit au centre, choisis « Charger une collection » > « Favoris », puis clique sur une chaise des favoris d'essai. BIMaestro lancera son placement dans Revit.", "✓ The radial menu is open. Right-click the center, choose “Load a collection” > “Favorites”, then click a chair among the test favorites. BIMaestro will start placing it in Revit.");
                    return;
                }
                if (_index >= 0 && _index < _steps.Length &&
                    string.Equals(_steps[_index].CompletionEvent, action, StringComparison.Ordinal))
                {
                    if (_steps[_index].IsExplanation && _completed)
                    {
                        if (_index + 1 < _steps.Length) { _index++; ShowStep(); }
                    }
                    else
                        CompleteStep(_index);
                }
            }

            private void WatchAction(string targetName, int expectedIndex)
            {
                if (_index != expectedIndex || !(_window.FindName(targetName) is FrameworkElement target)) return;
                if (targetName == "RestoreDeletedButton" || targetName == "RestoreParametersButton")
                    return; // Wait for a committed and verified restoration.
                if (target is RadioButton radio && (targetName == "SimpleMeshModeRadio" || targetName == "DetailedMeshModeRadio"))
                {
                    _completed = radio.IsChecked == true;
                    _next.IsEnabled = _completed;
                    RoutedEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    radio.Checked += handler;
                    _detachAction = () => radio.Checked -= handler;
                }
                else if (target is System.Windows.Controls.TextBox textBox)
                {
                    if (targetName == "PrefixTextBox" &&
                        string.Equals(textBox.Text, "PK-", StringComparison.Ordinal))
                    {
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    TextChangedEventHandler handler = (_, __) =>
                    {
                        if (targetName != "PrefixTextBox" ||
                            string.Equals(textBox.Text, "PK-", StringComparison.Ordinal))
                            CompleteStep(expectedIndex);
                    };
                    textBox.TextChanged += handler;
                    _detachAction = () => textBox.TextChanged -= handler;
                }
                else if (target is CheckBox checkBox &&
                    (targetName == "IncludeDuctsCheckBox" || targetName == "ExportToExcelCheckBox" ||
                     targetName == "SortByLevelCheckBox"))
                {
                    // A checked option already satisfies this step. Clicking it again
                    // must not force the learner to undo and redo the same choice.
                    _completed = checkBox.IsChecked == true;
                    _next.IsEnabled = _completed;
                    RoutedEventHandler checkedHandler = (_, __) => CompleteStep(expectedIndex);
                    RoutedEventHandler uncheckedHandler = (_, __) =>
                    {
                        _completed = false;
                        _next.IsEnabled = false;
                    };
                    checkBox.Checked += checkedHandler;
                    checkBox.Unchecked += uncheckedHandler;
                    _detachAction = () =>
                    {
                        checkBox.Checked -= checkedHandler;
                        checkBox.Unchecked -= uncheckedHandler;
                    };
                }
                else if (target is ButtonBase button)
                {
                    RoutedEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    button.Click += handler;
                    _detachAction = () => button.Click -= handler;
                }
                else if (target is Selector selector)
                {
                    if (TourId == "history" && targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    SelectionChangedEventHandler handler = (_, __) =>
                    {
                        string selected = selector.SelectedItem?.ToString() ?? "";
                        if ((targetName != "ActionFilterCombo" ||
                             IsDeletionAction(selected)) &&
                            (targetName != "ParameterComboBox" ||
                             selected == "CML_Numéros de place") &&
                            (targetName != "NumberFormatComboBox" ||
                             selected.StartsWith("001,002,003", StringComparison.Ordinal)))
                            CompleteStep(expectedIndex);
                    };
                    selector.SelectionChanged += handler;
                    _detachAction = () => selector.SelectionChanged -= handler;
                    if (targetName == "ActionFilterCombo" &&
                        IsDeletionAction(selector.SelectedItem?.ToString()))
                    {
                        _text.Text += UiLanguage.T(" Le filtre est déjà actif : clique sur Suivant.", " The filter is already active: click Next.");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if (targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _text.Text += UiLanguage.T(" Une carte est déjà sélectionnée : examine-la, puis clique sur Suivant.", " A card is already selected: examine it, then click Next.");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if ((targetName == "ParameterComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "") == "CML_Numéros de place") ||
                        (targetName == "NumberFormatComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "").StartsWith("001,002,003", StringComparison.Ordinal)))
                    {
                        _text.Text += UiLanguage.T(" Le bon choix est déjà actif : clique sur Suivant.", " The right choice is already active: click Next.");
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                }
                else
                {
                    MouseButtonEventHandler handler = (_, __) => CompleteStep(expectedIndex);
                    target.AddHandler(UIElement.MouseLeftButtonUpEvent, handler, true);
                    _detachAction = () => target.RemoveHandler(UIElement.MouseLeftButtonUpEvent, handler);
                }
            }

            internal void CompleteRestoration(bool furnitureRestored)
            {
                if (_index < 0 || _index >= _steps.Length || _steps[_index].Target != "RestoreDeletedButton")
                    return;
                if (TourId == "history" && IsPreparedExercise && !furnitureRestored)
                {
                    _text.Text = UiLanguage.T("Les deux tables complètes ne sont pas encore présentes. Sélectionne le cluster « Mobilier » avec les tables, puis clique sur « Restaurer les éléments ». Les chaises imbriquées ne sont pas des objets à restaurer séparément.", "The two complete tables are not there yet. Select the “Mobilier” (Furniture) cluster with the tables, then click “Restore elements”. Nested chairs are not elements to restore separately.");
                    return;
                }
                CompleteStep(_index);
            }

            internal void CompleteParameterRestoration(bool exerciseRestored)
            {
                if (_index < 0 || _index >= _steps.Length || _steps[_index].Target != "RestoreParametersButton")
                    return;
                if (!exerciseRestored)
                {
                    _text.Text = UiLanguage.T("Le témoin n’a pas encore retrouvé ses 4 chaises. Choisis la modification « Nombre chaises » de 4 à 2, puis restaure cette ligne.", "The reference element does not have its 4 chairs back yet. Choose the “Nombre chaises” change from 4 to 2, then restore that row.");
                    return;
                }
                CompleteStep(_index);
            }

            private void CompleteStep(int expectedIndex)
            {
                if (_index != expectedIndex || _completed) return;
                _completed = true;
                if (_index + 1 == _steps.Length)
                {
                    _text.Text = _steps[_index].Target == "RestoreParametersButton"
                        ? UiLanguage.T("✓ Les deux objets sont présents et le témoin a retrouvé ses 4 chaises initiales. Sélectionne-le dans Revit pour vérifier ses Propriétés. Tu as recréé des objets supprimés et rétabli des paramètres depuis leur historique.", "✓ Both elements are back and the reference element has its original 4 chairs again. Select it in Revit to check its Properties. You recreated deleted elements and restored parameters from their history.")
                        : _steps[_index].Target == "RestoreDeletedButton"
                        ? UiLanguage.T("✓ Le mobilier a réapparu. BIMaestro l'a sélectionné et cadré dans la vue : compare-le avec l'objet témoin, puis termine le parcours.", "✓ The furniture is back. BIMaestro selected it and zoomed to it in the view: compare it with the reference element, then finish the walkthrough.")
                        : _steps[_index].CompletionEvent == "load-bureau-commun"
                        ? UiLanguage.T("✓ Revit a chargé la famille et lancé son placement. Clique dans la vue pour la poser, ou appuie sur Échap si tu voulais seulement tester le chargement.", "✓ Revit loaded the family and started placing it. Click in the view to place it, or press Esc if you only wanted to test loading.")
                        : _steps[_index].CompletionEvent == "radial-tutorial-chaise-placed"
                        ? UiLanguage.T("✓ Tu as posé une chaise depuis la rosace. Appuie sur Échap pour quitter le mode placement. Ton favori d'essai et tes chemins personnels restent inchangés.", "✓ You placed a chair from the radial menu. Press Esc to exit placement mode. Your test favorite and personal paths are unchanged.")
                        : _steps[_index].CompletionEvent == "clash-export-written"
                        ? UiLanguage.T("✓ L'analyse a confirmé la disparition du conflit entre tuyaux et la conservation de la traversée du mur. Ton rapport a été enregistré. Tu peux terminer le parcours.", "✓ The analysis confirmed that the pipe-to-pipe clash is gone and the wall penetration remains. Your report has been saved. You can finish the walkthrough.")
                        : UiLanguage.T("✓ Action confirmée dans la maquette. Tu peux terminer ce parcours.", "✓ Action confirmed in the model. You can finish this walkthrough.");
                    _next.IsEnabled = true;
                }
                else if (TourId == "clash-3d") _next.IsEnabled = true;
                else
                {
                    _index++;
                    ShowStep();
                }
            }

            private DispatcherTimer _favoriteHighlightTimer;
            private void RefreshFavoriteHighlight()
            {
                if (_favoriteHighlightTimer != null) return;
                _favoriteHighlightTimer = new DispatcherTimer(DispatcherPriority.Loaded, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                _favoriteHighlightTimer.Tick += (_, __) => {
                    if (_index >= _steps.Length || _steps[_index].Target != "TutorialFavoriteStar") { _favoriteHighlightTimer.Stop(); _favoriteHighlightTimer = null; return; }
                    if (_adorner != null) _layer?.Remove(_adorner);
                    _adorner = null; _layer = null;
                    Highlight("TutorialFavoriteStar", _index);
                };
                _favoriteHighlightTimer.Start();
            }
            private void Highlight(string targetName, int expectedIndex, int attempt = 0)
            {
                if (_closed) return;
                if (_index != expectedIndex || !_window.IsVisible) return;
                if (targetName == "TutorialFavoriteStar") RefreshFavoriteHighlight();
                RemoveHighlight();
                FrameworkElement target;
                if (_window is Famille.FamilyBrowserWindow browser && targetName == "TutorialFavoriteStar")
                    target = browser.FindTutorialFavoriteStar();
                else if (_window is Famille.FamilyBrowserWindow folderBrowser && targetName == "TutorialMeetingFolderCard")
                    target = folderBrowser.FindTutorialMeetingFolderCard();
                else target = _window.FindName(targetName) as FrameworkElement;
                if (target == null || !target.IsVisible)
                {
                    if (targetName == "TutorialMeetingFolderCard" && attempt < 20)
                    {
                        var retry = new DispatcherTimer(DispatcherPriority.Loaded, _window.Dispatcher)
                            { Interval = TimeSpan.FromMilliseconds(100) };
                        retry.Tick += (_, __) =>
                        {
                            retry.Stop();
                            Highlight(targetName, expectedIndex, attempt + 1);
                        };
                        retry.Start();
                    }
                    return;
                }
                if (targetName == "TutorialFavoriteStar") RefreshFavoriteHighlight();
                target.BringIntoView();
                _layer = AdornerLayer.GetAdornerLayer(target);
                if (_layer == null) return;
                _adorner = new TargetAdorner(target);
                _layer.Add(_adorner);
            }

            private void RemoveHighlight()
            {
                if (_adorner != null) _layer?.Remove(_adorner);
                _adorner = null; _layer = null;
            }

            internal void Close()
            {
                if (_closed) return;
                _closed = true;
                if (TourId == "history" && _window is Analyse.ElementHistoryWindow) Analyse.ElementHistoryTracker.CaptureDetailedDeletedMesh = false;
                _favoriteHighlightTimer?.Stop(); _favoriteHighlightTimer = null;
                _window.Loaded -= OnLoaded;
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_root != null && _root.Children.Contains(_card)) _root.Children.Remove(_card);
                if (ActiveGuides.TryGetValue(_window, out DemoWindowGuide active) && ReferenceEquals(active, this))
                    ActiveGuides.Remove(_window);
                if (!_windowClosed && _window is Famille.FamilyBrowserWindow browser)
                    browser.EndTutorialCatalog();
                if (_window is Analyse.SmartCheckWindow clash) clash.EndTutorial();
            }
        }

        // Libellé « Suppression » / « Deletion » selon la langue de l'interface.
        private static bool IsDeletionAction(string action) =>
            !string.IsNullOrEmpty(action) &&
            (action.IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0 ||
             action.IndexOf("delet", StringComparison.OrdinalIgnoreCase) >= 0);

        private sealed class TargetAdorner : Adorner
        {
            internal TargetAdorner(UIElement target) : base(target)
            { IsHitTestVisible = false; Focusable = false; }
            protected override void OnRender(DrawingContext drawing)
            {
                var bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width > 0 && bounds.Height > 0)
                    drawing.DrawRoundedRectangle(null, new Pen(DemoTourPalette.Accent, 3),
                        new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }

    internal static class DemoTourCompletion
    {
        private static string _nextTour;
        private static readonly string[] Order = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };
        private static string[] Labels => new[] { UiLanguage.T("Auto réservation", "Auto Opening"), UiLanguage.T("Qui a fait ça ?", "Who Did That?"), UiLanguage.T("Couleurs et vues", "Colors and Views"), UiLanguage.T("Calcul des canalisations", "Pipe Calculation"), UiLanguage.T("Organisateur", "Organizer"), UiLanguage.T("Gabarit de vue", "View Template"), UiLanguage.T("Gestion Excel", "Excel Manager"), UiLanguage.T("Navigateur de familles", "Family Browser"), "MEP Booster", "Clash 3D" };
        internal static void Show(IntPtr owner, string completed, string summary, Action beforeNext = null)
        {
            TutorialAnalytics.Complete(completed);
            int index = Array.IndexOf(Order, completed);
            int next = index < 0 || index + 1 == Order.Length ? 0 : index + 1;
            bool continueTour = DemoTourMessage.Show(owner, UiLanguage.T("Tutoriel terminé", "Tutorial complete"),
                summary + UiLanguage.T("\n\nD’autres tutoriels sont disponibles. Veux-tu passer au suivant : « ", "\n\nMore tutorials are available. Do you want to go to the next one: “") + Labels[next] + UiLanguage.T(" » ?\nDurée estimée : ", "”?\nEstimated time: ") + DemoTourCatalog.EstimatedDurations[Order[next]] + ".",
                UiLanguage.T("Tutoriel suivant", "Next tutorial"), UiLanguage.T("Terminer pour l’instant", "Finish for now"), primaryButtonIsAction: true);
            if (!continueTour) return;
            beforeNext?.Invoke();
            _nextTour = Order[next];
        }
        internal static bool ProcessIdling(UIApplication app)
        {
            if (_nextTour == null) return false;
            string tour = _nextTour;
            _nextTour = null;
            string message = null;
            try { DemoToursCommand.RunChoice(app, tour, ref message); }
            catch (Exception ex) { DemoTourMessage.Show(app.MainWindowHandle, UiLanguage.T("Le tutoriel n’a pas pu démarrer", "The tutorial could not start"), ex.Message); }
            return true;
        }
    }

    internal sealed class DemoChoiceWindow : Window
    {
        internal string Choice { get; private set; }
        internal DemoChoiceWindow()
        {
            Title = UiLanguage.T("BIMaestro - Parcours guidés", "BIMaestro - Guided Tours");
            Width = 470; Height = 510; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(20) }; scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = UiLanguage.T("Apprendre BIMaestro avec Bulbizarre", "Learn BIMaestro with Bulbasaur"), FontSize = 18,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = UiLanguage.T("Découvre les commandes, puis essaie-les dans la maquette avec Bulbizarre.", "Discover the commands, then try them in the model with Bulbasaur."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            Button quick = AddChoice(stack, UiLanguage.T("Commencer la découverte · 2 min", "Start the overview · 2 min"), "overview");
            quick.FontWeight = FontWeights.SemiBold;
            quick.BorderBrush = DemoTourPalette.Accent;
            quick.BorderThickness = new Thickness(2);
            AddChoice(stack, UiLanguage.T("Créer et ouvrir la maquette de formation", "Create and open the training model"), "create");
            var detailed = new Expander { Header = UiLanguage.T("Aller directement aux exercices détaillés", "Go straight to the detailed exercises"),
                Margin = new Thickness(0, 6, 0, 12), IsExpanded = false };
            stack.Children.Add(detailed);
            var detailChoices = new StackPanel { Margin = new Thickness(8, 10, 0, 0) };
            detailed.Content = detailChoices;
            AddChoice(detailChoices, UiLanguage.T("1 · Auto réservation", "1 · Auto Opening"), "reservation");
            AddChoice(detailChoices, UiLanguage.T("2 · Qui a fait ça ?", "2 · Who Did That?"), "history");
            AddChoice(detailChoices, UiLanguage.T("3 · Couleurs et vues", "3 · Colors and Views"), "colors");
            AddChoice(detailChoices, UiLanguage.T("4 · Calcul des canalisations", "4 · Pipe Calculation"), "pipe-calculation");
            AddChoice(detailChoices, UiLanguage.T("5 · Organisateur", "5 · Organizer"), "organizer");
            AddChoice(detailChoices, UiLanguage.T("6 · Gabarit de vue", "6 · View Template"), "view-template");
            AddChoice(detailChoices, UiLanguage.T("7 · Gestion Excel", "7 · Excel Manager"), "excel");
            AddChoice(detailChoices, "9 · MEP Booster", "mep-booster");
            AddChoice(detailChoices, UiLanguage.T("8 · Navigateur de familles", "8 · Family Browser"), "family-browser");
            AddChoice(detailChoices, "10 · Clash 3D", "clash-3d");
            AddChoice(stack, UiLanguage.T("Recommencer les exercices de la maquette", "Restart the model exercises"), "reset");
        }
        private Button AddChoice(Panel panel, string label, string id)
        {
            if (DemoTourCatalog.EstimatedDurations.TryGetValue(id, out string duration))
                label += " · " + duration;
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, __) => { Choice = id; Close(); };
            panel.Children.Add(button);
            return button;
        }
    }

    internal class DemoDiscoveryWindow : Window
    {
        internal string SelectedTour { get; private set; }

        internal DemoDiscoveryWindow()
        {
            Title = UiLanguage.T("BIMaestro - Découverte rapide", "BIMaestro - Quick Overview");
            Width = 570; Height = 710; MinWidth = 480; MinHeight = 550;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = UiLanguage.T("Choisir un parcours guidé", "Choose a guided tour"), FontSize = 21,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = UiLanguage.T("Choisis un tutoriel : clique directement sur sa carte pour commencer dans la maquette de formation. Les durées sont indicatives, lecture et manipulations comprises, selon ton rythme.", "Choose a tutorial: click its card to start in the training model. Times are approximate, including reading and hands-on steps, depending on your pace."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            AddCard(stack, UiLanguage.T("1 · Auto réservation", "1 · Auto Opening"),
                UiLanguage.T("Quand un réseau traverse un mur, BIMaestro place une famille de réservation au croisement. Dans l'exercice, tu choisis toi-même la canalisation puis le mur et tu examines le résultat.", "When a system passes through a wall, BIMaestro places an opening family at the crossing. In the exercise, you pick the pipe and then the wall yourself, and review the result."));
            AddCard(stack, UiLanguage.T("2 · Qui a fait ça ?", "2 · Who Did That?"),
                UiLanguage.T("Retrouve l'auteur et le contexte d'une modification. Fais réapparaître deux meubles supprimés, puis restaure les 4 chaises du troisième à partir de l'historique.", "Find who made a change and in what context. Bring back two deleted furniture items, then restore the third one's 4 chairs from the history."));
            AddCard(stack, UiLanguage.T("3 · Couleurs et vues", "3 · Colors and Views"),
                UiLanguage.T("Personnalise l'arborescence sans renommer ni recréer les vues. Dans l'exercice, tu changes le fond et ajoutes des icônes aux dossiers Plans d'étage et Vues 3D.", "Customize the Project Browser without renaming or recreating views. In the exercise, you change the background and add icons to the Floor Plans and 3D Views folders."));
            AddCard(stack, UiLanguage.T("4 · Calcul des canalisations", "4 · Pipe Calculation"),
                UiLanguage.T("Sélectionne deux réseaux de canalisations avec coudes et une gaine. Un seul calcul affiche le récapitulatif Revit et crée le fichier Excel.", "Select two piping systems with elbows and a duct. A single calculation shows the Revit summary and creates the Excel file."));
            AddCard(stack, UiLanguage.T("5 · Organisateur", "5 · Organizer"),
                UiLanguage.T("Numérote huit places CML_Parking sur deux niveaux, d'abord par niveau puis selon l'ordre de lecture d'une vue 3D tournée de 90°.", "Number eight CML_Parking spaces on two levels, first by level, then following the reading order of a 3D view rotated by 90°."));
            AddCard(stack, UiLanguage.T("6 · Gabarit de vue", "6 · View Template"),
                UiLanguage.T("Exporte les réglages d'une vue 3D, puis importe-les dans une seconde vue pour créer un vrai gabarit Revit nommé.", "Export the settings of a 3D view, then import them into a second view to create a real named Revit view template."));
            AddCard(stack, UiLanguage.T("7 · Gestion Excel", "7 · Excel Manager"),
                UiLanguage.T("Exporte les places CML_Parking : numéro modifiable et repère fixe. Essaie par exemple XL-003 → XL-103, puis importe tes changements. Bulbizarre vérifie chaque valeur et l'affiche sur la place en 3D.", "Export the CML_Parking spaces: editable number and fixed mark. Try for example XL-003 → XL-103, then import your changes. Bulbasaur checks each value and shows it on the space in 3D."));
            AddCard(stack, UiLanguage.T("8 · Navigateur de familles", "8 · Family Browser"),
                UiLanguage.T("Explore un catalogue de 35 familles rangées en dossiers et sous-dossiers. Cherche une famille, prévisualise-la en 3D, découvre les photos automatiques et apprends à brancher ta propre bibliothèque.", "Explore a catalog of 35 families organized in folders and subfolders. Search for a family, preview it in 3D, discover automatic pictures and learn how to connect your own library."));
            AddCard(stack, "9 · MEP Booster", UiLanguage.T("Tourne une vanne, observe le résultat et copie son orientation sur d’autres vannes raccordées.", "Rotate a valve, look at the result and copy its orientation to other connected valves."));
            AddCard(stack, "10 · Clash 3D", UiLanguage.T("Analyse deux intersections, relève un tuyau d’essai, crée une réservation dans le mur puis relance l’analyse et exporte le rapport.", "Analyze two intersections, raise a test pipe, create an opening in the wall, then run the analysis again and export the report."));
            var close = new Button { Content = UiLanguage.T("Terminer pour l'instant", "Finish for now"), Padding = new Thickness(12, 7, 12, 7),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            close.Click += (_, __) => Close();
            stack.Children.Add(close);
        }

        private void AddExerciseButton(Panel parent, string label, string tourId)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, __) => { SelectedTour = tourId; Close(); };
            parent.Children.Add(button);
        }

        private void AddCard(Panel parent, string title, string description)
        {
            string[] ids = { "reservation", "history", "colors", "pipe-calculation", "organizer", "view-template", "excel", "family-browser", "mep-booster", "clash-3d" };
            string[] icons = { "Auto réservation.png", "qui à fait ça (2).png", "Couleur oui non.png", "Calcul de canalisation.png", "Organisateur.png", "Gabarit de vue simple.png", "Gestion Excel.png", "FamilyBrowser.png", "MEP Booster vanne rotation.png", "Clash 3D.png" };
            int index = int.Parse(title.Substring(0, title.IndexOf('·')).Trim()) - 1;
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var image = new Image { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
            using (var stream = typeof(DemoDiscoveryWindow).Assembly.GetManifestResourceStream(typeof(DemoDiscoveryWindow).Assembly.GetManifestResourceNames().First(name => name.EndsWith("." + icons[index], StringComparison.OrdinalIgnoreCase))))
            {
                if (stream != null) { var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap; }
            }
            row.Children.Add(image);
            var body = new StackPanel(); Grid.SetColumn(body, 1); row.Children.Add(body);
            body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = DemoTourPalette.Accent });
            body.Children.Add(new TextBlock { Text = UiLanguage.T("Durée estimée : ", "Estimated time: ") + DemoTourCatalog.EstimatedDurations[ids[index]],
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 8) });
            body.Children.Add(new TextBlock { Text = UiLanguage.T("Commencer ce tutoriel →", "Start this tutorial →"), FontWeight = FontWeights.SemiBold, Foreground = DemoTourPalette.Accent });
            var button = new Button { Content = row, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(1), Background = Brushes.White };
            button.Click += (_, __) => { SelectedTour = ids[index]; Close(); };
            parent.Children.Add(button);
        }
    }

    internal static class DemoTourMessage
    {
        internal static bool Show(IntPtr owner, string heading, string message,
            string buttonLabel = null, string learnMoreLabel = null, bool primaryButtonIsAction = false)
        {
            bool learnMore = false;
            var window = new Window
            {
                Title = UiLanguage.T("BIMaestro · Bulbizarre", "BIMaestro · Bulbasaur"),
                Width = 480,
                MinWidth = 380,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.White
            };
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = owner;

            var root = new StackPanel();
            window.Content = root;
            var header = new Border
            {
                Background = DemoTourPalette.Highlight,
                BorderBrush = DemoTourPalette.Accent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(18, 14, 18, 14)
            };
            root.Children.Add(header);
            var headingRow = new StackPanel { Orientation = Orientation.Horizontal };
            header.Child = headingRow;
            headingRow.Children.Add(new Image
            {
                Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 48,
                Height = 48,
                Margin = new Thickness(0, 0, 14, 0)
            });
            headingRow.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 365
            });
            var content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 400,
                Padding = new Thickness(20, 18, 20, 14)
            };
            root.Children.Add(content);
            content.Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 22,
                Foreground = Brushes.Black
            };
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 4, 20, 18)
            };
            root.Children.Add(footer);
            if (!string.IsNullOrWhiteSpace(learnMoreLabel))
            {
                var more = new Button
                {
                    Content = learnMoreLabel,
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(0, 0, 10, 0),
                    FontWeight = FontWeights.SemiBold,
                    Background = Brushes.White,
                    BorderBrush = DemoTourPalette.Accent,
                    BorderThickness = new Thickness(2),
                    IsCancel = primaryButtonIsAction
                };
                more.Click += (_, __) => { learnMore = !primaryButtonIsAction; window.Close(); };
                footer.Children.Add(more);
            }
            var close = new Button
            {
                Content = buttonLabel ?? UiLanguage.T("Continuer", "Continue"),
                MinWidth = 125,
                Padding = new Thickness(14, 7, 14, 7),
                FontWeight = FontWeights.SemiBold,
                Background = primaryButtonIsAction ? DemoTourPalette.Accent : DemoTourPalette.Highlight,
                Foreground = primaryButtonIsAction ? Brushes.White : Brushes.Black,
                BorderBrush = DemoTourPalette.Accent,
                BorderThickness = new Thickness(2),
                IsDefault = primaryButtonIsAction
            };
            close.Click += (_, __) => { learnMore = primaryButtonIsAction; window.Close(); };
            footer.Children.Add(close);
            window.ShowDialog();
            return learnMore;
        }
    }

    internal static class DemoTourDeepDive
    {
        internal static void Show(IntPtr owner, string tourId)
        {
            string heading;
            string[,] topics;
            switch (tourId)
            {
                case "reservation":
                    heading = UiLanguage.T("Auto résa · Comprendre les réglages", "Auto Opening · Understand the settings");
                    topics = new[,]
                    {
                        { UiLanguage.T("1. Choisir le cas", "1. Choose the case"), UiLanguage.T("Dans Familles, sélectionne le support (mur ou sol), la forme (rectangulaire ou circulaire) et l'hébergement. Chaque combinaison possède son propre type de réservation.", "In Families, select the host (wall or floor), the shape (rectangular or circular) and the hosting. Each combination has its own opening type.") },
                        { UiLanguage.T("2. Ajouter ton RFA", "2. Add your RFA"), UiLanguage.T("Si ta famille est déjà chargée dans le projet, choisis son type dans « Famille et type ». Sinon, utilise « Parcourir… » puis « Charger dans le projet ». L'import ne suffit pas : vérifie le type retenu.", "If your family is already loaded in the project, choose its type in “Family and type”. Otherwise, use “Browse…” then “Load into project”. Importing is not enough: check the selected type.") },
                        { UiLanguage.T("3. Mapper les paramètres", "3. Map the parameters"), UiLanguage.T("Associe les dimensions calculées aux paramètres existants de la famille. Pour un mur rectangulaire : longueur dans l'axe du mur, hauteur et profondeur. Une famille circulaire demande un diamètre. Enregistre ensuite cette famille pour le cas choisi.", "Map the calculated dimensions to the family's existing parameters. For a rectangular wall: length along the wall, height and depth. A circular family needs a diameter. Then save this family for the chosen case.") },
                        { UiLanguage.T("4. Ajuster le placement", "4. Adjust the placement"), UiLanguage.T("Commence avec une référence verticale automatique et un décalage de 0 mm. Si la famille est construite avec une origine décalée, corrige son placement et refais un essai sur une copie de la maquette.", "Start with an automatic vertical reference and a 0 mm offset. If the family is built with an offset origin, correct its placement and test again on a copy of the model.") },
                        { UiLanguage.T("5. Contrôler le résultat", "5. Check the result"), UiLanguage.T("Après la création, sélectionne la réservation, vérifie son insertion dans le mur et compare ses dimensions dans Propriétés. L'arrondi aux 50 mm est un réglage séparé ; Dynamo reste facultatif.", "After creation, select the opening, check how it sits in the wall and compare its dimensions in Properties. Rounding to 50 mm is a separate setting; Dynamo remains optional.") }
                    };
                    break;
                case "history":
                    heading = UiLanguage.T("Qui a fait ça ? · Lire et restaurer", "Who Did That? · Read and restore");
                    topics = new[,]
                    {
                        { UiLanguage.T("1. Réduire la recherche", "1. Narrow the search"), UiLanguage.T("Filtre par action, utilisateur, texte ou période. Si l'événement manque, élargis d'abord la période et recharge l'historique avant de conclure qu'il n'existe pas.", "Filter by action, user, text or period. If the event is missing, first widen the period and reload the history before concluding that it does not exist.") },
                        { UiLanguage.T("2. Lire la preuve", "2. Read the evidence"), UiLanguage.T("Dans Détails, compare l'auteur, la date, la catégorie et les propriétés enregistrées. Une carte ou un aperçu aide à repérer l'élément, mais ne remplace pas ces informations.", "In Details, compare the author, date, category and recorded properties. A card or preview helps locate the element but does not replace this information.") },
                        { UiLanguage.T("3. Prévisualiser", "3. Preview"), UiLanguage.T("« Visualiser » affiche l'emplacement estimé sans modifier le projet. L'aperçu détaillé dépend des familles et types encore présents ; il peut revenir à une représentation simple.", "“Visualize” shows the estimated location without changing the project. The detailed preview depends on the families and types still present; it may fall back to a simple representation.") },
                        { UiLanguage.T("4. Recréer les objets", "4. Recreate the elements"), UiLanguage.T("« Restaurer les éléments » recrée les objets supprimés. La famille, le type, le niveau et parfois l'hôte doivent être disponibles. Dans l'exercice, vérifie que les deux meubles sont réapparus.", "“Restore elements” recreates deleted elements. The family, type, level and sometimes the host must be available. In the exercise, check that both furniture items are back.") },
                        { UiLanguage.T("5. Rétablir des paramètres", "5. Restore parameters"), UiLanguage.T("Sélectionne une ligne « Modification paramètres », compare l'avant/après dans Détails, puis clique sur « Restaurer ». Cette action réapplique les anciennes valeurs enregistrées pour cette ligne sur l'élément existant. Elle ne constitue pas une annulation générale de toutes les actions du projet.", "Select a “Parameter change” row, compare before/after in Details, then click “Restore”. This reapplies the previous values recorded for that row to the existing element. It is not a general undo of all actions in the project.") }
                    };
                    break;
                default:
                    heading = UiLanguage.T("Couleurs et vues · Comprendre les règles", "Colors and Views · Understand the rules");
                    topics = new[,]
                    {
                        { UiLanguage.T("1. Panneaux et arborescence", "1. Panels and Project Browser"), UiLanguage.T("Les couleurs des panneaux BIMaestro et le style de l'arborescence sont deux réglages distincts. Active chacun dans son onglet, puis utilise l'aperçu avant d'enregistrer.", "BIMaestro panel colors and the Project Browser style are two separate settings. Enable each one in its tab, then use the preview before saving.") },
                        { UiLanguage.T("2. Fond et dossiers", "2. Background and folders"), UiLanguage.T("Le fond agit sur l'arrière-plan de l'arborescence ; les règles Dossiers colorent des dossiers existants. Elles ne créent et ne renomment aucune vue Revit.", "The background setting affects the Project Browser background; Folder rules color existing folders. They do not create or rename any Revit view.") },
                        { UiLanguage.T("3. Icônes par nom", "3. Icons by name"), UiLanguage.T("Dans Icônes, active l'affichage puis associe une image au nom exact du dossier, par exemple « Plans d'étage » ou « Vues 3D ». Si le nom diffère dans le projet, la règle ne trouve pas sa cible.", "In Icons, enable the display, then associate an image with the exact folder name, for example “Floor Plans” or “3D Views”. If the name differs in the project, the rule will not find its target.") },
                        { UiLanguage.T("4. Vérifier dans Revit", "4. Check in Revit"), UiLanguage.T("Enregistre et observe l'arborescence réelle. Si un effet manque, vérifie que la personnalisation est active, que le nom de la règle correspond au dossier et que l'icône est choisie.", "Save and look at the actual Project Browser. If an effect is missing, check that customization is enabled, that the rule name matches the folder and that an icon is selected.") }
                    };
                    break;
            }

            var window = new Window
            {
                Title = UiLanguage.T("BIMaestro · Bulbizarre approfondit", "BIMaestro · Bulbasaur goes deeper"),
                Width = 600,
                Height = 570,
                MinWidth = 480,
                MinHeight = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Brushes.White
            };
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
            var root = new DockPanel();
            window.Content = root;
            var footer = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            var close = new Button { Content = UiLanguage.T("Retour à la maquette", "Back to the model"), Padding = new Thickness(16, 8, 16, 8),
                Background = DemoTourPalette.Highlight,
                BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(2) };
            close.Click += (_, __) => window.Close();
            footer.Children.Add(close);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 16) };
            stack.Children.Add(titleRow);
            titleRow.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0) });
            titleRow.Children.Add(new TextBlock { Text = heading, FontSize = 20,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 490 });
            for (int i = 0; i < topics.GetLength(0); i++)
            {
                var card = new Border { BorderBrush = DemoTourPalette.Accent, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10), Background = Brushes.WhiteSmoke,
                    Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
                stack.Children.Add(card);
                var body = new StackPanel(); card.Child = body;
                body.Children.Add(new TextBlock { Text = topics[i, 0], FontSize = 15,
                    FontWeight = FontWeights.SemiBold });
                body.Children.Add(new TextBlock { Text = topics[i, 1], TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0), LineHeight = 21 });
            }
            window.ShowDialog();
        }
    }
}
