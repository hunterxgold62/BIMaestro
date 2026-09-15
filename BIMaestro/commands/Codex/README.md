# Codex dans BIMaestro — texte ou images vers famille RFA

Le bouton **Outils IA → Codex** ouvre une discussion dans Revit. Le compte ChatGPT est utilisé via le Codex officiel installé sur le poste. Aucune clé API ni connexion personnelle n'est incorporée dans BIMaestro.

## Créer une famille

1. Ouvrir le panneau, cliquer sur **Connexion ChatGPT**, terminer la connexion dans le navigateur et sélectionner un modèle. Les images nécessitent un modèle avec vision.
2. Décrire l'objet et ses dimensions. Facultativement, cliquer sur **Joindre une image** ou **Coller une image** (trois images maximum). Plusieurs vues et des cotes donnent un résultat plus fiable qu'une seule photo.
3. Cocher **Autoriser la lecture de la sélection et de sa géométrie**, puis **Autoriser les créations et modifications dans Revit**. Pour éviter les confirmations, activer **Appliquer directement** pour la discussion en cours.
4. Demander explicitement une nouvelle famille, sa catégorie, puis son chargement et son placement si souhaités. Exemple : « Crée une famille de transformateur électrique d'après cette image, encombrements X=1455, Y=898, Z=1800 mm. Reproduis les ailettes, isolateurs et supports. Charge-la dans le projet, sans la placer. »
5. La passerelle crée un document de famille séparé depuis le gabarit générique métrique de la version Revit active. Elle construit les pièces et matériaux, contrôle les dimensions, puis enregistre un nouveau `.rfa`. **Voir le RFA créé** sélectionne le fichier dans l'Explorateur. **Ouvrir dans Revit** affiche le nouveau RFA et rattache le panneau à cette famille, en laissant l'ancien document ouvert.

