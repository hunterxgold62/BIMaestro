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

namespace BIMaestro.Tutorials
{
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
        internal static readonly IReadOnlyDictionary<string, string> Views = new Dictionary<string, string>
        {
            ["reservation"] = "BIMaestro - 01 Auto réservation",
            ["history"] = "BIMaestro - 02 Qui a fait ça",
            ["colors"] = "BIMaestro - 03 Couleurs et vues",
            ["pipe-calculation"] = "BIMaestro - 04 Calcul des canalisations",
            ["organizer"] = "BIMaestro - 05 Organisateur",
            ["view-template"] = "BIMaestro - 06 Gabarit source",
            ["excel"] = "BIMaestro - 08 Gestion Excel"
        };

        internal static readonly IReadOnlyDictionary<string, DemoStep[]> Steps = new Dictionary<string, DemoStep[]>
        {
            ["reservation"] = new[]
            {
                new DemoStep("Le support", "Choisis « Mur » : la réservation sera placée dans le mur traversé. « Sol » correspond à une autre configuration de famille et de dimensions.", "hostMurCard"),
                new DemoStep("La forme", "Choisis « Rectangulaire ». Cette forme détermine les paramètres de dimensions à renseigner dans la famille : longueur, hauteur et profondeur pour un mur.", "shapeRectCard"),
                new DemoStep("Le réseau", "Choisis « Canalisation ». BIMaestro calcule l'ouverture autour du réseau sélectionné ; une gaine ou un autre réseau exige le cas adapté.", "objPipeCard"),
                new DemoStep("Manuel ou automatique ?", "Le mode manuel te fait choisir le réseau et le mur : idéal pour comprendre et contrôler le résultat. Le mode automatique scanne les croisements, uniquement pour les murs. Laisse-le désactivé pour cet exercice.", "chkAutomatique", true),
                new DemoStep("Source des éléments", "« Maquette » travaille sur les éléments du projet ouvert. Les options de liens servent lorsque le réseau ou le mur vient d'une maquette liée IFC/RVT ; « Double lien » restreint les deux choix aux liens.", "chkDoubleLink", true),
                new DemoStep("Configurer sa famille", "L'onglet Familles permet d'utiliser ta propre famille RFA. Nous allons parcourir les réglages sans modifier la famille déjà prête dans cette maquette.", "ReservationFamiliesTab", true),
                new DemoStep("Choisir le bon cas", "Dans Familles, choisis Support, Forme et Hébergement. Chaque combinaison a son propre type de réservation. Le type choisi ici doit correspondre à la façon dont ta famille est construite et hébergée.", "cbConfigSupport", true),
                new DemoStep("Famille déjà chargée", "Si ton type est déjà dans le projet, sélectionne-le ici. Les Modèles génériques compatibles apparaissent en premier. Vérifie le type avant d'enregistrer le mapping.", "cbLoadedType", true),
                new DemoStep("Importer un RFA", "Si la famille manque, clique sur « Parcourir… », choisis ton fichier .RFA, puis « Charger dans le projet ». L'import est facultatif ici : la maquette de formation possède déjà sa famille.", "btnBrowseRfa", true),
                new DemoStep("Associer les dimensions", "Ces champs relient les dimensions calculées par BIMaestro aux paramètres de ta famille. Pour une réservation rectangulaire murale : longueur dans l'axe du mur, hauteur et profondeur. Les noms doivent exister dans le type choisi.", "panelMapRectWall", true),
                new DemoStep("Position verticale", "La référence verticale règle où se place la famille par rapport au croisement ; le décalage corrige un écart propre à sa construction. Commence par « Automatique » et 0 mm, puis ajuste seulement si la réservation est décalée.", "cbVerticalReference", true),
                new DemoStep("Enregistrer le mapping", "Pour une nouvelle famille, « Enregistrer cette famille » mémorise le type, les paramètres et la position pour ce cas. Ne clique pas ici pendant la démo : nous gardons la configuration fournie.", "btnApplyMapping", true),
                new DemoStep("Arrondi des dimensions", "Dans Réglages, l'arrondi aux 50 mm supérieurs transforme par exemple 232 mm en 250 mm. Il n'ajoute pas d'abord un jeu de 50 mm ; adapte ce choix à ta convention de projet.", "chkDefaultNorme", true),
                new DemoStep("Dynamo est facultatif", "Un script Dynamo peut enrichir les paramètres après la création, par exemple avec des hauteurs NGF. Il n'est pas nécessaire pour créer la réservation : laisse-le désactivé si tu n'as pas de script adapté.", "chkDefaultDynamo", true),
                new DemoStep("Revenir à l'exécution", "La famille et les réglages sont compris. Reviens à Exécution pour créer ta réservation sans toucher aux paramètres de la maquette de formation.", "ReservationExecutionTab", true),
                new DemoStep("Placer ta réservation", "Clique sur « Lancer », puis sélectionne la canalisation et le mur dans la vue 01. Après création, contrôle la position et les dimensions dans les Propriétés de Revit.", "DemoRunReservationButton")
            },
            ["pipe-calculation"] = new[]
            {
                new DemoStep("Le périmètre", "Dans la vue 04, Pikachu a sélectionné les canalisations DN100 et DN50, leurs vrais coudes, ainsi qu'une gaine avec ses coudes. Le calcul ne compte que les éléments sélectionnés. Passe à l'étape suivante avant de modifier les options.", "CalculationOptionsTitle", true),
                new DemoStep("Inclure les gaines", "Coche « Inclure les gaines » pour compter aussi la gaine rectangulaire et ses coudes. Si la case est déjà cochée, laisse-la ainsi et clique sur Suivant.", "IncludeDuctsCheckBox"),
                new DemoStep("Filtrer par système", "Le filtre limite le résultat à certains types de systèmes. Laisse-le désactivé pour voir les deux diamètres de canalisations et la gaine ensemble.", "EnableSystemTypeFilterCheckBox", true),
                new DemoStep("Voir aussi le fichier Excel", "Garde « Exporter les résultats vers Excel » coché. Après le récapitulatif dans Revit, BIMaestro créera le fichier et te proposera de l'ouvrir. Si la case est déjà cochée, clique sur Suivant.", "ExportToExcelCheckBox"),
                new DemoStep("Lancer le calcul", "Clique sur OK. Lis le récapitulatif dans Revit, puis accepte l'ouverture du fichier Excel pour examiner les mêmes résultats. Pikachu vérifiera ce calcul unique.", "OkButton")
            },
            ["organizer"] = new[]
            {
                new DemoStep("Deux niveaux à traiter", "Huit places CML_Parking sont sélectionnées : quatre sur le niveau de base et quatre sur « BIMaestro - Niveau 1 ». Organisateur modifiera uniquement ces places.", "OrganizerHeaderTitle", true),
                new DemoStep("Le bon paramètre", "Choisis « CML_Numéros de place » dans la liste : ce paramètre texte d'instance porte le numéro visible de chaque place.", "ParameterComboBox"),
                new DemoStep("Préfixe", "Saisis « PK- » : il sera ajouté avant chaque numéro.", "PrefixTextBox"),
                new DemoStep("Format", "Choisis « 001,002,003... » pour numéroter les places sur trois chiffres.", "NumberFormatComboBox"),
                new DemoStep("Hauteur de bande", "La hauteur de bande regroupe les éléments par lignes dans la vue. Garde 1 m pour distinguer les places de chaque rangée.", "BandHeightTextBox", true),
                new DemoStep("D'abord par niveau", "Coche « Trier par niveau ». Le niveau de base recevra les quatre premiers numéros, puis le niveau supérieur les quatre suivants.", "SortByLevelCheckBox"),
                new DemoStep("Premier passage", "Clique sur Renommer. Pikachu vérifiera les huit numéros visibles, puis tournera automatiquement cette vue 3D de 90° pour un second essai rapide.", "DemoRenameButton")
            },
            ["family-browser"] = new[]
            {
                new DemoStep("Ton catalogue d'essai", "Clique sur « Familles » dans la colonne de gauche. Pikachu a ouvert 35 familles de mobilier réparties en dossiers. Ce catalogue temporaire ne remplace pas les chemins que tu as enregistrés pour ta bibliothèque.", "AllFamiliesButton", completionEvent: "families-root-open"),
                new DemoStep("Entrer dans Mobilier", "Dans l'arborescence, ouvre « Mobilier ». Le guide attend que ce dossier soit réellement affiché.", "FolderTreeView", completionEvent: "folder-mobilier"),
                new DemoStep("Ouvrir Bureau", "Ouvre le dossier « Bureau ». Il contient cinq familles et un sous-dossier ; observe aussi les cartes de dossiers à droite.", "FolderTreeView", completionEvent: "folder-bureau"),
                new DemoStep("Explorer le sous-dossier", "Ouvre « Salle de réunion » dans Bureau. Ses cinq familles montrent comment conserver ton classement actuel, même à plusieurs niveaux.", "FolderTreeView", completionEvent: "folder-reunion"),
                new DemoStep("Lire les fiches", "Les cartes affichent le nom et un aperçu lorsqu'il existe. Une image PNG portant le même nom qu'une famille RFA peut être retrouvée dans un dossier miroir ; sans image, le navigateur peut utiliser une vignette native.", "FamilyListView", true),
                new DemoStep("Chercher dans le dossier", "Saisis « Table » dans la recherche. Le mode « Dossier » limite les résultats au dossier ouvert.", "SearchBox", completionEvent: "search-table"),
                new DemoStep("Étendre la recherche", "Clique sur « Tout ». BIMaestro efface la recherche « Table » et passe à l'ensemble du catalogue, y compris les autres sous-dossiers.", "SearchAllButton", completionEvent: "search-all"),
                new DemoStep("Retrouver une famille", "Saisis « Chaise ». Les résultats doivent maintenant inclure des familles rangées hors de Salle de réunion.", "SearchBox", completionEvent: "search-chaise"),
                new DemoStep("Mettre une famille en favori", "Clique sur l'étoile entourée en orange d'une carte « Chaise ». L'étoile orange signifie que la famille est dans Favoris ; un second clic l'en retire. Si une chaise est déjà étoilée, Pikachu la reconnaît sans modifier tes favoris personnels. Sinon, ton favori d'essai reste seulement en mémoire.", "TutorialFavoriteStar", completionEvent: "favorite-added"),
                new DemoStep("Retrouver ses favoris", "Ouvre l'onglet « Favoris ». La famille étoilée doit apparaître dans la collection Favoris. Dans ta vraie bibliothèque, les favoris sont enregistrés et restent disponibles à la prochaine ouverture.", "FavoritesTabItem", completionEvent: "favorites-open"),
                new DemoStep("Collections et chargement", "La liste « Collection » permet de regrouper plusieurs familles ; « Nouv. », « Ren. » et « Suppr. » gèrent ces groupes. Le bouton « Charger la collection » charge toutes ses familles dans Revit. La croix d'une carte retire seulement cette famille de la collection. La rosace peut aussi ouvrir une collection choisie, mais ses pages par défaut montrent les familles récentes et utilisées.", "CollectionCombo", true),
                new DemoStep("Revenir aux dossiers", "Rouvre l'onglet des dossiers pour poursuivre l'exercice. Le favori d'essai sera retiré automatiquement quand tu quitteras le guide.", "FoldersTabItem", completionEvent: "folders-open"),
                new DemoStep("Voir une famille en 3D", "Sur une carte de famille, clique sur le bouton « 3D », puis ferme l'aperçu pour revenir ici. Tu peux examiner le modèle avant de le charger.", "GroupedFamilyListView", completionEvent: "preview-3d"),
                new DemoStep("Ouvrir les paramètres", "Clique sur « Paramètres » pour découvrir où se règlent les dossiers et où se créent les aperçus 3D en série.", "SettingsTabItem", completionEvent: "settings-open"),
                new DemoStep("Photos automatiques", "Pour l'essai, les cinq familles de « Salle de réunion » sont présélectionnées. « Lancer l'export 3D » ouvre le choix A/B : Pikachu t'expliquera comment garder la vue existante ou la vue normalisée. Valide ce choix, puis attends la fin de l'export pour revenir au guide. L'opération peut prendre du temps ; tu peux aussi la passer avec « Suivant ».", "StartPreviewButton", true, completionEvent: "preview-export-complete"),
                new DemoStep("Adapter à ta bibliothèque", "« Modifier les chemins… » permet de choisir ton propre dossier de familles RFA, puis un dossier d'images PNG. Leurs sous-dossiers doivent se correspondre. Pendant ce guide, tes chemins enregistrés sont protégés : quitte le tutoriel avant de choisir ta bibliothèque personnelle.", "ChangePathsButton", true),
                new DemoStep("Autres actions d'une carte", "Sur une carte, le clic droit propose « Rentrer dans la famille » pour ouvrir le RFA source, « Charger la dernière version » pour recharger le fichier depuis le disque et « Ajouter à la collection active » pour classer plusieurs familles. Aucune de ces actions n'est nécessaire pour le placement d'essai.", "FamilyListView", true),
                new DemoStep("Ajouter du texte pour retrouver une famille", "Pikachu revient dans Bureau. Fais un clic droit sur « Bureau commun », puis choisis « Mots-clés de recherche… ». Dans « Description recherchable », décris son usage, par exemple « Poste de travail pour un espace partagé ». Ajoute dans « Mots-clés » : poste de travail, espace partagé, mobilier de bureau. Sépare les termes par des virgules. Dans ta bibliothèque personnelle, « Enregistrer » permet ensuite de retrouver la famille avec ces termes même s'ils ne figurent pas dans son nom, sans renommer le RFA. Pour cet essai, ferme avec « Annuler » afin de garder le catalogue de formation intact, puis clique sur « Suivant ».", "FamilyListView", true, completionEvent: "search-metadata-manual"),
                new DemoStep("Gagner du temps avec l'IA", "Sur « Bureau commun », rouvre « Mots-clés de recherche… » par clic droit. Le bouton « Proposer avec l’IA » suggère une description et des mots-clés à partir du nom de la famille, de son dossier et de sa catégorie lorsqu'elle est disponible. Relis et corrige les propositions avant d'enregistrer : l'IA ne vérifie pas ici la géométrie du RFA. L'appel démarre uniquement si tu cliques sur ce bouton ; tu peux passer cette étape si l'IA n'est pas disponible. Dans cet exercice, ferme avec « Annuler », puis clique sur « Suivant ». Dans ta bibliothèque, enregistre les termes retenus pour accélérer les prochaines recherches.", "FamilyListView", true, completionEvent: "search-metadata-ai"),
                new DemoStep("Charger et placer une famille", "Pikachu revient au dossier Bureau. Double-clique sur la carte « Bureau commun » : le simple clic sélectionne la carte, le double-clic charge la famille dans Revit et lance son placement. Clique ensuite dans la vue pour la poser, puis appuie sur Échap pour sortir du mode placement.", "FamilyListView", completionEvent: "load-bureau-commun"),
                new DemoStep("Ouvrir Famille dans Revit", "Le navigateur se ferme et Pikachu revient dans Revit. Termine le placement précédent avec Échap, ouvre l'onglet BIMaestro si besoin, puis clique sur la petite flèche du bouton « Famille ». Le contour orange suit le bon bouton.", "RevitFamilySplit"),
                new DemoStep("Choisir la rosace « . »", "Dans le menu « Famille », clique sur l'entrée « . ». Cette rosace s'ouvre près de la souris. Pikachu attend son ouverture avant de poursuivre.", "RevitRosace", completionEvent: "radial-opened"),
                new DemoStep("Passer à l'onglet Vue", "Ferme la rosace avec Échap, puis clique sur l'onglet « Vue » du ruban Revit. Pikachu encadre l'onglet et suit ton choix.", "RevitViewTab"),
                new DemoStep("Ouvrir Interface utilisateur", "Dans Vue, ouvre « Interface utilisateur » dans le panneau Fenêtres. Le contour orange passe du ruban BIMaestro au ruban Revit.", "RevitUserInterface"),
                new DemoStep("Créer le raccourci dans Revit", "Dans Vue > Interface utilisateur, clique sur « Raccourcis clavier ». Une fois la fenêtre ouverte :\n1. Garde le filtre « Tous », cherche « Navigateur de Familles » et sélectionne « Navigateur de Familles:. » sous BIMaestro > Spécifique aux familles. La ligne qui ouvre le navigateur est différente.\n2. Clique dans « Appuyer sur de nouvelles touches », puis tape B et F. Si BF est pris, choisis une autre combinaison libre.\n3. Clique sur « Attribuer », puis sur « OK ».\nPendant cette fenêtre Revit, le bouton « Suivant » de Pikachu ne répond pas. Après fermeture avec OK, Pikachu avance seul ; sinon clique sur « Suivant ».", "RevitKeyboardShortcuts", true),
                new DemoStep("Utiliser ton raccourci", "Reviens dans une vue Revit, place la souris où tu veux ouvrir la rosace et tape BF, ou le raccourci que tu as choisi. Fais un clic droit au centre de la rosace, puis « Charger une collection » > « Favoris ». Clique sur la chaise étoilée de l'exercice pour lancer son placement ; Pikachu le vérifiera. Clique dans la vue pour la poser, puis Échap.", "RevitUseShortcut", completionEvent: "radial-tutorial-chaise-used")
            },
            ["history"] = new[]
            {
                new DemoStep("Voir les suppressions", "Deux objets de la scène ont été supprimés au démarrage du parcours. Dans Action, filtre sur « Suppressions » pour distinguer une suppression d'une création ou modification.", "ActionFilterCombo"),
                new DemoStep("Filtrer par utilisateur", "Ce filtre isole les actions d'une personne. Il sert à comprendre qui a modifié la maquette, mais une absence de résultat peut aussi venir de la période chargée. Ne change rien pour retrouver les objets de la démo.", "UserFilterCombo", true),
                new DemoStep("Recherche et période", "La recherche cible un élément ou une information précise. Les dates « Du » et « Au » limitent les événements chargés ; « Charger période » relit alors l'historique. Garde les filtres actuels pour l'exercice.", "SearchBox", true),
                new DemoStep("Aperçu d'une suppression", "« Simple » montre vite un volume estimatif. « Détaillé » utilise la famille et le type encore présents pour les suppressions futures ; si les données manquent, l'aperçu reste simplifié. Cela ne restaure rien.", "DetailedMeshModeRadio", true),
                new DemoStep("Choisir un objet", "Sélectionne une carte de mobilier supprimé. Le troisième objet resté dans la vue 02 sert de repère pour comparer sa position.", "VisualCardsList"),
                new DemoStep("Examiner le contexte", "Ouvre Détails : vérifie l'auteur, la date, la catégorie et les informations enregistrées avant la suppression. Ce sont les éléments à confirmer avant toute restauration.", "DetailsButton"),
                new DemoStep("Visualiser avant d'agir", "« Visualiser » affiche un aperçu sans recréer l'élément. « Restaurer les éléments » le recrée durablement dans le projet. Si tu veux seulement enquêter, arrête-toi à l'aperçu.", "VisualizeDeletedButton", true),
                new DemoStep("Faire réapparaître l'objet", "Clique sur « Restaurer les éléments » et confirme. La famille, le type et le niveau doivent encore être disponibles ; Pikachu attendra une restauration réussie avant de valider.", "RestoreDeletedButton")
            }
        };

