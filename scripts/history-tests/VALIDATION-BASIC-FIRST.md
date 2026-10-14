# Restauration de base prioritaire — 13 octobre 2026

Le test utilisateur `suppression-20261013-095731-56b0cd1a.json` révèle 150 snapshots,
52 recettes, 187 captures différées en attente ; 98 échecs de restauration faute de
recette et une vague native de 7650,6054 ms. Les maquettes et lots des deux tests
utilisateur diffèrent : leurs taux ne constituent pas un benchmark comparable.

Correction : recette directe capturée dès la baseline ; jonctions, connexions,
circuits supplémentaires et demandes de copies natives différés. Les archives
natives et l'enrichissement passent après la couverture de base. Le catalogue
initial filtre les catégories de modèle. `BasicRecipesCaptured` distingue les
snapshots capturés des recettes directes disponibles dans le préchargement.

Validation native Revit 2024, processus isolé PID 9948 : `passed: true`.
Un mur et une famille sont supprimés, leurs recettes font un aller-retour JSON,
puis les deux sont restaurés, sans échec. La position de la famille est vérifiée.
La baseline n'enregistre ni relations, ni connexions, ni circuits supplémentaires,
et ne met pas un groupe complexe en file d'archive native. Le processus de test
ferme son document sans sauvegarde et quitte Revit.

Résultat : `tmp/codex-native-validation/2024-history-basic-regression/result.json`.
Ce test valide la restauration directe sans enrichissement. Il ne mesure pas le
débit d'une grosse maquette, le scheduler de préchargement en conditions réelles,
ni la durée maximale des archives natives. Une copie/sauvegarde native déjà
commencée reste non interruptible. La DLL de production n'est pas installée par
cette validation.

## Priorité à la disponibilité de Revit (13 octobre 2026)

`TestIdleGate.ps1` vérifie directement la logique de production : AFK hors de Revit
interdit le suivi automatique, revenir impose 2 secondes de délai, une nouvelle
entrée interrompt l'enchaînement et le débordement du compteur Windows est géré.
Les captures explicites du banc ne dépendent pas de la fenêtre au premier plan.
Le scheduler partage un budget cible de 25 ms entre phases, espace ses passages
de 100 ms et les vagues natives de 500 ms. Une copie Revit individuelle reste
non interruptible ; ces valeurs ne plafonnent donc pas la durée maximale d'un appel.

Revit 2024 `2024-history-native-pool-v2/result.json` : archives de groupes, murs,
DirectShape, escaliers, composants de murs-rideaux et connexions MEP restaurées.
Revit 2024 `2024-history-native-pool-v3/result.json` : les archives avant et après
un changement de type conservent les épaisseurs historiques distinctes des murs.

Copie enregistrée de Testi, Revit 2025 `2025-history-real-model-v3` :
125 éléments recréés et 12 inclus dans leur parent, 0 échec, soit 137/137.
Les suppressions/restaurations sont annulées par rollback et l'original est intact.
Les 96 secondes d'archives ne constituent pas une comparaison fiable avec les
51 secondes du précédent essai : un autre banc Revit tournait simultanément.
Le profil révèle toutefois 39,8 secondes de sauvegarde et une archive lourde
réenregistrée huit fois. La réutilisation est désormais interrompue au-delà de
8 Mio afin de ne pas transporter cette géométrie dans les vagues suivantes.

