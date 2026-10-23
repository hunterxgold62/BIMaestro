# Essai interactif Antibes — 22 octobre 2026

Les **trois objets demandés ont été restaurés et vérifiés** sur la copie
`Antibes-interactif.rvt`, puis cette copie a été fermée sans enregistrement.
Le lot complet contient aussi quatre échecs sur des IDs annexes : ils restent
présents dans les preuves et ne sont pas comptés comme des réussites.

## Dispositif et preuves

Run : `tmp/history-interactive/antibes-interactive-v1`, Revit 2025 isolé, PID 27728.
Le banc utilise les vrais événements et hooks BIMaestroApp ; il ne force aucun
passage du scheduler, capture initiale, ajout au journal ou flush disque.
Deux suppressions passent par un ExternalEvent ; la troisième par la sélection
Revit et la véritable touche Suppr. La restauration relit les événements
persistés, utilise `ElementHistoryWindow.ToRestoreRequest`, puis la vraie API
`ElementHistoryRestoration.Restore` de l’assemblée déjà chargée.

Produit effectivement chargé :
`C:/Users/plemert/AppData/Local/BIMaestro/Bin/Revit2025-Clash3D-C9CED231881F/BIMaestro.dll`.
SHA-256 : `C9CED231881FBCDE747CCD3E3CAEA34907B4A76B3868CF176899ED45A0DA34A0`.
Cet essai ne modifie ni le produit ni le manifeste BIMaestro. Seul le dossier du
journal est redirigé en mémoire vers `run/history` dans ce processus de test.

Les bruts sont dans `observer.jsonl`, `history/element-history-2026-10-22.jsonl`
et `history/history-performance/`. La ligne 7963 de `observer.jsonl` contient le
résultat réel ; les lignes 7964–7966 contiennent les trois vérifications.
`interactive-summary.json` est un document dérivé, reproductible avec :

```powershell
./scripts/history-interactive-tests/Summarize.ps1 -RunName antibes-interactive-v1
```

## Suppressions et restauration

Toutes les heures ci-dessous sont en UTC, soit heure de Paris moins deux heures.
`DocumentOpened` survient à 13:29:38.071 ; le premier Idling du document actif à
13:29:50.707. Les demandes de suppression prévues à +1 s et +3 s attendent que
Revit puisse exécuter leur ExternalEvent : leurs délais réels sont **12,826 s** et
**13,330 s** après `DocumentOpened`.

| Objet | ID initial | État avant suppression | Journal visible, borne haute | Restauration |
|---|---:|---|---:|---|
| Brique de référencement | 474717 | Aucun snapshot ; inventaire 1/6113 | 261,35 ms | PASS : classe, UID de type et point identiques |
| Plante ARBOL TIPO 01 | 520659 | Aucun snapshot ; inventaire 7/6113 | 314,70 ms | PASS : classe, UID de type et point identiques |
| IPN200, suppression par l’interface | 1010815 | Snapshot de préchargement ; dernière sélection réelle | 513,73 ms | PASS : classe, UID de type, extrémités et milieu de l’axe identiques |

Les deux premières suppressions sont conservées grâce à la copie d’ouverture :
le JSON relu contient `snapshotMissing=true`, `SnapshotFound=false`,
`PreloadStillQueuedAtDeletion=true`, une recette `native_archive`,
`WholeModel=true` et le `SourceElementId` exact. La copie initiale était prête en
**430,39 ms**. Les transactions de suppression durent 307,24 ms et 52,67 ms.
L’IPN est supprimé par « Supprime la sélection » à 13:32:41.972.

La vraie restauration termine à **13:33:36.697**, après **12,914 s** : sept
requêtes, trois objets créés, quatre échecs annexes. Les trois contrôles de
géométrie utilisent un seuil de `1e-5` pied ; ici les valeurs enregistrées et
restaurées coïncident exactement. L’IPN réussit par sa recette directe, sans
déclenchement de son secours d’ouverture. Ces objets n’ont aucune connexion MEP
à rétablir ; le résultat ne contient aucun échec de connexion ou de relation.

| Objet | Nouvel UniqueId |
|---|---|
| Générique | `5b588b62-90a1-4dc6-bd0b-54cd03f66d34-001103b6` |
| Plante | `5b588b62-90a1-4dc6-bd0b-54cd03f66d34-001103b3` |
| IPN | `5b588b62-90a1-4dc6-bd0b-54cd03f66d34-001103b8` |