        internal static readonly DemoStep[] OrganizerRotatedSteps =
        {
            new DemoStep("Relancer depuis la vue tournée", "La vue a tourné de 90°. CML_Numéros de place, PK- et le format 001 sont déjà préparés. « Trier par niveau » reste décoché : clique sur Renommer pour comparer l'ordre dicté par la vue aux numéros du premier passage.", "DemoRenameButton")
        };

        // A TUTO button may be pressed from an ordinary project, without the
        // prepared model scene. These shorter tours explain the interface and
        // never claim that training elements have already been selected.
        internal static readonly IReadOnlyDictionary<string, DemoStep[]> StandaloneSteps =
            new Dictionary<string, DemoStep[]>
            {
                ["reservation"] = new[]
                {
                    new DemoStep("Choisir le support", "Mur crée une réservation dans un mur traversé ; Sol correspond à un autre cas de famille. Pour pratiquer avec une scène prête, ouvre Parcours guidés puis la maquette de formation.", "hostMurCard", true),
                    new DemoStep("Choisir la forme et le réseau", "La forme rectangulaire demande des paramètres de longueur, hauteur et profondeur. Choisis ensuite Canalisation ou Gaine selon le réseau réel à traiter.", "shapeRectCard", true),
                    new DemoStep("Configurer ta famille", "L'onglet Familles relie un type RFA à chaque combinaison de support, forme et hébergement. Vérifie les paramètres de dimensions avant d'enregistrer un nouveau mapping.", "ReservationFamiliesTab", true),
                    new DemoStep("Lancer dans ton projet", "En mode manuel, sélectionne un réseau puis le mur traversé. Contrôle la famille créée et ses dimensions. La maquette de formation propose un exercice vérifié pas à pas.", "DemoRunReservationButton", true)
                },
                ["pipe-calculation"] = new[]
                {
                    new DemoStep("Définir le périmètre", "Sélectionne dans la vue les canalisations et raccords à calculer. Le résultat dépend de cette sélection ; l'exercice guidé prépare un réseau complet dans la maquette de formation.", "CalculationOptionsTitle", true),
                    new DemoStep("Inclure les gaines", "Coche cette option si ta sélection contient aussi des gaines. Le calcul les ajoutera aux canalisations sélectionnées.", "IncludeDuctsCheckBox", true),
                    new DemoStep("Lire et exporter", "Garde l'export Excel coché pour voir d'abord le récapitulatif dans Revit, puis recevoir un fichier que BIMaestro te proposera d'ouvrir.", "ExportToExcelCheckBox", true),
                    new DemoStep("Calculer", "Clique sur OK quand ta sélection et tes options sont prêtes. Le parcours de formation vérifie ensuite les résultats sur sa scène dédiée.", "OkButton", true)
                },
                ["organizer"] = new[]
                {
                    new DemoStep("Choisir les éléments", "Organisateur agit sur les éléments sélectionnés dans la vue. Pour l'essai vérifié, la maquette de formation utilise huit places CML_Parking sur deux niveaux.", "ParameterComboBox", true),
                    new DemoStep("Choisir le paramètre", "Sélectionne le paramètre d'instance à modifier. Sur les parkings de formation, il s'appelle « CML_Numéros de place » ; dans ton projet, le nom peut être différent.", "ParameterComboBox", true),
                    new DemoStep("Régler le nom", "Le préfixe, le format et le sens de lecture déterminent les nouvelles valeurs. « Trier par niveau » sépare les étages ; sans cette coche, l'ordre suit l'orientation de la vue.", "PrefixTextBox", true),
                    new DemoStep("Appliquer", "Renommer écrit les valeurs dans le projet. Pour voir Pikachu contrôler chaque place, lance le parcours Organisateur depuis la maquette de formation.", "DemoRenameButton", true)
                },
                ["history"] = new[]
                {
                    new DemoStep("Retrouver les actions", "Filtre l'historique par type d'action, utilisateur ou période. La maquette de formation prépare deux suppressions pour l'exercice complet.", "ActionFilterCombo", true),
                    new DemoStep("Examiner un élément", "Sélectionne une carte et ouvre Détails pour vérifier l'auteur, la date et l'objet avant toute opération.", "VisualCardsList", true),
                    new DemoStep("Visualiser", "L'aperçu permet de situer un élément supprimé sans le recréer dans la maquette.", "VisualizeDeletedButton", true),
                    new DemoStep("Restaurer si nécessaire", "Restaurer recrée l'élément dans le projet. Utilise cette action après avoir confirmé la suppression et la disponibilité de la famille correspondante.", "RestoreDeletedButton", true)
                }
            };

    }

    internal static class DemoHistoryScene
    {
        internal static int Reset(Document doc, out int removedReservations)
        {
            removedReservations = 0;
            if (doc == null || !System.IO.Path.GetFileName(doc.PathName)
                    .StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ouvre d'abord une maquette BIMaestro_Apprentissage pour recommencer les exercices.");

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            FamilyInstance witness = furniture.FirstOrDefault(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.HistoryFurnitureMark(0));
            if (witness == null)
                throw new InvalidOperationException("Cette maquette n'a pas la scène de mobilier. Crée une nouvelle maquette de formation.");
            Level level = doc.GetElement(witness.LevelId) as Level;
            if (level == null) throw new InvalidOperationException("Le niveau du mobilier de démonstration est introuvable.");

            var reservations = furniture.Where(instance =>
                (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                == DemoProjectBuilder.DemoPrefix + "RESERVATION_CREEE")
                .Select(instance => instance.Id).ToList();
            int restoredFurniture = 0;
            using (var tx = new Transaction(doc, "BIMaestro - Recommencer les exercices"))
            {
                tx.Start();
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
                throw new InvalidOperationException("Ouvre d'abord une maquette BIMaestro_Apprentissage pour préparer cet exercice.");

            var furniture = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().ToList();
            if (!furniture.Any(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    == DemoProjectBuilder.DemoPrefix + "HISTORIQUE_TEMOIN"))
                throw new InvalidOperationException("Cette maquette utilise l'ancien scénario. Crée une nouvelle maquette de formation pour l'exercice de restauration.");
            var toRemove = furniture
                .Where(instance => (instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(DemoProjectBuilder.DemoPrefix + "HISTORIQUE_A_RESTAURER_", StringComparison.Ordinal))
                .Select(instance => instance.Id).ToList();
            if (toRemove.Count == 0) return 0;

            Analyse.ElementHistoryTracker.PrimeDocument(doc);
            using (var tx = new Transaction(doc, "BIMaestro - Exercice historique : supprimer le mobilier"))
            {
                tx.Start();
                doc.Delete(toRemove);
                tx.Commit();
            }
            Analyse.ElementHistoryTracker.FlushPendingForHistory();
            doc.Save();
            return toRemove.Count;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class DemoToursCommand : BaseTrackedCommand
    {
        protected override string ButtonId => "DemoTours";
        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var choice = new DemoChoiceWindow();
            new WindowInteropHelper(choice).Owner = data.Application.MainWindowHandle;
            choice.ShowDialog();
            string selectedChoice = choice.Choice;
            if (selectedChoice == "overview")
            {
                var overview = new DemoDiscoveryWindow();
                new WindowInteropHelper(overview).Owner = data.Application.MainWindowHandle;
                overview.ShowDialog();
                selectedChoice = overview.SelectedTour;
            }
            return RunChoice(data.Application, selectedChoice, ref message);
        }

        internal static Result RunChoice(UIApplication uiApp, string selectedChoice, ref string message)
        {
            if (selectedChoice == "create")
            {
                try
                {
                    string path = DemoProjectBuilder.Create(uiApp);
                    DemoTourMessage.Show(uiApp.MainWindowHandle, "Pika ! Maquette prête",
                        "La maquette de formation est créée et ouverte :\n" + path,
                        "Choisir un tutoriel");
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                        "Création impossible : " + ex.Message);
                    return Result.Failed;
                }
            }
            else if (!string.IsNullOrEmpty(selectedChoice))
            {
                UIDocument activeDocument = uiApp.ActiveUIDocument;
                if (activeDocument == null)
                {
                    DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu attend une maquette",
                        "Ouvre d'abord la maquette de formation ou un projet Revit.");
                    return Result.Cancelled;
                }
                if (selectedChoice == "reset")
                {
                    try
                    {
                        int restored = DemoHistoryScene.Reset(activeDocument.Document, out int removed);
                        DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pika ! Exercices prêts",
                            "Exercices prêts à recommencer : " + restored + " meuble(s) remis en place et " +
                            removed + " réservation(s) du parcours retirée(s). Les couleurs personnelles restent inchangées.");
                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                            "Réinitialisation impossible : " + ex.Message);
                        return Result.Failed;
                    }
                }
                DemoProjectBuilder.UpdateTrainingViews(activeDocument.Document);
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
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                            "Préparation de Gestion Excel impossible : " + ex.Message);
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
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                            "Préparation d'Organisateur impossible : " + ex.Message);
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
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu cherche la bonne vue",
                            "Cette maquette ne contient pas encore la scène de ce parcours. Crée une nouvelle maquette de formation avec le bouton dédié.");
                        return Result.Cancelled;
                    }
                    if (tourView != null && activeDocument.ActiveView.Id != tourView.Id)
                    {
                        activeDocument.RequestViewChange(tourView);
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
                    DemoViewTemplateGuide.Begin(activeDocument.Document);
                    DemoTourMessage.Show(uiApp.MainWindowHandle, "Pika ! Gabarit de vue",
                        "Tu es dans « 06 Gabarit source ». Exporte ses réglages depuis le bouton Gabarit de vue. Ensuite, ouvre « 07 Gabarit cible », relance le bouton et importe le fichier en choisissant « Créer un vrai gabarit nommé ». Pikachu te guidera après l'export.");
                }
                if (selectedChoice == "history")
                {
                    try
                    {
                        int removed = DemoHistoryScene.Prepare(activeDocument.Document);
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu prépare l'enquête", removed > 0
                            ? removed + " objets de la scène ont été supprimés et enregistrés dans l'historique. Le troisième reste visible. Suis Pikachu pour les restaurer."
                            : "La scène est déjà préparée. Suis Pikachu pour retrouver les suppressions dans l'historique.");
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                            "Préparation de l'exercice impossible : " + ex.Message);
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
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu attend la vue",
                    "Revit n'a pas activé la vue de formation. Ouvre-la dans l'arborescence, puis relance ce parcours.");
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
                (tourId == "organizer" ? "ORGANISATEUR_" : "CALCUL_");
            var ids = new FilteredElementCollector(uiDocument.Document)
                .WhereElementIsNotElementType()
                .Where(element => (element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith(marker, StringComparison.Ordinal))
                .Select(element => element.Id).ToList();
            if (ids.Count == 0)
            {
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu cherche la scène",
                    "Les éléments de cet exercice manquent. Crée une nouvelle maquette de formation avec le bouton « Créer et ouvrir la maquette ».");
                return false;
            }
            uiDocument.Selection.SetElementIds(ids);
            if (tourId == "organizer")
                try { uiDocument.ShowElements(ids); } catch { /* La sélection reste active. */ }
            DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Sélection prête",
                ids.Count + (tourId == "organizer"
                    ? " places sur deux niveaux sont sélectionnées. Suis Pikachu jusqu'au bouton Organisateur, puis teste le tri par niveau."
                    : " éléments de la vue sont sélectionnés. Suis Pikachu jusqu'au bouton, puis dans la fenêtre de la commande."));
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
            _documentPath = document.PathName;
            _active = true;
        }

        internal static void Verify(UIApplication app, ICollection<ElementId> selectedIds,
            bool includeDucts, bool exportToExcel, string excelFilePath,
            Dictionary<double, double> pipeLengths,
            Dictionary<string, double> ductLengths, Dictionary<string, double> ductFittingLengths,
            Dictionary<string, int> elbowCounts)
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
            if (!completeSelection || !bothDiameters || elbowCount < 5)
            {
                Retry(app, uiDocument, "Le résultat ne contient pas encore toute la scène : les deux diamètres, leurs cinq coudes et tous les éléments sélectionnés sont nécessaires. Pikachu les resélectionne pour réessayer.");
                return;
            }

            double ductTotal = ductLengths.Values.Sum();
            double ductFittingsTotal = ductFittingLengths.Values.Sum();
            if (!includeDucts || ductTotal <= 0 || ductFittingLengths.Count == 0)
            {
                Retry(app, uiDocument, "Le résultat doit contenir les gaines et leurs raccords. Coche « Inclure les gaines », puis relance le calcul.");
                return;
            }
            if (!exportToExcel || string.IsNullOrEmpty(excelFilePath) ||
                !System.IO.File.Exists(excelFilePath))
            {
                Retry(app, uiDocument, "Le fichier Excel manque. Garde « Exporter les résultats vers Excel » coché, puis relance le calcul.");
                return;
            }
            _active = false;
            Reselect(uiDocument, demoIds);
            DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Calcul et export réussis",
                $"Pikachu a vérifié les canalisations DN100 et DN50, leurs {elbowCount} coudes, {ductTotal:F2} m de gaine et environ {ductFittingsTotal:F2} m de raccords de gaine. Le récapitulatif Revit et le fichier Excel viennent du même calcul.\n\nFichier : {excelFilePath}");
        }

        private static void Retry(UIApplication app, UIDocument uiDocument, string message)
        {
            Reselect(uiDocument, DemoExerciseElements.Find(uiDocument.Document, "CALCUL_"));
            DemoTourMessage.Show(app.MainWindowHandle, "Pikachu a besoin d'un essai", message);
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
                throw new InvalidOperationException("Les quatre places du parcours Excel sont introuvables.");
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
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu attend la bonne vue",
                    "La vue du parcours Excel ne s'est pas ouverte. Relance le tutoriel depuis BIMaestro.");
                return true;
            }
            int action = _pendingAction;
            _pendingAction = 0;
            if (action == 1)
            {
                DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Gestion Excel",
                    "Voici la nomenclature des quatre places. « " + _fieldName + " » contient le numéro modifiable, visible sur chaque place en 3D. « " +
                    _referenceFieldName + " » est la référence fixe : ne la change pas. Suis Pikachu jusqu'à « Gestion Excel » et choisis « Exporter ».");
                Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
            }
            else
            {
                var ids = DemoProjectBuilder.FindExcelParking(uiDocument.Document)
                    .Select(place => place.Id).ToList();
                uiDocument.Selection.SetElementIds(ids);
                try { uiDocument.ShowElements(ids); } catch { }
                DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Import vérifié",
                    "Pikachu a relu les quatre places dans Revit. Modifications vérifiées :\n" + _verifiedChanges +
                    "\n\nLa référence « " + _referenceFieldName + " » est restée identique. Le numéro modifié est aussi affiché sur les places sélectionnées dans la vue 3D.");
            }
            return true;
        }

        internal static void Exported(UIApplication app, string path)
        {
            if (!NeedsExport(app.ActiveUIDocument?.Document)) return;
            _workbookPath = path;
            _stage = 2;
            DemoTourMessage.Show(app.MainWindowHandle, "Pika ! À toi dans Excel",
                "Le classeur est ici :\n" + path +
                "\n\nDans l'onglet « Nomenclature », modifie un ou plusieurs numéros dans « " + _fieldName +
                " » (par exemple XL-003 → XL-103). La colonne « " + _referenceFieldName +
                " » est une référence fixe : garde-la intacte. L'onglet « Edition » reprend tes modifications par formule pour l'import ; ne touche pas à ses identifiants cachés. Enregistre et ferme le fichier, puis reviens sur « Gestion Excel » et choisis « Importer ». Pikachu affichera les nouveaux numéros sur les places en 3D.");
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "excel");
        }

        internal static bool ValidateWorkbook(UIApplication app, string path,
            List<Dictionary<string, string>> rows)
        {
            Document document = app.ActiveUIDocument?.Document;
            if (!IsActive(document)) return true;
            if (_stage != 2 || !string.Equals(path, _workbookPath, StringComparison.OrdinalIgnoreCase))
                return RejectWorkbook(app, "Pikachu attend l'export",
                    "Exporte d'abord la nomenclature du parcours, puis modifie et enregistre le classeur avant de l'importer.");

            // Contrôler les quatre identifiants ET le repère avant d'ouvrir une transaction.
            // Une ligne supplémentaire ou dupliquée ne doit jamais pouvoir modifier une autre place.
            if (rows == null || _references == null || _references.Count != 4 || rows.Count != 4)
                return RejectWorkbook(app, "Pikachu vérifie les places",
                    "Le classeur doit contenir exactement les quatre places exportées. Repars de l'export du parcours, sans ajouter ni supprimer de ligne.");

            var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null || !row.TryGetValue("UniqueId", out string uid) ||
                    !_references.TryGetValue(uid ?? "", out string reference) || numbers.ContainsKey(uid))
                    return RejectWorkbook(app, "Pikachu vérifie les places",
                        "Un identifiant de place est absent, inconnu ou en double dans l'onglet « Edition ». Repars du classeur exporté.");
                if (!row.TryGetValue(_referenceFieldName, out string workbookReference) ||
                    !string.Equals(workbookReference, reference, StringComparison.Ordinal))
                    return RejectWorkbook(app, "Pikachu protège le repère",
                        "La colonne « " + _referenceFieldName + " » est une référence fixe. Restaure sa valeur d'origine dans « Nomenclature », puis enregistre le classeur avant l'import.");
                if (!row.TryGetValue(_fieldName, out string number))
                    return RejectWorkbook(app, "Pikachu cherche les numéros",
                        "La colonne « " + _fieldName + " » manque. Repars du classeur exporté par le parcours.");
                numbers.Add(uid, number ?? "");
            }

            bool changed = numbers.Any(pair =>
                !string.Equals(pair.Value, _initialNumbers[pair.Key], StringComparison.Ordinal));
            if (!changed)
                return RejectWorkbook(app, "Pikachu attend une modification",
                    "Modifie au moins un numéro dans « Nomenclature » (par exemple XL-003 → XL-103), enregistre et ferme Excel, puis réessaie. « " + _referenceFieldName + " » reste fixe.");

            _expectedNumbers = numbers;
            var changes = new List<string>();
            foreach (var pair in _references.OrderBy(pair => pair.Value))
            {
                string uid = pair.Key;
                string label = pair.Value.Substring((DemoProjectBuilder.DemoPrefix + "EXCEL_").Length);
                if (_initialNumbers[uid] != numbers[uid])
                    changes.Add("Place " + label + " · " + _fieldName + " : " +
                        DisplayExcelValue(_initialNumbers[uid]) + " → " + DisplayExcelValue(numbers[uid]));
            }
            _verifiedChanges = string.Join("\n", changes);
            return true;
        }

        private static string DisplayExcelValue(string value) => string.IsNullOrEmpty(value) ? "(vide)" : value;

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
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu vérifie l'import",
                    "L'import s'est terminé, mais Revit ne correspond pas encore aux valeurs du classeur. Vérifie les numéros et la référence fixe, puis réessaie l'import.");
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
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu vérifie l'affichage",
                    "Les valeurs du classeur sont dans Revit, mais les numéros visibles n'ont pas pu être actualisés : " +
                    ex.Message);
                return;
            }
            if (!places.All(place => _expectedNumbers.TryGetValue(place.UniqueId, out string expectedNumber) &&
                string.Equals(place.LookupParameter("CML_Numéros de place")?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal) &&
                string.Equals(place.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                    expectedNumber, StringComparison.Ordinal)))
            {
                DemoTourMessage.Show(app.MainWindowHandle, "Pikachu vérifie l'affichage",
                    "Le numéro importé ne correspond pas au texte affiché sur chaque place. Vérifie les paramètres des places avant de conclure l'exercice.");
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

        internal static void Begin(Document document)
        {
            _documentPath = document?.PathName;
            _exportedFile = null;
            _waitingForImport = false;
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
            DemoTourMessage.Show(app.MainWindowHandle, "Pika ! Export réussi",
                "La vue cible est ouverte. Relance « Gabarit de vue », choisis « Importer dans la vue active ». Le fichier exporté sera prérempli :\n" +
                path + "\n\nChoisis ensuite « Créer / mettre à jour un vrai gabarit nommé ».");
            Couleur.AppearanceOnboarding.StartIntro(app.MainWindowHandle, "view-template");
        }

        internal static void OnImportCompleted(Document document, IntPtr owner)
        {
            if (!IsCurrent(document) || !_waitingForImport) return;
            _documentPath = null;
            _exportedFile = null;
            _waitingForImport = false;
            DemoTourMessage.Show(owner, "Pika ! Gabarit créé",
                "Dans les Propriétés de la vue 07, vérifie le gabarit affecté. Tu peux maintenant comparer les vues 06 et 07 et réutiliser ce gabarit dans le projet.");
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
            if (string.IsNullOrWhiteSpace(path) ||
                !System.IO.Path.GetFileName(path).StartsWith("BIMaestro_Apprentissage_", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(System.IO.Path.GetExtension(path), ".rvt", StringComparison.OrdinalIgnoreCase))
                return;
            _pendingPath = path;
            _readyAfterUtc = DateTime.UtcNow.AddMilliseconds(600);
        }

        // Return true while this invitation owns the active learning model, so other
        // first-run prompts wait until the learner has made a choice.
        internal static bool ProcessIdling(UIApplication uiApp)
        {
            if (DemoTourPendingSelection.ProcessIdling(uiApp)) return true;
            if (DemoExcelExercise.ProcessIdling(uiApp)) return true;
            if (string.IsNullOrEmpty(_pendingPath) || uiApp?.ActiveUIDocument?.Document == null)
                return false;
            string activePath = uiApp.ActiveUIDocument.Document.PathName;
            if (!string.Equals(activePath, _pendingPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (DateTime.UtcNow < _readyAfterUtc || GetForegroundWindow() != uiApp.MainWindowHandle ||
                Application.Current?.Windows.Cast<Window>().Any(window => window.IsVisible) == true)
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
                DemoTourMessage.Show(uiApp.MainWindowHandle, "Pikachu a besoin d'aide",
                    "Le tutoriel n'a pas pu démarrer : " + ex.Message);
            }
            return true;
        }
    }

    internal sealed class DemoTrainingChoiceWindow : Window
    {
        internal string SelectedTour { get; private set; }

        internal DemoTrainingChoiceWindow()
        {
            Title = "BIMaestro · Choisir un tutoriel";
            Width = 500; Height = 650; MinWidth = 420; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            Content = scroll;
            var heading = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(heading);
            heading.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                Width = 42, Height = 42, Margin = new Thickness(0, 0, 12, 0) });
            heading.Children.Add(new TextBlock { Text = "Quelle mission veux-tu suivre ?",
                FontSize = 18, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(new TextBlock
            {
                Text = "La maquette de formation est ouverte. Choisis un tutoriel : Pikachu te placera dans la bonne vue et te guidera dans la commande.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
            });
            AddTour(stack, "1 · Auto résa", "Créer une réservation sur le mur traversé par la canalisation.", "reservation");
            AddTour(stack, "2 · Qui a fait ça ?", "Retrouver et restaurer les meubles supprimés.", "history");
            AddTour(stack, "3 · Couleurs et vues", "Colorer l'arborescence et ajouter des icônes aux dossiers.", "colors");
            AddTour(stack, "4 · Calcul des canalisations", "Calculer canalisations, coudes et gaines, puis lire le fichier Excel.", "pipe-calculation");
            AddTour(stack, "5 · Organisateur", "Renuméroter huit places sur deux niveaux, puis tourner la vue.", "organizer");
            AddTour(stack, "6 · Gabarit de vue", "Exporter une vue, puis créer un gabarit dans une autre.", "view-template");
            AddTour(stack, "7 · Gestion Excel", "Modifier les numéros dans Excel, garder le repère fixe et voir les changements sur les places en 3D.", "excel");
            AddTour(stack, "8 · Navigateur de familles", "Explorer 35 familles, rechercher, voir en 3D et préparer les aperçus.", "family-browser");
            var later = new Button { Content = "Plus tard", HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 10, 0, 0) };
            later.Click += (_, __) => Close();
            stack.Children.Add(later);
        }

        private void AddTour(Panel parent, string title, string description, string tourId)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.WhiteSmoke, BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8) };
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0) });
            button.Content = body;
            button.Click += (_, __) => { SelectedTour = tourId; Close(); };
            parent.Children.Add(button);
        }
    }

    internal static class DemoTourService
    {
        private static readonly Dictionary<Window, DemoWindowGuide> ActiveGuides = new Dictionary<Window, DemoWindowGuide>();
        private static FamilyRibbonTutorialGuide _familyRibbonGuide;
        private static Famille.Collection _familyTutorialFavorites;

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
                previous.Close();
            }
            if (id == "family-browser" && window is Famille.FamilyBrowserWindow browser &&
                !browser.BeginTutorialCatalog()) return false;
            if (!preparedExercise && DemoTourCatalog.StandaloneSteps.TryGetValue(id, out DemoStep[] standalone))
                steps = standalone;
            if (id == "organizer" && preparedExercise && DemoOrganizerExercise.IsRotatedPass)
                steps = DemoTourCatalog.OrganizerRotatedSteps;
            if (id == "pipe-calculation")
            {
                window.Width = Math.Max(window.Width, 760);
                window.Height = Math.Max(window.Height, 520);
            }
            ActiveGuides[window] = new DemoWindowGuide(window, steps, id, preparedExercise);
            return true;
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
            _familyTutorialFavorites = browser.TryGetTutorialFavoritesForRosace();
            int ribbonStartIndex = Array.FindIndex(steps, step => step.Target == "RevitFamilySplit");
            if (ribbonStartIndex < 0) return;
            var guide = new FamilyRibbonTutorialGuide(owner, steps, ribbonStartIndex, () =>
            {
                _familyTutorialFavorites = null;
                _familyRibbonGuide = null;
            });
            _familyRibbonGuide = guide;
            // The browser keeps the exercise favourites only in memory. Snapshot
            // them first, then restore the learner's paths before closing it.
            browser.EndTutorialCatalog();
            browser.Close();
            guide.Show();
        }

        internal static void ReportRestoration(Window window)
        {
            if (ActiveGuides.TryGetValue(window, out DemoWindowGuide guide))
                guide.CompleteRestoration();
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
            private bool _windowClosed;

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
                    BorderBrush = Brushes.DarkOrange, BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetRowSpan(_card, Math.Max(1, _root.RowDefinitions.Count));
                Grid.SetColumnSpan(_card, Math.Max(1, _root.ColumnDefinitions.Count));
                Panel.SetZIndex(_card, 1000);
                var stack = new StackPanel(); _card.Child = stack;
                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Image { Source = Couleur.RibbonPanelColorScheme.CreateCompanionImage(),
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
                _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 15,
                    Width = 245, TextWrapping = TextWrapping.Wrap };
                heading.Children.Add(_title); stack.Children.Add(heading);
                _text = new TextBlock { Margin = new Thickness(0, 9, 0, 12), TextWrapping = TextWrapping.Wrap };
                stack.Children.Add(_text);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right };
                stack.Children.Add(buttons);
                _previous = AddButton(buttons, "Précédent", () => { _index--; ShowStep(); });
                _next = AddButton(buttons, "Suivant", () =>
                {
                    if (!_completed) return;
                    if (_index + 1 == _steps.Length) { Close(); return; }
                    _index++; ShowStep();
                });
                AddButton(buttons, "Quitter", Close);
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
                _window.Loaded -= OnLoaded;
                ShowStep();
            }

            private void ShowStep()
            {
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_index < 0 || _index >= _steps.Length) return;
                if (TourId == "family-browser" && _steps[_index].Target == "RevitFamilySplit" &&
                    _window is Famille.FamilyBrowserWindow familyBrowser)
                {
                    ContinueFamilyGuideInRevit(_steps, familyBrowser);
                    return;
                }
                _completed = false;
                DemoStep step = _steps[_index];
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
                    step.Target == "FamilyListView" || step.Target == "GroupedFamilyListView"
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _card.VerticalAlignment = TourId == "pipe-calculation"
                    ? VerticalAlignment.Center
                    : TourId == "organizer" && step.Target == "SortByLevelCheckBox"
                    ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                _title.Text = $"{_index + 1}/{_steps.Length} · {step.Title}";
                _text.Text = step.Text;
                _previous.IsEnabled = _index > 0;
                _next.Content = _index + 1 == _steps.Length ? "Terminer" : "Suivant";
                _completed = step.IsExplanation;
                _next.IsEnabled = step.IsExplanation;
                if (step.CompletionEvent == "load-bureau-commun" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += " Aucun projet n'est ouvert : ouvre-en un dans Revit pour essayer le chargement, ou termine ici et reviens plus tard avec le bouton TUTO.";
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "favorite-added" &&
                    _window is Famille.FamilyBrowserWindow favoriteBrowser &&
                    favoriteBrowser.HasTutorialChaiseFavorite())
                {
                    _text.Text += " Une chaise est déjà dans Favoris : ouvre l'onglet Favoris avec « Suivant ».";
                    _completed = true;
                    _next.IsEnabled = true;
                }
                if (step.CompletionEvent == "radial-tutorial-chaise-used" &&
                    Famille.FamilyBrowserCommand.uiapp?.ActiveUIDocument == null)
                {
                    _text.Text += " Aucun projet Revit n'est ouvert : ouvre une maquette pour essayer la rosace et placer une famille. Tu peux terminer ici et relancer TUTO plus tard.";
                    _completed = true;
                    _next.IsEnabled = true;
                }
                int current = _index;
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Highlight(step.Target, current);
                    if (!step.IsExplanation && step.CompletionEvent == null)
                        WatchAction(step.Target, current);
                }), DispatcherPriority.Loaded);
            }

            internal void CompleteAction(string action)
            {
                if (_index >= 0 && _index < _steps.Length &&
                    _steps[_index].CompletionEvent == "radial-tutorial-chaise-used" &&
                    action == "radial-opened")
                {
                    _text.Text = "✓ La rosace est ouverte. Fais un clic droit au centre, choisis « Charger une collection » > « Favoris », puis clique sur ta chaise étoilée. BIMaestro lancera son placement dans Revit.";
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
                if (targetName == "RestoreDeletedButton") return; // Wait for a committed restoration.
                if (target is System.Windows.Controls.TextBox textBox)
                {
                    TextChangedEventHandler handler = (_, __) =>
                    {
                        if (targetName != "PrefixTextBox" ||
                            textBox.Text.StartsWith("PK-", StringComparison.OrdinalIgnoreCase))
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
                    SelectionChangedEventHandler handler = (_, __) =>
                    {
                        string selected = selector.SelectedItem?.ToString() ?? "";
                        if ((targetName != "ActionFilterCombo" ||
                             selected.IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            (targetName != "ParameterComboBox" ||
                             selected == "CML_Numéros de place") &&
                            (targetName != "NumberFormatComboBox" ||
                             selected.StartsWith("001,002,003", StringComparison.Ordinal)))
                            CompleteStep(expectedIndex);
                    };
                    selector.SelectionChanged += handler;
                    _detachAction = () => selector.SelectionChanged -= handler;
                    if (targetName == "ActionFilterCombo" &&
                        (selector.SelectedItem?.ToString() ?? "").IndexOf("supp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _text.Text += " Le filtre est déjà actif : clique sur Suivant.";
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if (targetName == "VisualCardsList" && selector.SelectedItem != null)
                    {
                        _text.Text += " Une carte est déjà sélectionnée : examine-la, puis clique sur Suivant.";
                        _completed = true;
                        _next.IsEnabled = true;
                    }
                    if ((targetName == "ParameterComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "") == "CML_Numéros de place") ||
                        (targetName == "NumberFormatComboBox" &&
                         (selector.SelectedItem?.ToString() ?? "").StartsWith("001,002,003", StringComparison.Ordinal)))
                    {
                        _text.Text += " Le bon choix est déjà actif : clique sur Suivant.";
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

            internal void CompleteRestoration()
            {
                if (_index == _steps.Length - 1 && _steps[_index].Target == "RestoreDeletedButton")
                    CompleteStep(_index);
            }

            private void CompleteStep(int expectedIndex)
            {
                if (_index != expectedIndex || _completed) return;
                _completed = true;
                if (_index + 1 == _steps.Length)
                {
                    _text.Text = _steps[_index].Target == "RestoreDeletedButton"
                        ? "✓ Le mobilier a réapparu. BIMaestro l'a sélectionné et cadré dans la vue : compare-le avec l'objet témoin, puis termine le parcours."
                        : _steps[_index].CompletionEvent == "load-bureau-commun"
                        ? "✓ Revit a chargé la famille et lancé son placement. Clique dans la vue pour la poser, ou appuie sur Échap si tu voulais seulement tester le chargement."
                        : _steps[_index].CompletionEvent == "radial-tutorial-chaise-used"
                        ? "✓ Tu as lancé ta chaise favorite depuis la rosace. Clique dans la vue pour la placer, puis appuie sur Échap pour quitter le mode placement. Ton favori d'essai et tes chemins personnels restent inchangés."
                        : "✓ Action confirmée dans la maquette. Tu peux terminer ce parcours.";
                    _next.IsEnabled = true;
                }
                else
                {
                    _index++;
                    ShowStep();
                }
            }

            private void Highlight(string targetName, int expectedIndex)
            {
                if (_index != expectedIndex || !_window.IsVisible) return;
                FrameworkElement target = targetName == "TutorialFavoriteStar" &&
                    _window is Famille.FamilyBrowserWindow browser
                    ? browser.FindTutorialFavoriteStar()
                    : _window.FindName(targetName) as FrameworkElement;
                if (target == null || !target.IsVisible) return;
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
                _window.Loaded -= OnLoaded;
                _detachAction?.Invoke();
                _detachAction = null;
                RemoveHighlight();
                if (_root != null && _root.Children.Contains(_card)) _root.Children.Remove(_card);
                if (ActiveGuides.TryGetValue(_window, out DemoWindowGuide active) && ReferenceEquals(active, this))
                    ActiveGuides.Remove(_window);
                if (!_windowClosed && _window is Famille.FamilyBrowserWindow browser)
                    browser.EndTutorialCatalog();
            }
        }

        private sealed class TargetAdorner : Adorner
        {
            internal TargetAdorner(UIElement target) : base(target)
            { IsHitTestVisible = false; Focusable = false; }
            protected override void OnRender(DrawingContext drawing)
            {
                var bounds = new Rect(AdornedElement.RenderSize);
                if (bounds.Width > 0 && bounds.Height > 0)
                    drawing.DrawRoundedRectangle(null, new Pen(Brushes.DarkOrange, 3),
                        new Rect(-2, -2, bounds.Width + 4, bounds.Height + 4), 5, 5);
            }
        }
    }

    internal sealed class DemoChoiceWindow : Window
    {
        internal string Choice { get; private set; }
        internal DemoChoiceWindow()
        {
            Title = "BIMaestro - Parcours guidés";
            Width = 470; Height = 510; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(20) }; scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = "Apprendre BIMaestro avec Pikachu", FontSize = 18,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = "Découvre les commandes, puis essaie-les dans la maquette avec Pikachu.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            Button quick = AddChoice(stack, "Commencer la découverte · 2 min", "overview");
            quick.FontWeight = FontWeights.SemiBold;
            quick.BorderBrush = Brushes.DarkOrange;
            quick.BorderThickness = new Thickness(2);
            AddChoice(stack, "Créer et ouvrir la maquette de formation", "create");
            var detailed = new Expander { Header = "Aller directement aux exercices détaillés",
                Margin = new Thickness(0, 6, 0, 12), IsExpanded = false };
            stack.Children.Add(detailed);
            var detailChoices = new StackPanel { Margin = new Thickness(8, 10, 0, 0) };
            detailed.Content = detailChoices;
            AddChoice(detailChoices, "1 · Auto réservation", "reservation");
            AddChoice(detailChoices, "2 · Qui a fait ça ?", "history");
            AddChoice(detailChoices, "3 · Couleurs et vues", "colors");
            AddChoice(detailChoices, "4 · Calcul des canalisations", "pipe-calculation");
            AddChoice(detailChoices, "5 · Organisateur", "organizer");
            AddChoice(detailChoices, "6 · Gabarit de vue", "view-template");
            AddChoice(detailChoices, "7 · Gestion Excel", "excel");
            AddChoice(detailChoices, "8 · Navigateur de familles", "family-browser");
            AddChoice(stack, "Recommencer les exercices de la maquette", "reset");
        }
        private Button AddChoice(Panel panel, string label, string id)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(10, 6, 10, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, __) => { Choice = id; Close(); };
            panel.Children.Add(button);
            return button;
        }
    }

    internal sealed class DemoDiscoveryWindow : Window
    {
        internal string SelectedTour { get; private set; }

        internal DemoDiscoveryWindow()
        {
            Title = "BIMaestro - Découverte rapide";
            Width = 570; Height = 710; MinWidth = 480; MinHeight = 550;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Background = Brushes.White;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Content = scroll;
            var stack = new StackPanel { Margin = new Thickness(22) };
            scroll.Content = stack;
            stack.Children.Add(new TextBlock { Text = "BIMaestro en 2 minutes", FontSize = 21,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "Voici les fonctions de la maquette de formation. Cette découverte ne modifie pas le projet ; tu choisiras ensuite si tu veux pratiquer.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            AddCard(stack, "1 · Auto réservation",
                "Quand un réseau traverse un mur, BIMaestro place une famille de réservation au croisement. Dans l'exercice, tu choisis toi-même la canalisation puis le mur et tu examines le résultat.");
            AddCard(stack, "2 · Qui a fait ça ?",
                "Retrouve l'auteur et le contexte d'une modification. Dans l'exercice, deux meubles sont supprimés, puis tu les fais réapparaître à partir de l'historique.");
            AddCard(stack, "3 · Couleurs et vues",
                "Personnalise l'arborescence sans renommer ni recréer les vues. Dans l'exercice, tu changes le fond et ajoutes des icônes aux dossiers Plans d'étage et Vues 3D.");
            AddCard(stack, "4 · Calcul des canalisations",
                "Sélectionne deux réseaux de canalisations avec coudes et une gaine. Un seul calcul affiche le récapitulatif Revit et crée le fichier Excel.");
            AddCard(stack, "5 · Organisateur",
                "Numérote huit places CML_Parking sur deux niveaux, d'abord par niveau puis selon l'ordre de lecture d'une vue 3D tournée de 90°.");
            AddCard(stack, "6 · Gabarit de vue",
                "Exporte les réglages d'une vue 3D, puis importe-les dans une seconde vue pour créer un vrai gabarit Revit nommé.");
            AddCard(stack, "7 · Gestion Excel",
                "Exporte les places CML_Parking : numéro modifiable et repère fixe. Essaie par exemple XL-003 → XL-103, puis importe tes changements. Pikachu vérifie chaque valeur et l'affiche sur la place en 3D.");
            AddCard(stack, "8 · Navigateur de familles",
                "Explore un catalogue de 35 familles rangées en dossiers et sous-dossiers. Cherche une famille, prévisualise-la en 3D, découvre les photos automatiques et apprends à brancher ta propre bibliothèque.");
            stack.Children.Add(new TextBlock { Text = "Envie d'essayer dans la maquette ?",
                FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "Choisis un exercice détaillé, ou termine ici et reviens plus tard.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var exercises = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(exercises);
            AddExerciseButton(exercises, "Auto résa", "reservation");
            AddExerciseButton(exercises, "Qui a fait ça ?", "history");
            AddExerciseButton(exercises, "Couleurs et vues", "colors");
            AddExerciseButton(exercises, "Calcul canalisations", "pipe-calculation");
            AddExerciseButton(exercises, "Organisateur", "organizer");
            AddExerciseButton(exercises, "Gabarit de vue", "view-template");
            AddExerciseButton(exercises, "Gestion Excel", "excel");
            AddExerciseButton(exercises, "Navigateur de familles", "family-browser");
            var close = new Button { Content = "Terminer pour l'instant", Padding = new Thickness(12, 7, 12, 7),
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

        private static void AddCard(Panel parent, string title, string description)
        {
            var card = new Border { Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(10), BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(1), Background = Brushes.WhiteSmoke };
            parent.Children.Add(card);
            var body = new StackPanel(); card.Child = body;
            body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 10) });
        }
    }

    internal static class DemoTourMessage
    {
        internal static bool Show(IntPtr owner, string heading, string message,
            string buttonLabel = "Continuer", string learnMoreLabel = null)
        {
            bool learnMore = false;
            var window = new Window
            {
                Title = "BIMaestro · Pikachu",
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
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 231, 98)),
                BorderBrush = Brushes.DarkOrange,
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
                    BorderBrush = Brushes.DarkOrange,
                    BorderThickness = new Thickness(2)
                };
                more.Click += (_, __) => { learnMore = true; window.Close(); };
                footer.Children.Add(more);
            }
            var close = new Button
            {
                Content = buttonLabel,
                MinWidth = 125,
                Padding = new Thickness(14, 7, 14, 7),
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 231, 98)),
                BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(2)
            };
            close.Click += (_, __) => window.Close();
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
                    heading = "Auto résa · Comprendre les réglages";
                    topics = new[,]
                    {
                        { "1. Choisir le cas", "Dans Familles, sélectionne le support (mur ou sol), la forme (rectangulaire ou circulaire) et l'hébergement. Chaque combinaison possède son propre type de réservation." },
                        { "2. Ajouter ton RFA", "Si ta famille est déjà chargée dans le projet, choisis son type dans « Famille et type ». Sinon, utilise « Parcourir… » puis « Charger dans le projet ». L'import ne suffit pas : vérifie le type retenu." },
                        { "3. Mapper les paramètres", "Associe les dimensions calculées aux paramètres existants de la famille. Pour un mur rectangulaire : longueur dans l'axe du mur, hauteur et profondeur. Une famille circulaire demande un diamètre. Enregistre ensuite cette famille pour le cas choisi." },
                        { "4. Ajuster le placement", "Commence avec une référence verticale automatique et un décalage de 0 mm. Si la famille est construite avec une origine décalée, corrige son placement et refais un essai sur une copie de la maquette." },
                        { "5. Contrôler le résultat", "Après la création, sélectionne la réservation, vérifie son insertion dans le mur et compare ses dimensions dans Propriétés. L'arrondi aux 50 mm est un réglage séparé ; Dynamo reste facultatif." }
                    };
                    break;
                case "history":
                    heading = "Qui a fait ça ? · Lire et restaurer";
                    topics = new[,]
                    {
                        { "1. Réduire la recherche", "Filtre par action, utilisateur, texte ou période. Si l'événement manque, élargis d'abord la période et recharge l'historique avant de conclure qu'il n'existe pas." },
                        { "2. Lire la preuve", "Dans Détails, compare l'auteur, la date, la catégorie et les propriétés enregistrées. Une carte ou un aperçu aide à repérer l'élément, mais ne remplace pas ces informations." },
                        { "3. Prévisualiser", "« Visualiser » affiche l'emplacement estimé sans modifier le projet. L'aperçu détaillé dépend des familles et types encore présents ; il peut revenir à une représentation simple." },
                        { "4. Restaurer avec prudence", "« Restaurer les éléments » recrée l'objet dans la maquette. La famille, le type, le niveau et parfois l'hôte doivent être disponibles. Vérifie le rapport si certains éléments échouent." }
                    };
                    break;
                default:
                    heading = "Couleurs et vues · Comprendre les règles";
                    topics = new[,]
                    {
                        { "1. Panneaux et arborescence", "Les couleurs des panneaux BIMaestro et le style de l'arborescence sont deux réglages distincts. Active chacun dans son onglet, puis utilise l'aperçu avant d'enregistrer." },
                        { "2. Fond et dossiers", "Le fond agit sur l'arrière-plan de l'arborescence ; les règles Dossiers colorent des dossiers existants. Elles ne créent et ne renomment aucune vue Revit." },
                        { "3. Icônes par nom", "Dans Icônes, active l'affichage puis associe une image au nom exact du dossier, par exemple « Plans d'étage » ou « Vues 3D ». Si le nom diffère dans le projet, la règle ne trouve pas sa cible." },
                        { "4. Vérifier dans Revit", "Enregistre et observe l'arborescence réelle. Si un effet manque, vérifie que la personnalisation est active, que le nom de la règle correspond au dossier et que l'icône est choisie." }
                    };
                    break;
            }

            var window = new Window
            {
                Title = "BIMaestro · Pikachu approfondit",
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
            var close = new Button { Content = "Retour à la maquette", Padding = new Thickness(16, 8, 16, 8),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 231, 98)),
                BorderBrush = Brushes.DarkOrange, BorderThickness = new Thickness(2) };
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
                var card = new Border { BorderBrush = Brushes.DarkOrange, BorderThickness = new Thickness(1),
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