Essai suivant, sans autre banc ni compilation parallèle, `2025-history-real-model-v4` :
125 recréés + 12 inclus dans leur parent, 0 échec. Archives 72,6 s, sauvegardes 23,8 s,
créations de documents 8,3 s (3 documents au lieu des 11 de l'ancien essai v2).
Le gain total face aux 51 s anciennes n'est pas établi. Une copie native unique
a pris 20,8 s : les budgets coopératifs ne suppriment pas cette pause individuelle.
Le scheduler prévoit désormais jusqu'à 5 s de récupération après une vague coûteuse,
contre 500 ms minimum pour les vagues rapides. Ces pauses sont ignorées par le banc
explicite ; le temps AFK total doit être mesuré dans le scheduler de production.

## Régression de reprise et suppressions manquantes

Rapport utilisateur `suppression-20261013-135024-d0ddb698.json` :
2411 suppressions détectées, 288 snapshots présents, 2123 absents, baseline null,
856 captures de sélection différées. Le compteur visible ne représentait que les
événements conservés : EnqueueDeleted ignorait les éléments sans snapshot.

Correction : réveil périodique via ExternalEvent, baseline reprogrammée pour la
maquette active et sa nouvelle clé après Enregistrer sous ; suppressions sans
snapshot conservées avec diagnostic explicite, total du lot séparé des snapshots,
groupement par BatchId plutôt que tranches temporelles de cinq secondes.

`2025-history-tracker-regression/result.json` passed : reprogrammation après Save As,
2000 événements sans snapshot présents en queue et après sérialisation, compteur
2000 supprimés / 0 capturés, lot unique malgré des timestamps répartis sur 2000 s.
Ce banc vérifie ces régressions dans le contexte API Revit ; il ne constitue pas
une mesure du débit du timer de production en AFK. TestIdleGate passe également.

DLL Revit 2025 net10 compilée et installée avec sauvegarde de l'ancien manifeste :
SHA256 `03DA75C8CEB18FAFA4AAD84CC1B010D41231A03C060A170DFFBE5177E2791938`.
La nouvelle version sera chargée au prochain lancement de Revit 2025.

## Préparation prioritaire de la sélection

La sélection consulte d'abord les snapshots existants, même après expiration du
budget de capture de 80 ms. Les manquants sélectionnés sont traités avant le scan
général ; leurs captures légères précèdent leurs enrichissements et leurs archives
natives. Une fenêtre sans activation affiche recettes disponibles et préparations
restantes. La désélection ferme cet indicateur sans vider le cache ni les files.
La préparation reste coopérative après 2 secondes d'inactivité : une suppression
immédiate avant sa fin n'est pas garantie restaurable.

`2025-history-selection-priority/result.json` passed : sélection de 200 murs,
resélection conservant les mêmes objets snapshot et leurs dates de capture,
compteur portant sur les 200 objets et travail restant non masqué. Le banc conserve
également les contrôles de 2000 suppressions non capturées et de Save As.
Cette validation ne mesure pas la fluidité de la fenêtre de progression ni la durée
maximale d'une grosse sélection comportant des archives complexes.

## Sélection immédiate et Stop

La sélection demande un ExternalEvent immédiatement et ses passages n'attendent
plus les 2 secondes d'inactivité. Le contrôle du premier plan reste obligatoire.
Le préchargement général garde le délai d'inactivité et attend au moins 2 secondes
entre ses passages. Stop suspend les captures différées de sélection et générales,
y compris les archives natives suivantes ; Reprendre garde les données et les files.
L'appel Revit déjà engagé doit finir avant de pouvoir traiter le clic Stop.

`TestIdleGate.ps1` passe avec contrôles de sélection immédiate, pause Stop,
premier plan obligatoire et absence de contournement pour le préchargement général.
`2025-history-selection-stop/result.json` passed : 200 murs sélectionnés/resélectionnés,
snapshots et dates conservés ; en pause, une nouvelle sélection ne capture pas les
murs manquants et ne remplace pas les données existantes. Les contrôles Save As et
2000 suppressions non capturées passent également. Le temps de réaction du bouton
pendant un appel natif long n'est pas plafonné par ces tests.

## Mesures par objet et classement par coût

Capture de base/full, enrichissement et copie native émettent désormais identité,
classe, catégorie, famille/type et durée par objet. Le classement utilise des clés
de document/type/phase en mémoire, puis une estimation provisoire pour les inconnus.
Les captures de base déjà mesurées à plus de 100 ms sont reportées une seule fois ;
les enrichissements et copies natives sont triés au sein de leur priorité sélection.
Les données de reconstruction et les opérations de copie restent identiques.

`TestCaptureCosts.ps1` passed : ordre provisoire, adaptation à un groupe mesuré lent,
isolation document/phase et conservation d'une mesure lente après un retry rapide.
`2025-history-cost-ordering/result.json` passed : groupe traité avant le candidat
DirectShape initialement classé plus lourd ; les deux restaurés après suppression
et aller-retour JSON ; identités, succès et ordre réel présents dans les journaux.
Mesures de cette fixture : groupe 257,27 ms, DirectShape simple 132,04 ms. Cet exemple
montre pourquoi les classes servent seulement de point de départ, et pas de verdict
universel de complexité. `SummarizeElementCosts.ps1` produit correctement les maxima,
moyennes et identités, sur fixture synthétique et ces véritables journaux Revit.

Ancien test Testi v4 : copie maximale 20798,36 ms, suivantes 3563,97 / 2680,78 /
2614,27 ms. L'absence d'identité dans ces anciennes lignes empêche leur attribution
fiable à une classe ou un objet. Le prochain essai de maquette avec le schéma 2
permettra cette attribution. Le premier objet inconnu reste potentiellement lent.

Version Revit 2025 installée pour le prochain lancement : SHA256
`9C38F07BEB2B80201B2E4FB008F5CBE56067599174A697F302A116DE9A5834AE`.
