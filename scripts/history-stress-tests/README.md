# Stress de suppression massive sur copie Antibes

Ce banc est distinct du produit et des essais précédents. Il ne lance Revit que
sur instruction de l’opérateur, au moyen de `Launch.ps1`. Le produit réellement
chargé est identifié par chemin et SHA dans `ready.json`. Aucun manifeste produit,
préférence persistante, appel manuel au scheduler ou événement historique fabriqué.
Le dossier historique est redirigé uniquement en mémoire vers `run/history`.

Deux scénarios, chacun avec une copie fraîche et un processus neuf :

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-physical-early-v1 -Scenario early
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-physical-early-v1

# À préparer après la fin des mesures early, pour éviter de les perturber :
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-physical-prepared-v3 -Scenario prepared
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-physical-prepared-v3
```

`Prepare` refuse d’écraser un run existant. Le lancement ajoute temporairement
son seul manifeste `BIMaestro.History.Stress.addin`, puis le retire. Le PID doit
correspondre au marqueur du run ; le processus doit être vide au démarrage.
Les documents permis sont uniquement `run/Antibes-stress.rvt` et son dérivé
`run/Antibes-restaure.rvt`. Les processus et documents utilisateur restent hors scope.

## Historique durable : fermeture puis nouveau processus

Le scénario `durable` utilise le même périmètre physique et la même suppression
massive que `early`. Il ajoute une conversion explicite par le vrai
`ElementHistoryDurableBridge.ProcessPending(Application, force:true)`, une vague
au plus par callback Idling observateur, avec retour à Revit entre les vagues.
Le banc ne fabrique ni événements, ni recettes, ni capsules et ne réécrit pas
le journal. Les sidecars/capsules sont publiés par le produit dans `history-data`.
Le launcher transmet `BIMAESTRO_HISTORY_MANUAL_TEST=1` uniquement au processus
enfant puis rétablit aussitôt l'environnement de son propre processus. Le service
automatique de conversion attend ainsi la redirection mémoire du dossier de test ;
les vagues explicites `force:true` restent actives. Aucune variable utilisateur
ou machine n'est modifiée ; la valeur héritée est relevée dans `ready.json`.
Les durées et états avant/après chaque vague sont exportés ; cela ne prouve pas
la fluidité d'une navigation simultanée.

Exemple de préparation, à exécuter seulement lorsque l'opérateur autorise le build :

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-durable-capture-v1 -Scenario durable
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-durable-capture-v1 -ExpectedProductSha256 <SHA-produit-valide> -OpenWhenReady
```

Dans le dossier de capture :

1. Attendre `deletion.json` et `journal-ready.json`, puis écrire `convert.request`.
   `durable-status.json` indique PendingJobs/ConvertedEvents/FailedJobs/Failures,
   Complete/SessionClosed et les chemins des passerelles. Les compteurs ne sont
   pas un verdict de fidélité. Un échec reste visible ; aucune conversion forcée
   par le banc ne remplace une recette manquante.
   Le banc abandonne avant Delete si l'état de checkpoint est absent ou invalide,
   via le résolveur de document vivant du produit (des callbacks peuvent fournir
   des wrappers différents). Il n'attend pas Ready et ne prime aucun snapshot :
   une copie d'octets encore en cours reste admise. `deletion-not-attempted.json`
   conserve ce refus sans modifier le modèle. La conversion ne peut ensuite être
   déclarée complète que si ConvertedEvents couvre tous les événements du lot ;
   une file vide avec zéro conversion ne constitue jamais cette preuve.
2. Attendre `durable-conversion.json` (Complete, PendingJobs=0 et aucune erreur).
   Écrire `persist-deleted.request` : SaveAs vers le **nouveau** fichier
   `Antibes-supprime.rvt`, activation d'un document vide, Close(false) et contrôle
   fermé/invalide/absent. La copie initiale `Antibes-stress.rvt` reste intacte.
3. Le service continue par Idling après fermeture du projet, sans document test
   actif. `durable-session-closed.json` n'est écrit que si la session est fermée,
   la conversion complète et toutes les passerelles connues absentes, avec
   confirmation de leur retrait par le produit. `finished.json` termine cette
   phase. L'opérateur ferme ensuite **ce seul processus isolé**.
