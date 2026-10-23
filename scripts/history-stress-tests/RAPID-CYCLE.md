# Suppression après modification, puis récupération à J+14 simulé

Ce scénario natif Revit 2025 complète le banc durable. Il travaille sur une copie
Antibes et un magasin historique isolé. Il ne lance aucun amorçage ou capture de
production artificiels, et ne fabrique aucun événement historique.

La compilation du banc ne constitue pas une validation Revit. Une réussite exige
les deux processus réels et toutes les preuves demandées par le résumé strict.

Le contrat nominal est `UsesExistingModelDefinitions=true` : les recettes JSON
réemploient les définitions toujours présentes dans le projet, avec **exactement
les UID de type et de famille d'origine**. Les paramètres et noms des types doivent
rester inchangés. Aucun import de famille, capsule RVT/RFA, doublon historique ou
marque de provenance ne remplace une définition purgée. Si le type ou la famille
a disparu, la restauration doit indiquer explicitement cette indisponibilité.

## Préparer et exécuter la capture

Depuis la racine du dépôt, après construction et déploiement coordonné de la DLL
produit testée dans le processus isolé :

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-rapid-capture-r1 -Scenario durable -RapidModifiedDelete
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-rapid-capture-r1 -ExpectedProductSha256 <SHA256_PRODUIT> -OpenWhenReady
```

Le lancement utilise les gardes existantes : nouveau dossier, nouveau processus,
contrôle du SHA produit et `BIMAESTRO_HISTORY_MANUAL_TEST=1`. Ne pas lancer un second
banc simultanément avec la même inscription d’observateur.

La préparation différée du produit reste suspendue depuis l’initialisation du
processus jusqu’à la fin de la suppression. Les événements `DocumentChanged`
restent branchés et leur capture immédiate fonctionne normalement. La copie
fichier d’ouverture peut finir pendant ce temps ; elle ne représente pas les
trois états modifiés ensuite.

Le banc sélectionne exactement trois objets réels :

- famille indépendante déplacée de 50 mm et commentaire changé ;
- mur droit sans insertion, extrémité libre raccourcie de 50 mm et commentaire changé ;
- Pipe/Duct droit avec un bout libre raccourci de 50 mm et commentaire changé.

Les IDs préférés de la copie connue sont 474717, 556835 et 913245. S’ils ne sont
pas admissibles, le choix est déterministe et les IDs réels sont enregistrés.
L’absence d’un témoin fait échouer le scénario ; il n’existe aucun « skip réussi ».

L’inventaire indépendant de référence précède les modifications. Il ne prépare
pas les recettes du produit. Après chaque transaction d’édition, le banc relit
le véritable état commis et mesure le temps du Commit, y compris les callbacks
`DocumentChanged` du produit. La suppression se produit au callback ExternalEvent
suivant de l’observateur, sans passage de préparation différée entre les deux.
Le délai réellement écoulé figure dans `rapid-deletion-gap.json` ; aucune durée
de zéro milliseconde n’est supposée.

Après suppression, le banc reprend la préparation et utilise le convertisseur
durable de production. La conversion forcée est réservée à cette phase **après**
la suppression. Les trois événements doivent tous être réellement `Ready`.
Puis il sauvegarde l’état supprimé, ferme le document et attend le retrait
automatique des passerelles. Attendre `durable-session-closed.json` et
`finished.json`, puis arrêter le processus de capture avec les gardes existantes.
Le deuxième processus refuse de démarrer sa validation si ce PID vit encore.

## Nouveau processus, maintenance J+14, restauration et réouverture

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-rapid-restore-r1 -Scenario durable -DurableCaptureRunName antibes-rapid-capture-r1
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-rapid-restore-r1 -ExpectedProductSha256 <MEME_SHA256_PRODUIT> -OpenWhenReady
```

La phase suivante hérite du scénario de capture. Elle tient des handles de
lecture exclusifs sur la source de test et la copie initiale, depuis
l’initialisation jusqu’à la fin de la sauvegarde/réouverture restaurée. Une
tentative de lecture indépendante vérifie leur inaccessibilité. Aucune source
originale active de l’utilisateur ne doit être choisie comme `SourceFile` :
utiliser la copie Antibes de test prévue. Les anciennes passerelles doivent être
physiquement absentes. Le magasin historique conserve les recettes JSON et le
journal ; aucune charge utile RVT/RFA persistante n'est admise dans ce scénario.

Le worker lit les vrais événements de suppression et fixe uniquement
`ElementHistoryClock.UtcNow` à `max(Ts suppression) + 14 jours`. Le hook de test
est gardé par la variable d’environnement du processus isolé. Il appelle
véritablement `ElementHistoryTracker.CompressOldFiles()` avant les loaders de
restauration, puis vérifie :

- suppression des journaux quotidiens devenus anciens ;
- création des archives gzip ;
- mêmes événements, mêmes IDs et mêmes Ts, mêmes SHA256 des lignes JSON.