Les résultats se trouvent dans `%LOCALAPPDATA%\BIMaestro\Codex\Families\` : RFA, aperçu PNG si l'export Revit réussit, description `construction.json` et rapport. Chaque création utilise son propre sous-dossier. Aucun fichier existant n'est écrasé. Le projet ouvert n'est jamais enregistré automatiquement.

## Corriger une famille et comprendre un échec

L'assistant dispose de `revit_read_family_design` pour relire la description enregistrée à côté d'un RFA BIMaestro actif, ou de la dernière création dans le panneau. Les modifications manuelles ultérieures ne sont pas reflétées dans cette description. Les RFA déplacés sans leur `construction.json` ne disposent pas de ce mécanisme de reprise.

`revit_validate_family` construit et valide la famille dans un document temporaire, puis le ferme sans enregistrer de RFA ni charger quoi que ce soit. Il utilise les mêmes solides, matériaux et contrôles d'encombrement que la création. Il exige l'autorisation des modifications mais n'affiche pas de confirmation supplémentaire, puisqu'il ne change pas le document utilisateur. La validation n'est pas un aperçu visuel et ne garantit pas que l'enregistrement ou le chargement ultérieurs réussiront.

Les échecs indiquent l'étape et, pour une erreur de pièce, son nom ; les profils indiquent les indices d'arêtes en cause. Les écarts sur tous les axes sont renvoyés ensemble. Le contrôle mesure les boîtes alignées sur le modèle des éléments régénérés plutôt que les coins des boîtes locales de solides tournés. Les encombrements calculés d'une ancienne version ne doivent pas devenir des cotes imposées si l'utilisateur ne les a pas demandés. Les cotes explicitement demandées restent contrôlées.

Chaque échec d'outil est affiché dans le panneau et renvoyé au modèle. Un diagnostic local conserve l'outil, ses arguments et l'exception dans `%LOCALAPPDATA%\BIMaestro\Codex\Diagnostics\`, sans identifiants de connexion, images jointes ou conversation complète. Les arguments peuvent contenir une description de famille : ces fichiers restent privés, hors du dépôt, et ne sont pas envoyés à la télémétrie BIMaestro. Un échec d'écriture du diagnostic ne masque pas l'erreur originale.

Une correction produit une nouvelle version. Dans l'éditeur de familles, l'assistant peut l'ouvrir avec `revit_open_created_family` ; le chargement dans un projet n'est pas possible tant que le panneau est attaché à une famille. Il n'y a pas de remplacement automatique des instances de l'ancienne version.

## Construire sur les contours de sols sélectionnés

Dans un projet, sélectionner le ou les sols puis demander, par exemple : « Lis le contour extérieur de ce sol et crée des murs de 5 m dessus, avec le type de mur [nom]. » `revit_selection_geometry` renvoie les positions, encombrements, faces supérieures, boucles et arêtes des sols. Les coordonnées sont internes à Revit, en mm ; ce ne sont pas les coordonnées partagées. La lecture est limitée à 20 éléments et 400 arêtes au total, avec signalement explicite des contours omis.

`revit_walls_from_floor_edges` reprend directement les arêtes natives choisies, sans reconstruction des coordonnées par le modèle. Il vérifie que les sols sont encore sélectionnés et que leurs contours n'ont pas changé, puis crée les murs en une transaction annulable. Le niveau utilise son élévation interne ; l'axe du mur suit la limite choisie, avec la base au-dessus du sol et le décalage vertical demandé. Le projet n'est pas enregistré automatiquement.

Limites : segments et arcs de faces horizontales, 200 murs maximum, types de murs de base existants. Pas de fusion automatique des sols, pas de gestion des pentes ni de créneaux. Les réservations sont visibles dans la lecture et ne doivent pas être entourées sans demande. Pour plusieurs sols voisins, l'assistant doit choisir les arêtes utiles ; deux arêtes exactement superposées dans un lot sont refusées. Il n'y a pas de contrôle général des collisions avec des murs déjà présents.

## Géométrie et qualité

Le moteur comprend blocs, cylindres, tubes, cônes/troncs de cône, sphères, profils polygonaux extrudés et profils de révolution. Chaque forme peut être orientée et positionnée. Les évidements booléens et répétitions permettent des supports ajourés, cuves horizontales, ailettes, isolateurs et assemblages détaillés.

La famille contient des **solides Revit analytiques distincts**, avec une sous-catégorie par groupe de pièces et des paramètres de matériaux. Elle ne repose pas sur un maillage de triangles importé. Les dimensions d'encombrement sont calculées et inscrites comme valeurs de référence non pilotantes. La géométrie reste fixe : cette version ne génère pas un réseau de contraintes dimensionnelles ni des connecteurs MEP. Pour changer la géométrie, demander une nouvelle version ; les versions précédentes sont conservées.

Avant de sauvegarder, le moteur refuse les descriptions invalides, profils auto-intersectés, dimensions négatives, répétitions sans espacement et certaines superpositions exactes. Les dimensions cibles non nulles sont contrôlées avec une tolérance de 0,5 % (minimum 1 mm). Il n'effectue pas de détection générale des collisions entre toutes les pièces. Les contacts, détails, faces cachées et cotes supposées doivent être vérifiés sur l'aperçu et dans Revit : aucune promesse de fidélité parfaite ne peut être faite à partir d'une photo.

Limites de calcul : 750 solides, 200 groupes, 150 répétitions par groupe, 32 matériaux, 12 évidements par groupe et 128 sommets par profil. Les arêtes polygonales et dimensions utiles doivent mesurer au moins 1 mm. Une erreur géométrique annule la nouvelle famille avant sa sauvegarde. L'opération Revit peut occuper l'interface pendant la construction ; **Arrêter** ne peut pas interrompre une transaction synchrone déjà en cours.

Le modèle reçoit le rapport et, si disponible, l'aperçu PNG du résultat. Il peut les examiner et signaler des écarts. Ce contrôle n'est pas une certification BIM et ne remplace pas une vérification humaine. Les hypothèses du modèle sont conservées dans le rapport.

## Chargement et annulation

La famille peut être chargée dans le projet attaché au panneau. Si une famille du même nom est déjà ouverte ou chargée, la nouvelle version reçoit un suffixe unique, signalé dans le rapport ; les anciennes instances restent inchangées. Une collision détectée au moment du chargement est refusée et le RFA reste disponible séparément. Un placement à l'origine doit être demandé explicitement ; il utilise le niveau le plus proche de Z=0 et son élévation.

Le chargement et le placement sont regroupés pour l'annulation. En cas d'échec, le projet revient à son état précédent et le RFA déjà créé reste sur disque. Ctrl+Z annule les modifications du document mais ne supprime pas le fichier créé.

Les anciens outils restent disponibles pour modifier la famille déjà ouverte : lots de blocs/cylindres, changement d'un paramètre de longueur existant. Leur géométrie n'est pas enregistrée automatiquement. Les modifications de projet sont limitées au chargement/placement de familles et aux murs sur contours décrits ci-dessus.

## Cases d'autorisation et images

- **Lecture** : autorise, sur demande, le nom du document, les 20 premiers éléments sélectionnés (30 paramètres chacun, textes tronqués à 300 caractères), leurs positions/encombrements et les contours des sols, ainsi que les types de murs disponibles. Dans une famille : jusqu'à 100 paramètres de longueur et la description constructive BIMaestro disponible. Aucune capture d'écran automatique ni lecture complète du modèle. Décocher bloque les lectures suivantes, sans effacer ce qui a déjà été transmis.
- **Créations et modifications** : autorise les outils de famille, dont leur validation temporaire, la création de nouveaux fichiers RFA et leur éventuel chargement, ainsi que les murs natifs sur contours de sols. Par défaut, une confirmation résume la famille ou le lot à créer.
- **Mode direct** : dispense de validation pour les opérations autorisées de la discussion en cours. Il est remis à zéro quand la discussion est renouvelée, déconnectée, fermée ou quand les autorisations sont retirées. Il n'autorise aucun code C# arbitraire.
- **Images jointes** : leur ajout constitue une sélection explicite pour l'envoi au modèle, distincte de la lecture du contexte Revit. Les images sont réduites à 1600 pixels sur leur plus grand côté et réencodées en PNG sans reprise des métadonnées. Limites : 20 Mo par fichier d'entrée, 8 Mo après préparation. Elles sont retirées de la zone de saisie après envoi mais restent dans le contexte de la discussion côté modèle.

## Abonnement, confidentialité et Git

La connexion ChatGPT est imposée ; le panneau ne propose pas de clé API, de bascule vers l'API ou d'achat de crédits. L'usage consomme les limites Codex du compte. Les crédits supplémentaires éventuellement déjà présents et les réglages de facturation du compte continuent de s'appliquer : le panneau n'est pas un plafond financier indépendant.

Le coffre d'identifiants Windows conserve la connexion (`keyring`). Le profil local dédié est `%LOCALAPPDATA%\BIMaestro\Codex\home`, hors du dépôt. Les serveurs MCP, hooks et clés API du profil Codex habituel ne sont pas importés. Shell et recherche web sont désactivés, environnement d'exécution désactivé pour les tours, sandbox en lecture seule. Les écritures de RFA sont exécutées exclusivement par les outils Revit dans leur dossier dédié, pas par le shell de l'agent.

Messages, images et données Revit autorisées sont transmis à OpenAI. Le pont local ne rend pas le modèle IA local. Appliquer les règles de confidentialité de l'entreprise. Les discussions sont éphémères côté App Server ; cela ne garantit pas l'absence de conservation côté OpenAI ou de diagnostics dans le runtime. Le contenu du tchat n'est pas envoyé à la télémétrie BIMaestro.

Le code publié sur Git ne publie pas la connexion utilisateur. Ne pas ajouter le dossier local Codex ni ses sorties privées au dépôt. Les exclusions `.gitignore` ne retirent pas des secrets déjà suivis ou déjà présents dans l'historique. Le reste du dépôt n'a pas fait l'objet d'un audit de secrets dans cette tâche.

## Validation technique et limites de validation

Tests depuis PowerShell 7 : `./scripts/codex-tests/Run.ps1 -RealCodex`. Ils couvrent le protocole, les erreurs JSON nulles qui causaient le premier crash, le mode direct, les images et les validations de construction. Le cas `scripts/codex-tests/transformer.design.json` décrit un transformateur de démonstration de 103 solides et cinq matériaux. Il sert à tester la description et ne constitue pas un RFA déjà vérifié dans Revit.

La comparaison des documents utilise `Document.Equals`, et les notifications ne laissent pas remonter d'exception de protocole dans le dispatcher Revit. La compilation WPF utilise `AlwaysCompileMarkupFilesInSeparateDomain=false` pour éviter un chargement de ressources depuis des assemblies de référence.

La compilation et les tests hors Revit ne valident pas le résultat natif. Les régressions vérifient aussi la transmission des erreurs de pièces au modèle et au panneau, l'absence de faux fichier créé après validation et le rejet des appels provenant d'une ancienne réponse. Les nouveaux parcours restent à tester dans Revit : reprise d'une description, validation puis ouverture d'une révision, tracé de murs sur un sol horizontal et un arc, réservations, deux sols voisins, niveau utilisant des coordonnées partagées, sélection ou contour modifié entre lecture et création, et Ctrl+Z du lot. La géométrie produite à partir d'une référence nécessite une comparaison visuelle dans Revit.

Références : [Codex App Server](https://developers.openai.com/codex/app-server), [abonnements et usage](https://developers.openai.com/codex/pricing).