4. Préparer une phase de restauration neuve, **sans nouvelle copie RVT complète** :

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName antibes-durable-restore-v1 -Scenario durable -DurableCaptureRunName antibes-durable-capture-v1
./scripts/history-stress-tests/Launch.ps1 -RunName antibes-durable-restore-v1 -ExpectedProductSha256 <SHA-produit-valide> -OpenWhenReady
```

La phase deux ouvre directement le chemin exact `capture/Antibes-supprime.rvt`.
Elle exige un PID distinct, l'ancien processus arrêté, les hashes du checkpoint
et des références inchangés, ainsi que l'absence persistante des passerelles.
Elle recharge l'inventaire et les IDs de suppression de la phase un ; elle ne
réinventorie pas le projet supprimé comme référence et ne supprime rien.
Le dossier d'historique reste celui de capture, redirigé seulement en mémoire.

5. Écrire `restore.request` dans le **nouveau dossier restore**. Les vrais loader
   de plage de dates et `ToRestoreRequest` doivent restituer exactement l'ensemble
   d'événements initial. `durable-restoration-inputs.json` conserve les reçus résolus
   et refuse tout `WholeModel` ou chemin de passerelle restant avant Restore.
   Les recettes nulles restent comptées et rapportées. La présence du RVT initial
   conservé comme preuve ne remplace pas une capsule : ses reçus temporaires sont
   explicitement interdits. Attendre le bilan de restauration et sa vérification.
6. `persist.request` sauvegarde un **autre** fichier
   `restore/Antibes-restaure.rvt`, puis ferme et rouvre une nouvelle instance pour
   les mêmes contrôles. `finish.request` ferme cette copie sans nouvelle sauvegarde
   et exporte l'intégrité. Aucun chemin original utilisateur n'est ouvert ou écrit.

`durable-diagnostics.request` exporte en lecture seule l'état du service et la
liste/hash des fichiers `history-data` pendant qu'un document test autorisé est
actif. Les événements JSONL bruts de capture et les rapports précédents restent
conservés. Aucun PASS durable n'est déduit de la seule conversion ou suppression
de la passerelle : restauration, correspondances injectives, fidélité et
sauvegarde/réouverture doivent encore passer.

### Minute réelle, modifications et moitié du modèle

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName '<capture-neuve>' -Scenario durable `
  -DeletionDelaySeconds 60 -PhysicalTargetFraction 0.5 -ApplyDeterministicEdits `
  -SkipPhysicalCategories OST_AreaSchemeLines -SkipPhysicalIds 438240,440709 `
  -WitnessExclusionReason '<blocages observés, annoncés avant suppression>'