Les échecs annexes sont **1012786 et 1110420**, refusés à l’hydratation car absents
comme objets de modèle éligibles dans la copie d’ouverture, et **1115045 et
1115046**, sans recette ni identité exploitable. Ces deux derniers sont créés
par un commit puis supprimés au commit suivant. Leur classe exacte n’a pas été
relevée ; on ne prétend donc pas les identifier plus précisément. Ils ne sont
aucun des trois objets visés, mais le lot global reste `Failed=4`.

## Navigation et coûts observés

L’inventaire de base atteint 6113/6113 à **13:30:09.232**, soit **31,161 s** après
`DocumentOpened` et **18,525 s** après le premier Idling actif : 4195 captures,
zéro échec, puis 4197 snapshots en tenant compte des modifications. Les 232
phases produit `preload` ont un P95 de **30,61 ms**, un maximum de **78,71 ms** et
aucune durée supérieure à 100 ms. Ces durées de phase n’incluent pas tous les
coûts possibles de l’add-in ou de Revit.

La vue 3D est activée à **13:31:05.932**, après la fin de cet inventaire. Le stage
`live-3d-zoom-navigation`, de 13:31:29 à 13:32:27, contient 1530 passages du
Dispatcher, dont 24 observent une nouvelle entrée Windows avec ce Revit au
premier plan : écarts P95 **64,51 ms**, maximum **1205,77 ms**, 19 au-dessus de
100 ms et un au-dessus d’une seconde. Ce sont des écarts de Dispatcher à priorité
Background, **pas des FPS, des temps de peinture ou une attribution exclusive
au plugin**. Le timestamp de dernière entrée peut omettre des gestes intermédiaires.

Des captures natives en arrière-plan ont aussi un coût réel : six phases
`native_archive`, maximum **2105,67 ms**, dont deux supérieures à une seconde.
La première finit à 13:32:44.669, après la suppression UI ; les suivantes forment
des vagues à 13:32:47–51 et 13:33:51–56, après suppression et restauration.
Les racines concernent notamment les IPN modifiés par ces opérations. Ces coûts
ne sont pas présentés comme des pauses prouvées pendant les zooms. Les phases
`native_root`, `native_create` et `native_save` peuvent être imbriquées et ne
doivent pas être additionnées aux phases englobantes.

Le marqueur humain de viewport utilisable n’est écrit qu’à 13:30:07.143 : les
deux suppressions automatiques le précèdent. Ce marqueur peut être tardif et ne
donne pas l’heure exacte de la première image utilisable. L’essai prouve la
récupération d’objets supprimés avant leur capture ; il **ne prouve pas la
fluidité de navigation 3D pendant l’inventaire initial**, ni une absence générale
de pauses. Les sondes d’état de l’observateur ont un P95 de 0,265 ms et un maximum
de 10,42 ms. La mesure du journal inclut file d’attente, écriture et polling à
100 ms ; elle reste une borne haute. `observer-errors.txt` conserve une collision
de partage lors d’une lecture du journal par l’observateur (`File.ReadLines`) :
le worker d’observation reprend au polling suivant. Ce diagnostic du banc ne
constitue pas un échec de restauration ; il peut retarder une première lecture.

## Clôture et intégrité

Le contrôle visuel a été interrompu par Échap utilisateur. Après autorisation de
reprise, les preuves existantes ont confirmé les trois restaurations déjà
terminées. Aucun nouvel essai ni changement produit n’a été nécessaire.
`finish.request` a ensuite fait fermer uniquement la copie désignée, via
`Document.Close(false)`, après activation d’un document vide. L’événement
`copy_closed_without_saving` est enregistré à **13:36:07.011** et `finished.txt`
est présent. Aucun `command_error` n’est enregistré.

Le SHA-256 de la copie fermée et celui de son fichier source de banc
`tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt` sont
identiques à la valeur initiale :

`EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA`.

Les trois restaurations sont donc vérifiées en mémoire dans la copie de test ;
la fermeture sans enregistrement préserve les fichiers sur disque. La validation
de géométrie couvre les points ou l’axe, sans constituer une comparaison exhaustive
de tous les solides, paramètres, contraintes ou cas MEP de la maquette.