L’horloge Windows, les événements et les dates de fichiers ne sont pas falsifiés.
**J+14 est une simulation de l’horloge de maintenance/chargement. Ce test ne
prétend pas avoir attendu quatorze jours de stockage réel.**

La restauration passe par le loader de plage et le `ToRestoreRequest` réel de
l'interface. Tous les événements et leurs conversions UI restent en preuve ;
seuls les objets physiques admissibles effectivement supprimés sont transmis à
Restore. `rapid-request-selection.json` doit prouver individuellement les autres
enregistrements : métadonnées live d'exclusion cohérentes avec le catalogue
initial, ou classe/catégorie protégées du catalogue et refus explicite de
reconstruction. Ces deux cas restent distincts. Un UID vide, une recette absente
ou un échec générique ne suffisent jamais à exclure un objet physique.

Les lignes d'axe générées sont consignées dans `GeneratedModelRecords`. La classe
API doit être exactement `Autodesk.Revit.DB.Element`, dans la catégorie native
autorisée et déjà représentée par un enregistrement protégé réellement supprimé
du catalogue initial. Le nombre admis est borné au nombre distinct supprimé ;
aucun nom « Axe », FamilyInstance ou ModelCurve n'est utilisé comme exclusion.
Pour le scénario Pipe actuel, le résumé vérifie la catégorie -2008045 et recoupe
ses références au catalogue source. Les autres nouveaux objets restent dans
`UnexpectedModelObjects` et font échouer le cycle.

Le banc vérifie les classes, définitions de type, paramètres
d’instance, points/courbes/transforms, bbox, sections MEP, ports et connexions,
hôtes/niveaux/groupes, joins et relations disponibles. Il sauvegarde, ferme et
rouvre un document réellement distinct puis répète les vérifications. Les
limites du comparateur géométrique existant restent explicites : aucune égalité
exhaustive des solides, ni preuve de toutes les contraintes de Revit.

La preuve `rapid-existing-definition-inputs.json` lit le vrai flag dans chacune
des trois requêtes issues du loader/UI, puis vérifie récursivement l'absence
d'artefacts Native/OpeningFallback. Les deux vérifications indépendantes exigent
`TypeUidMatchesSource=true`, `FamilyUidMatchesSource=true` et
`ExistingModelDefinitionsUsed=true`, ainsi que l'égalité des UID et des paramètres
de type. Les contrôles géométriques et des paramètres d'instance sont conservés,
y compris leur tolérance numérique préexistante.

Après réussite des contrôles positifs, sauvegarde et réouverture, le banc ouvre
une **autre copie du RVT positif sauvegardé**, sous `BeginHistoricalScope` et avec
propriété historique vérifiée. Il supprime la famille témoin et ses types dans
une vraie transaction commise, puis passe sa requête d'origine au vrai Restore.
Il exige `Reason=definition_missing`, un Detail commençant par
`ExistingModelDefinitionMissing:` contenant l'UID de type, zéro objet créé ou
réutilisé et l'absence persistante du type, de la famille et du témoin. Un échec
générique n'est pas accepté. La copie est fermée sans sauvegarde ; les SHA256
du RVT positif et du checkpoint supprimé doivent rester identiques. Le résultat
indépendant figure dans `rapid-purge-refusal.json`.

`rapid-storage-capture.json` et `rapid-storage-final.json` inventorient le vrai
magasin après retrait des passerelles et après réouverture positive : zéro
fichier RVT/RFA, zéro nouveau payload natif, octets UTF-8 exacts des trois recettes
dans leurs événements naturels et octets du journal (compressé à J14). Les copies
RVT de test ne sont pas comptées comme données d'archive. La mesure finale précède
le scénario négatif de purge. La copie de sécurité d'ouverture reste autorisée
pendant la session et doit avoir disparu à la clôture.

## Résumé strict

```powershell
./scripts/history-stress-tests/SummarizeRapidCycle.ps1 -CaptureRunName antibes-rapid-capture-r1 -RestoreRunName antibes-rapid-restore-r1
```

Le script exige la même DLL produit dans les deux processus, une capture
persistée exacte, les gardes d’inaccessibilité, la compression J+14 réelle, les
vérifications après restauration et réouverture, l’intégrité des fichiers et
l’absence de fichiers d’erreur. Il conserve `rapid-cycle-summary.json` et échoue
si une preuve manque. Les temps de callbacks et du spool synchrone éventuel sont
inclus dans les temps de Commit ; la latence du worker de stockage asynchrone
est observée séparément par l’attente du journal et des reçus `Ready`.

Le périmètre est celui des trois témoins modifiés et de leurs victimes physiques
admissibles en cascade. Ce résultat ne démontre ni la récupération de toutes
les catégories Revit, ni l’absence de gêne pendant une navigation humaine.