```

Le chronomètre commence après le retour de l'API d'ouverture. Le banc rend la
main à Revit entre les callbacks ; il ne prime aucun snapshot. Il exige une
visite et une capture réelles du produit pendant cette minute, sinon il refuse
la suppression. Vers 10 secondes, il déplace une plante indépendante de 100 mm
en Z ; vers 25 secondes, il interdit une jonction réelle de mur. Les transactions,
placements et relations avant/après sont exportés. L'inventaire de référence est
relevé après ces modifications. Ces objets et leurs unités physiques sont inclus
dans la sélection ; un conflit avec une exclusion annoncée fait échouer le plan.
Si aucun candidat sûr n'existe pour une modification artificielle, la tentative
est exportée avec `Skipped=true`, `Passed=false` et sa cause ; elle ne devient
jamais une modification réussie. La suppression peut continuer après la minute,
avec `EditAttemptsCompleted`, `CompletedEditsCount` et `SkippedEdits` distincts.
Une transaction réellement tentée qui échoue reste bloquante. Les IDs ajoutés et
modifiés par la vraie transaction de suppression figurent dans `deletion.json` ;
un événement Modified seul ne prouve pas la nature géométrique de la modification.

`sampling-plan.json` conserve l'univers physique initial, les exclusions et leurs
causes, les unités complètes et la sélection. Les groupes 438240/440709 excluent
leurs feuilles physiques sans compter le conteneur logique comme une instance.
La cible vaut environ la moitié du nombre d'objets admissibles restant, avec une
tolérance de cinq points de pourcentage. Les objets physiques supprimés par
cascade restent au dénominateur, même s'ils n'étaient pas sélectionnés. Le délai
et les callbacks ne constituent pas une mesure de fluidité ou de fréquence vidéo.

### Preuve partielle après des échecs de conversion

Cette voie garde les vrais compteurs Failed/Pending et les événements bruts. Elle
ne prouve ni la conversion complète, ni le nettoyage automatique, ni la
restauration de tous les objets supprimés.

1. `persist-subset.request` arrête les nouvelles vagues de conversion. Si une
   publication IO est encore active, la commande refuse et doit être relancée
   plus tard ; elle ne bloque pas l'interface en attendant. Elle retient seulement
   les événements physiques résolus par le vrai sidecar produit, avec UID source
   exact et reçus non vides portant tous un PayloadId SHA256 et une TypeVersion.
   Les autres objets et leurs causes restent dans `partial-subset.json`.
2. Le banc sauvegarde le dérivé supprimé, ferme réellement le projet et dispose
   les sources historiques. `partial-close-ready.json` fige conversion et demandes,
   identité de session, chemins, hashes et preuves de fermeture. Aucune ouverture
   de source ni conversion supplémentaire n'est permise après ce point.
3. L'opérateur lance `RemoveClosedPartialBridge.ps1 -RunDirectory '<capture>'`
   pour prévisualiser, puis la même commande avec `-Execute` dans le contexte
   autorisé. Le script ne retire que la passerelle exacte, après revalidation
   des gardes. Il conserve manifestes, jobs échoués, sidecars et preuves.
4. `finalize-subset.request` constate l'absence et écrit
   `partial-session-closed.json`. L'opérateur arrête le processus de capture puis
   prépare une nouvelle phase avec `-DurableCaptureRunName '<capture>'`
   et `-AllowPartialDurableRestore`. Aucun RVT complet supplémentaire n'est copié.
5. Le nouveau processus charge uniquement les IDs du manifeste figé, revérifie
   leurs conversions et interdit tout fallback vers une recette brute. Les
   contrôles de fidélité, sauvegarde et réouverture restent identiques. Le résultat
   distingue la preuve du sous-ensemble de tous les objets supprimés et porte
   `ManualTestBridgeRemoval=true`, `AutomaticCleanupVerified=false`.

Les types versionnés conservent un contrôle séparé : UID source identique, ou
provenance source du type et de sa famille, marqueur de version correspondant à
une capsule résolue par le produit et paramètres de définition conformes. Les
paramètres builtin/partagés/nommés, valeurs et références normalisées par Origins
sont relevés une fois par type dans l'inventaire et dans les exports de vérification.
Seuls les paramètres de libellé famille/type explicitement énumérés sont exclus
de cette comparaison, car la capsule peut ajouter un suffixe ; leurs valeurs restent
exportées. Un paramètre inconnu, non lisible ou différent empêche cette validation.
Les critères de géométrie, repères, dimensions d'instance et réseaux ne sont pas
assouplis pour accepter un type versionné.

## Périmètre exact

Le préinventaire conserve les instances `CategoryType.Model`, non spécifiques à
une vue, avec leurs protections explicites. Il vise tous les murs, sols, toits,
poutres, poteaux, équipements, réseaux, groupes, escaliers, terrains et imports
non liés admissibles. Il n’épargne aucun contexte structurel physique.

Sont protégés comme cibles directes : types/définitions/systèmes, vues et objets
de vue, niveaux/grilles/références/origines, liens Revit/CAD et nuages externes.
Les axes et contours générés, ressources de matériaux, caméras, légendes et
réglages soleil sont également protégés. Les espaces, pièces, zones et agrégats
logiques `ConduitRun` ne sont pas des cibles physiques. Un candidat sans point,
axe, transformation ou bbox vérifiable est exclu avec son motif dans l'inventaire.
Les vrais toposolids et limons avec une bbox restent ciblés.
Les lignes possédées par une esquisse sont des dépendants de leur propriétaire,
pas des racines indépendantes. Les groupes et éléments hébergés sont soumis à
`Delete` par leurs racines natives lorsque celles-ci font partie du même périmètre.
Leurs membres restent dans l’univers physique visé et dans la vérification finale.
Les dépendants et définitions supprimés par la cascade Revit sont comptés à part.

Le calorifuge n’est pas masqué du bilan : si présent et supprimé, il reste parmi
les objets manquants lorsque la politique actuelle du produit l’exclut de Restore.
Une transaction massive refusée est annulée et rapportée ; le banc ne la transforme
pas en milliers de suppressions individuelles. Toute autre collection de racines
doit être testée dans une nouvelle session, car le rollback peut invalider des caches.

## Exécution par requêtes

Dans `tmp/history-stress/<RunName>`, écrire chaque fichier avec un nouveau timestamp :

1. `open.request` : ouverture de la copie, puis suppression massive automatique.
   Le scénario **early** exécute inventaire observateur et suppression dans le même
   ExternalEvent dès que Revit le permet. Son préinventaire est chronométré et
   n’alimente aucun cache produit ; il peut laisser finir la copie disque initiale.
   Le scénario **prepared** active seulement le champ Detailed en mémoire, attend
   les vrais inventaires/compléments/racines natives, ou signale leur état exact au
   timeout de 300 s. Il ne simule jamais leur achèvement. Les gates réelles de repos
   et de premier plan peuvent donc limiter la préparation.
2. Attendre `deletion.json`, puis `journal-ready.json`. Le worker observe les
   écritures naturelles, sans flush forcé. `journal-summary.json` conserve les IDs
   manquants et les BatchIds ; un timeout de 180 s est explicite.
3. `restore.request` : relit le journal par la vraie méthode produit de plage de
   dates, sans plafond 2000 ; convertit chaque événement de la transaction par
   la vraie méthode UI `ToRestoreRequest`, puis appelle Restore avec un vrai
   callback `RefreshActiveView`. `load.json` compare aussi le loader récent plafonné.
4. Attendre `restore-result.json` et `verification.json`. Le résultat complet
   conserve les échecs annexes. `restore-again.request` permet un contrôle séparé
   d’idempotence, à condition de vérifier `Created=0` et les nombres d’objets.
5. `persist.request` : sauvegarde vers le NOUVEAU fichier `Antibes-restaure.rvt`,
   ferme le document après activation d’un document vide, rouvre le dérivé et écrit
   `reopened-verification.json`. Les bancs physiques renforcés exigent une fermeture
   effective, l'absence de l'ancien document et une nouvelle instance de document
   au chemin sauvegardé. La copie source initiale n’est pas sauvegardée.
6. `finish.request` : ferme le document test sans enregistrer, remet le champ
   Detailed à sa valeur initiale en mémoire, relève les hashes de source/copie et
   écrit `integrity.json`/`finished.json`. Revit reste ouvert sur un document vide.

Exemple d’écriture depuis la racine du dépôt :

```powershell
$run = Join-Path (Get-Location) 'tmp/history-stress/antibes-physical-early-v1'
[IO.File]::WriteAllText((Join-Path $run 'open.request'), [DateTime]::UtcNow.ToString('O'))
```

Pendant une restauration longue, `heartbeat.json`, les callbacks de rafraîchissement
et `observer.jsonl` continuent d’être exportés hors thread Revit. Le banc n’interrompt
pas artificiellement la boucle monolithique de restauration. Les phases produit
peuvent inclure recaptures `DocumentChanged`, régénérations et archives ultérieures.

## Validation et limites

`inventory.json` conserve un catalogue de tous les éléments DB et des métadonnées
des instances modèle : classe/type, point ou axe, bbox, transformation de famille,
sections MEP, ports physiques/liens, host/groupe/supercomposant/niveau et jointures.
Le coût de cette lecture tierce est séparé du coût produit et de la suppression.

La vérification couvre chaque objet physique effectivement disparu, même s’il est
un membre de racine. Elle construit un multimap des Origins, refuse doublons et
absences, et compare classe/type, placement/bbox/section, host/groupe/jointures et
graphe des connecteurs. Un changement de type in-place n’est admis que si le type
initial a été supprimé et si famille/nom/type et géométrie restent conformes. Un
type système historique conserve son UID ; le banc renforcé exige également
qu'un type historiquement absent reste absent. Le diagnostic de normalisation
reste exporté, mais ne rend pas cette divergence conforme.

Ces comparaisons ne prouvent pas l’égalité de tous les solides/contraintes/coupes,
ni le traitement de chaque geste utilisateur pendant une opération massive. Les
temps API et rafraîchissements sont des mesures d’opérations, pas des FPS. Les
cardinalités physiques, dépendants, journal, requests, résultats et vérifications
restent distinctes ; aucun succès partiel ne devient un PASS global.

Le run historique `antibes-mass-early-v3` conserve son ancien périmètre trop large
et ses preuves gelées ; il ne devient pas rétroactivement un essai physique pur.
La copie `antibes-physical-prepared-v1` a été fermée sans suppression lors du dernier
contrôle du périmètre. `antibes-physical-prepared-v2` utilise le filtre physique
renforcé décrit ci-dessus. Chaque nouveau lancement doit employer un nom neuf.

La version produit du 22 octobre 2026 porte le chargement et l'affichage récents
à 10 000 événements. Une limite atteinte est signalée, et « Compléter la période »
utilise le chargeur sans plafond sur les dates concernées. `load.json` conserve
le résultat du vrai chargeur récent de la DLL chargée, qu'il s'agisse de l'ancienne
limite 2 000 ou de la nouvelle 10 000. Le test isolé
`scripts/history-tests/TestHistoryEventLimits.ps1` valide aussi une période de
12 075 événements ; il ne mesure pas les performances du rendu réel dans Revit.

Analyse indépendante :

```powershell
./scripts/history-stress-tests/Summarize.ps1 -RunDirectory '<chemin absolu du run>'
```

## Banc renforcé après la référence physique v2

Les groupes dont toutes les feuilles sont déjà des instances physiques retenues
peuvent servir de racines logiques de la suppression. `LogicalGroupRoots` exporte
leurs feuilles ; celles-ci restent dans `SelectedPhysicalIds` et sont vérifiées
individuellement. Un groupe contenant une ressource ou une feuille protégée n'est
pas ajouté par ce mécanisme. Le dénominateur physique ne change pas.

Les objets inattendus exportent maintenant leur `Spec` complet et leurs Origins.
Les données `Toposolid.GetIntersectingElementData` sont capturées une fois par phase,
associées au terrain et à l'objet qui le coupe, puis comparées après restauration
et réouverture : type de relation, deux identités et volume en pieds cubes. La
tolérance de volume vaut `max(1e-5, abs(volume)*1e-8)` ; une exception API laisse
la vérification explicitement incomplète. Cette preuve ne compare pas tous les
solides du projet.

`-Scenario diagnostic` ouvre une nouvelle copie et n'arme aucune suppression.
Après `open.request`, l'action `diagnose.request` est en lecture seule et écrit
`baluster-diagnostic.json` : six barreaux sources, paramètres ElementId de leurs
instances/types, dépendants inverses des railings/escaliers/symboles/types et
excavations des terrains. Le run diagnostic v2 compilé le 22 octobre contient
uniquement cette action de diagnostic, sans les deux actions expérimentales
suivantes ajoutées dans la source future.

Après gel de la DLL produit corrigée, une nouvelle compilation du banc peut armer :

- `regressions.request` : deux projets temporaires testent le mapping injectif
  de deux courbes coïncidentes groupées, avec et sans provenance source préalable.
  Le vrai escalier source 556837 est copié dans un projet vide : ses six barreaux
  doivent avoir la même racine, six correspondances distinctes, les bbox et
  placements exacts et les bons garde-corps propriétaires malgré les trois paires
  coïncidentes. Des copies de familles dans des projets temporaires testent le rejet puis
  la correction d'un offset de 300 mm ou d'une rotation de 90°. Chaque cas de
  placement se termine par un rollback ; tous les projets temporaires sont fermés
  sans sauvegarde.
  Les raccords déjà connectés sont refusés ; ces tests ne remplacent pas le rejeu
  complet des réseaux.
- `probe-unset.request` : copie nativement chacune des trois gaines 604614,
  744177 et 744330, relève le type système/classement/ports, tente uniquement sur
  la nouvelle copie `RBS_DUCT_SYSTEM_TYPE_PARAM.Set(InvalidElementId)`, relève le
  retour ou l'exception exacte, puis annule toujours le groupe de transactions.
  `unset-probe-source.json` est écrit avant les essais ; `unset-probe.json` garde
  chaque état et la preuve du rollback. Aucun système partagé n'est supprimé.

Ces expériences utilisent une copie diagnostic distincte du prochain stress massif.
`finish.request` ferme toujours la copie sans enregistrer. `Prepare.ps1` conserve
les sources exactes dans `frozen-source` et leurs hashes dans
`compiled-evidence.json`, sans compiler ni installer le produit.
`Launch.ps1 -OpenWhenReady` écrit la demande d'ouverture immédiatement après avoir
contrôlé le PID de `ready.json`. Le banc refuse toujours l'ouverture si un autre
document est entre-temps apparu dans le processus ; ce document n'est pas fermé.

Après clôture d'un stress sauvegardé, un diagnostic final peut copier le dérivé
restauré et contrôler les propriétaires des six barreaux par leurs Origins :

```powershell
./scripts/history-stress-tests/Prepare.ps1 -RunName '<nouveau diagnostic>' -Scenario diagnostic `
  -SourceFile '<run massif>/Antibes-restaure.rvt' `
  -ReferenceInventoryFile '<run massif>/inventory.json' `
  -ReferenceOwnershipFile '<diagnostic source>/baluster-diagnostic.json'
```

Après ouverture de cette nouvelle copie, `diagnose.request` écrit
`restored-origin-diagnostic.json`. Les six UID proviennent de l'inventaire initial,
les deux garde-corps attendus du diagnostic source par dépendants inverses. Le
banc exige des correspondances injectives, les bons propriétaires et types,
transforms/bbox exacts, ainsi que les paires d'excavation et volumes. Il relève
`IsModified` avant/après et n'ouvre aucune transaction. Aucun ID nouvellement créé
n'est deviné ; les fichiers de référence sont identifiés par hash dans le résultat.
