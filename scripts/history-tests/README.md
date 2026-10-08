# Aperçus historiques reconstruits

Tous les modes conservent une recette versionnée pour les instances ponctuelles
sur un niveau (libres ou hébergées), les murs de base verticaux, y compris à profil
modifié à une boucle, et les sols définis par des lignes/arcs, avec leurs boucles
intérieures et leurs points/lignes de modification de forme.
Les identités des types, niveaux, hôtes et références de paramètres sont des UniqueId.
Les unités numériques sont les unités internes Revit.

La recette d'un mur/sol hôte peut aussi être conservée lorsqu'il est joint ou découpé,
avec un maillage séparé pour l'aperçu détaillé. Les formes non prises en charge
(familles en place, familles sur plan de travail sans face enregistrée, murs
inclinés/attachés ou à plusieurs boucles de profil, sols avec flèche de pente) restent
sur l'ancien aperçu. Les toitures par tracé, plafonds plats, familles sur face,
sur deux niveaux, basées sur une courbe et adaptatives possèdent désormais des
recettes natives ; les références de face sont vérifiées géométriquement après
reconstruction du support. Les formes restent signalées en erreur quand les données
requises manquent ; aucun maillage ne se substitue à une restauration native.
La recette utilise le type dans son état actuel et ne sauvegarde pas sa définition.

Une sous-transaction crée brièvement l'élément, copie les coordonnées des triangles
et annule toutes les mutations avant de créer le DirectShape d'aperçu. Le nettoyage
des aperçus reste identique. Si l'original a été rétabli et existe encore, son UniqueId
sert à le sélectionner directement.

## Restauration native définitive

**Restaurer les éléments** traite la ligne/le cluster sélectionné, ou les lignes
sélectionnées dans le tableau. **Restaurer sélection** traite uniquement les éléments
sélectionnés dans le détail du cluster. Les familles, murs et sols sont recréés comme
de vrais objets Revit, conservés après enregistrement/réouverture, avec de nouveaux
identifiants Revit. Les hôtes sont restaurés avant leurs instances.

Les tuyaux, gaines, conduits électriques et chemins de câbles rectilignes conservent
leurs extrémités en coordonnées absolues (y compris pentes et verticales), type,
niveau, section et type de système. Les flexibles circulaires de tuyauterie/gaine conservent
leurs points et tangentes. Les accessoires/familles admissibles conservent aussi
leur orientation 3D. Les connexions physiques enregistrées sont rétablies dans une
seconde passe, après création de tout le lot, vers les voisins existants ou restaurés.
Les deux connecteurs doivent correspondre à leurs positions et directions historiques,
être libres et coïncider. Un voisin déplacé ou déjà connecté ailleurs n'est pas modifié.
Les échecs de connexion sont signalés sans supprimer les éléments recréés. On peut
restaurer le voisin manquant ultérieurement ; ses références retrouvent les éléments
déjà restaurés via leurs marqueurs d'origine. Aucun raccord de remplacement n'est
inventé lorsqu'une connexion directe ne peut pas être rétablie.

Le calorifuge reste exclu à la demande explicite de l'utilisateur, confirmée le
6 octobre 2026 : ni les isolants natifs de tuyau/gaine, ni les familles nommées
« calorifuge » ne sont restaurés ou comptés comme échecs. Les modèles
génériques suivent le placement réel de leur famille ; les lignes/arcs de modèle
autonomes enregistrent leur géométrie, plan et style de ligne.
Les familles ponctuelles admissibles en miroir conservent désormais leur repère
réfléchi ; une rotation seule ne peut pas reproduire une symétrie.

Les objets de modèle qui ne possèdent pas de recette géométrique passent désormais
par une archive RVT native, sans liste limitative de classes : escaliers, garde-corps,
groupes imbriqués, murs rideaux, familles en place, objets importés et autres formes
complexes utilisent les capacités de copie de Revit. Les composants sont archivés
avec leur parent et retrouvent leur identité historique après la copie. Les groupes
sont recomposés avec leurs composants utiles, en excluant le calorifuge ; les types
de groupe sont isolés pour préserver les autres occurrences. Le calorifuge natif
hébergé est retiré uniquement dans le document d'archive. Les connexions d'un groupe
MEP vers l'extérieur sont enregistrées sur leurs composants et rétablies séparément.
Les systèmes logiques de tuyauterie/gaine ont une recette dédiée qui conserve leur
type, nom et membres après la reconstruction des connexions physiques.

Les archives sont produites pendant Idling, hors transaction du modèle source,
et enregistrées de manière immutable dans `native-history` sous le dossier
d'historique actif. Les fichiers suivent le dossier partagé même lorsque sa lettre
de lecteur change. Une modification invalide le cache courant et programme une
nouvelle capture, sans réécrire les archives historiques. Un refus de copie Revit
est enregistré avec son diagnostic exact ; il ne produit ni faux succès ni substitut
en maillage. Une archive doit être enregistrée avant la suppression concernée.

Un marqueur Extensible Storage persistant associe les éléments restaurés à leurs
origines : un second clic ne crée pas de doublon. Une nouvelle suppression permet
une nouvelle restauration. Les marqueurs suivent l'annulation des transactions.
Les opérations réussies forment une seule action d'annulation Revit ; les erreurs
par élément sont annulées séparément et présentées dans le bilan.

Pour une restauration définitive, aucun maillage ne remplace un élément natif.
Les événements antérieurs sans recette, les formes non prises en charge et les
types/niveaux/hôtes manquants sont explicitement signalés comme non restaurés.
La capture de la recette ne dépend plus du choix Simple/Détaillé.
L'ordre de restauration suit désormais les dépendances sélectionnées (supports,
parents, références de paramètres), et pas seulement « murs avant familles ».
Les lignes d'esquisse enregistrées avec un sol, plafond, mur à profil ou toit sont
identifiées sur leur parent recréé et portent un marqueur d'origine persistant :
elles ne sont pas comptées comme échecs ni recréées une deuxième fois.
Le préchargement en arrière-plan fonctionne aussi en mode Simple. Les limites de
temps/nombre des aperçus ne tronquent plus les recettes d'une grande sélection ni
les suppressions en lot ; les éléments ajoutés/modifiés hors budget détaillé ont
une capture simple de secours. L'ouverture doit néanmoins laisser au préchargement
le temps de parcourir la maquette : un élément supprimé par un autre outil avant
toute capture ne peut pas être reconstitué rétroactivement.

Un refus/échec de capture est maintenant sauvegardé dans `captureFailure` avec la
classe réelle et, pour les familles, le mode de placement/miroir/sous-composant.
Une erreur de lecture d'une connexion ne supprime plus la recette géométrique du
tronçon : les liens incomplets sont enregistrés comme avertissements de capture.
Le bilan écrit automatiquement un rapport JSON exhaustif dans
`%LOCALAPPDATA%/BIMaestro/HistoryReports` et affiche son chemin. Ce rapport contient
tous les résultats par élément et toutes les erreurs de connexion (sans limite de
de lignes), ainsi que leurs catégories/identifiants. Une erreur d'écriture du
rapport ne remet pas en cause une restauration déjà validée.

## Banc natif

Validation du 6 octobre 2026 dans des processus Revit 2025 distincts, sur des
maquettes de test : `2025-history-all-model-v5/result.json` contient 34 contrôles
réussis (régression complète et extension). `2025-history-all-model-v6/result.json`
contient 10 contrôles réussis, dont une deuxième suppression/restauration, la
résolution des anciennes identités d'esquisse et l'exclusion du calorifuge.
Ces résultats se trouvent sous `tmp/codex-native-validation/`.
`2025-history-native-archive-final/result.json` contient 50 contrôles réussis :
régression complète, archives natives, escaliers/volées, garde-corps, murs rideaux/
panneaux, groupes imbriqués avec calorifuge exclu, groupe MEP relié à un voisin
existant, systèmes logiques tuyauterie/gaine, deuxième suppression/restauration
et conservation des anciennes identités. Le banc compile également pour Revit 2024.
La DLL Revit 2025 Release a également été compilée après les dernières corrections.
Les recettes n'ajoutent pas une copie complète du modèle au RVT : elles sont
sérialisées dans l'historique JSONL existant. Exemples mesurés, en JSON indenté :
mur 764–899 octets, famille environ 1,7–2 Ko, toiture simple 2 947 octets,
sol avec ouverture 4 119 octets. Ce sont des tailles de recette uniquement,
hors enveloppe d'événement, éventuel maillage d'aperçu et compression d'archive.
L'extension native produit en plus des fichiers RVT externes pour les objets
complexes. Dans cette validation, huit fichiers (deux séries de captures) totalisent
4 001 792 octets, entre 487 424 et 528 384 octets par fichier. Ces mesures concernent
des petites maquettes de test ; la taille dépend des géométries et définitions
nécessaires, et les anciennes archives restent conservées pour les anciennes
suppressions. Les objets ordinaires continuent d'utiliser seulement leurs recettes.
Les nouvelles recettes ne peuvent pas reconstituer rétroactivement la géométrie
absente d'une suppression déjà enregistrée.

Depuis la racine du dépôt :

```powershell
./scripts/history-tests/Build.ps1 -RevitVersion 2024 -RunName history-validation
./scripts/codex-tests/StartNativeHarness.ps1 -RevitVersion 2024 -RunName history-validation
```

Le lanceur ouvre un processus Revit distinct. Le banc refuse tout processus contenant
un document ouvert et ne travaille que dans une nouvelle maquette non enregistrée.
Il nécessite les gabarits de familles anglais installés. Résultats dans
`tmp/codex-native-validation/2024-history-validation/result.json` ou `error.txt`.
Utiliser un nouveau RunName pour relancer.

Cas : mur droit avec décalage et ligne de justification, mur courbe, sol troué,
famille tournée avec dimension d'instance, famille hébergée sur mur, références
manquantes, recette invalide, recherche de l'original. Les tests vérifient le passage
JSON, la suppression réelle avant reconstruction, les dimensions et volumes,
l'absence d'éléments natifs résiduels et la validité du DirectShape après annulation.
Le banc vérifie aussi une vraie restauration après sauvegarde/réouverture, les
paramètres d'instance, la restauration conjointe hôte/famille, l'absence de doublons
après réouverture, la nouvelle restauration après suppression, l'annulation du
groupe de transactions et un lot mêlant succès, type absent et données invalides.

Validation du 19 septembre 2026 : 25 contrôles natifs Revit 2024 réussis,
rapport `tmp/codex-native-validation/2024-history-network-diagnostics-v4/result.json`.
Aux 15 contrôles initiaux s'ajoutent : les quatre catégories de tronçons rigides,
les deux catégories de flexibles circulaires, les connexions à un voisin existant,
leur persistance après réouverture, la restauration en plusieurs lots, l'accessoire
tourné connecté à deux tuyaux, le refus d'une inclinaison interdite sans boîte
modale et le respect d'un accessoire déplacé depuis l'enregistrement.
Deux contrôles supplémentaires vérifient la famille tournée en miroir (état
`Mirrored`, repère et boîte englobante) et l'exclusion du calorifuge des comptages,
sans perdre les erreurs/diagnostics des autres familles. Le booléen Revit `Mirrored`
est conservé séparément : le déterminant de `GetTransform()` ne suffit pas pour
identifier une famille en miroir.

Le banc crée une famille d'accessoire synthétique qui interdit son inclinaison :
ce refus est un test explicite de rollback, pas une invitation à supprimer une
occurrence dans une fenêtre Revit. Ses transactions de préparation possèdent aussi
un traitement des erreurs pour ne pas ouvrir de boîte bloquante.

### Raccords CML et Revit 2025

`Build.ps1 -RevitVersion 2025` utilise le banc .NET 8 `HistoryNative2025.csproj`.
Si son dossier de sortie contient `CML-source.rvt`, le banc ouvre cette **copie**
isolée (détachée si collaborative), jamais le modèle utilisateur d'origine.
Il ferme sans enregistrer et annule chaque scénario. Sinon il exécute les tests
synthétiques habituels dans une maquette vierge.

Les scénarios CML couvrent dix couples famille/dimensions/angle, avec les nouvelles
recettes, les anciennes sans identifiants/angles de connecteurs et la réparation
d'un raccord déjà restauré. Ils contrôlent les connexions, l'absence de tuyaux
temporaires résiduels et l'idempotence. La suppression du calorifuge par Revit au
moment de supprimer le raccord de test est consignée séparément.

La reconstruction conserve les diamètres et angles des connecteurs même si les
paramètres d'origine étaient en lecture seule. Pour les réductions pilotées par
le solveur, des tronçons temporaires servent au calcul natif, puis sont supprimés.
Le type et tous les connecteurs doivent correspondre exactement à l'historique.
La réparation des occurrences déjà restaurées est annoncée dans la confirmation ;
elle protège les originaux, les déplacements, les nouveaux voisins et les
dépendances que le remplacement risquerait de supprimer.

Validation : `2025-history-cml-legacy-final/result.json` — 30 scénarios réussis,
60 connexions rétablies, 10 réparations d'éléments déjà présents, aucun échec.
Les variantes anciennes omettent également les paramètres numériques de famille
afin de tester la récupération à partir des seules extrémités historiques.
Régression synthétique : `2024-history-connectors-regression-v1/result.json` —
25 contrôles réussis. Compilations complètes Debug Revit 2024 et 2025 réussies.

### Derniers défauts visibles : piquages et réductions de gaines

Un fichier `CML-targets.json` dans le dossier de sortie peut sélectionner les noms
de familles à tester. Pour le cas ciblé, le banc exige la présence des six IDs du
rapport : 903986, 903766, 877948, 770610, 770608 et 744493. Les anciennes recettes
omettent les dimensions en lecture seule, mais conservent les paramètres qui
étaient modifiables (notamment la longueur personnalisée). Chaque restauration
vérifie aussi les extrémités des tuyaux/gaines voisins, pas seulement leurs IDs.

Les piquages utilisent `NewTakeoffFitting` sur la canalisation historique existante
ou déjà restaurée. Un hôte absent est signalé ; aucun voisin n'est choisi par
proximité. Les préférences de routage de calcul sont isolées dans des copies de
types temporaires, supprimées avant validation ; le type de la canalisation
porteuse est rétabli. Les réductions sont créées avec leur famille exacte ; les
deux ordres d'extrémités peuvent être essayés pour respecter leurs paramètres de
décalage. Le recalage d'un raccord déconnecté est autorisé seulement si une même
translation fait coïncider tous ses connecteurs historiques, dimensions et
directions comprises. Les tolérances de reconnexion ne sont pas élargies.

Validation du correctif :

- `2025-history-visible-v5/result.json` : 24 scénarios réussis (les six occurrences
  exactes et deux piquages supplémentaires, chacun en capture récente, ancienne
  et réparation d'une occurrence déjà recréée).
- `2025-history-routing-regression/result.json` : 30 scénarios acier réussis.
- Compilations complètes Debug Revit 2024 et Revit 2025 réussies.

Le calorifuge reste exclu. Les systèmes logiques ont ensuite été pris en charge
par les recettes dédiées aux systèmes de tuyauterie, ventilation et circuits électriques.

### Circuits électriques

La recette `electrical_system` enregistre les identités des équipements, le tableau,
le type de circuit, les paramètres modifiables, le type de connexion au tableau,
l'emplacement et le numéro du circuit, ainsi que son chemin personnalisé.
Les équipements et le tableau sont restaurés avant le circuit. Un circuit existant
est réutilisé uniquement si son type et tous ses membres correspondent exactement.
Chaque équipement conserve également ses circuits dans sa recette : sa restauration
peut rétablir ses appartenances même quand le circuit a survécu à la suppression.
Dans ce cas, un circuit identifié par son identité historique peut recevoir les
membres restaurés manquants, sans retirer ses membres ni fusionner d'autres circuits.
Un membre ou tableau absent, un autre circuit utilisant les équipements, un
emplacement occupé ou une numérotation incompatible provoque un échec explicite et
l'annulation de la transaction du circuit. Les circuits voisins ne sont pas déplacés.
Les anciennes suppressions dépourvues de cette recette ne peuvent pas retrouver
ces relations rétroactivement.

Le système de distribution du tableau est appliqué avant les paramètres qui en
dépendent. Les totaux électriques calculés en lecture seule ne sont pas rejoués
comme des dimensions de famille : Revit les recalcule à partir des circuits.
Les dimensions géométriques en lecture seule des raccords restent capturées.
Pour les paramètres non partagés de famille, le nom conservé dans la recette
permet une résolution dans la famille si la définition de projet n'est pas accessible.

Le fichier `electrical-only.txt` dans le répertoire de sortie sélectionne les tests
électriques : circuits de données et de puissance, restauration du circuit seul et
des équipements avec le tableau, paramètres, emplacement, chemin personnalisé,
absence de doublons, équipements manquants, tableau manquant et circuits concurrents.

Validation Revit 2025 : `2025-history-electrical-final-v2/result.json`, 64 contrôles
réussis, dont 14 scénarios électriques (7 données et 7 puissance). Le test de
puissance contrôle aussi la tension, les pôles, le numéro et le déplacement vers
l'emplacement historique, ainsi que le chemin personnalisé. Une restauration
répétée n'ajoute aucun objet et ne signale aucune réparation superflue.
Les DLL complètes compilent pour Revit 2023, 2024 et 2025 ; le banc compile aussi
contre l'API Revit 2024. L'exécution native de cette validation a lieu dans Revit 2025.

## Jonctions, découpes et attaches

Les nouvelles recettes conservent les relations géométriques par identité historique :
jonctions et ordre de l'élément qui coupe, découpes par vide, découpes entre solides,
autorisation et type de nettoyage des extrémités de murs, attaches hautes/basses des
murs et poteaux. Pour les poteaux, le style de découpe, la justification et le
décalage sont conservés. Les supports enregistrent aussi les attaches entrantes :
restaurer uniquement un sol peut réattacher un mur ou poteau resté présent.
Les poutres et contreventements admissibles sont également reconnus comme supports.

Le rétablissement intervient après les éléments physiques. Il résout les identités
originales et celles des restaurations précédentes, sans écraser une attache concurrente.
Une référence manquante ou une relation refusée apparaît séparément dans le bilan
et le rapport JSON. Les relations déjà correctes évitent une transaction vide.
Les découpes automatiques des attaches de poteaux sont gérées par l'attache elle-même.
Les familles adaptatives sans points de placement utilisent l'archive native pour
conserver leur transformation, nécessaire au rétablissement de leurs découpes.

Les attaches de murs utilisent une API disponible depuis Revit 2025.2, résolue
dynamiquement pour préserver la compatibilité avec Revit 2023/2024. Une attache de
mur dont la cible ne peut pas être capturée sur une version antérieure est signalée.
Le calorifuge reste exclu. Les relations ajoutent des identifiants et réglages au
JSON existant, sans une seconde copie de la géométrie pour chaque relation.

Le marqueur `relations-only.txt` sélectionne les scénarios dédiés.
Validation Revit 2025 : `2025-history-relations-v9/result.json`, 11 scénarios réussis :
jonction avec un mur survivant et ordre/volume conservés, extrémité désactivée,
absence de doublons, attaches de murs et poteaux avec restauration du support seul
ou du propriétaire seul, références restaurées auparavant, référence absente,
découpes par vide et entre solides avec contrôle du volume et de l'ordre.

Régression complète : `2025-history-relations-full-v2/result.json`, 75 contrôles
réussis, dont les 11 scénarios de relations et les 14 scénarios électriques.
Les versions complètes Revit 2023, 2024 et 2025 compilent après ces ajouts ;
le banc de scénarios compile également contre l'API Revit 2024.

## Charge de l'historique au repos

L'invalidation native ne réarchive que les racines déjà enregistrées ou en attente.
Une modification d'un mur ou tuyau restaurable directement ne crée plus une nouvelle
archive native après l'initialisation du premier fallback. Le scénario de régression
contrôle le nombre réel de fichiers RVT produits, ainsi que l'absence de recettes
natives pour ces objets.

L'index des attaches conserve les propriétaires et un accès direct par support.
Les changements mettent à jour les propriétaires concernés et les attaches d'un
support supprimé, plutôt que de reparcourir tous les murs/poteaux à chaque transaction.
Les relations déjà capturées sont réutilisées dans le suivi des éléments voisins.

Les jonctions automatiques entre murs sont enregistrées séparément des jointures
`JoinGeometryUtils` et des réglages `wall_end`. Chaque extrémité conserve les murs
qui participaient réellement à sa jonction, leur ordre observé et, si elle existe,
l'extrémité réciproque. Après recréation des murs, la restauration vérifie la
présence de chaque voisin avec `LocationCurve.ElementsAtJoin`. Si le voisin manque,
elle relance uniquement les extrémités concernées et vérifie à nouveau. Le rapport
contient une entrée `wall_auto_join` par voisin avec l'ID résolu, le résultat et
l'ordre effectivement observé. L'ordre est audité ; il n'est pas forcé, car l'API
Revit peut rejeter son changement même quand la jonction physique est correcte.

La préparation initiale et l'archivage au repos attendent au moins 1,5 seconde sans
entrée clavier/souris. Les petits lots de préparation sont espacés d'au moins 300 ms ;
les vagues d'archives d'au moins deux secondes. La pause augmente jusqu'à neuf fois
la durée du dernier lot pour laisser la priorité au travail dans la maquette. Il s'agit d'une
régulation entre opérations : la création/copie/sauvegarde d'un document Revit
reste indivisible et doit s'exécuter dans son contexte API principal.

### Audit d'une suppression massive

Chaque transaction de suppression écrit un fichier `suppression-*.json` sous
`%LOCALAPPDATA%/BIMaestro/HistoryReports`. Il contient **tous** les ID supprimés,
y compris ceux qui n'avaient aucun instantané et n'apparaîtront donc pas dans
l'historique restaurable. `PreloadAtDeletion` donne le total initial, les éléments
visités/capturés/ignorés/échoués, le temps actif et l'estimation restante au débit
observé. Cette estimation inclut les pauses déjà observées ; elle ne garantit pas
le temps futur. `PreloadStillQueuedAtDeletion` dit si un ID était encore dans la
file de préchargement. Une valeur `null` signifie que la file n'avait pas encore été
établie ou que l'état était indisponible.

Chaque ligne conserve l'origine et l'heure de son instantané, son type de recette,
l'état exact de son archive native avant suppression, sa racine native, ses liens
géométriques et, si connue, la courbe d'esquisse propriétaire. Le dernier événement
de sélection comporte le nombre sélectionné, les cache hits, les captures et les
temps de chaque service de sélection du plugin. Une archive `Ready=false` à la
suppression explique directement un échec `archive`, même si la préparation globale
du modèle était terminée.

Le rapport `restauration-*.json` version 2 référence ces fichiers, détaille les
échecs par catégorie/motif/recette, relie chaque résultat à son parent connu et
liste les jointures/attaches avec les identifiants réellement résolus. Un parent
marqué `created` n'est **pas** une preuve qu'un enfant signalé en échec a été recréé :
seul `IncludedInParent`, `Existing` ou `Created` confirme ce résultat. Les anciens
événements sans audit restent lisibles et sont comptés dans `ItemsWithoutDeletionAudit`.

Pour comparer deux essais, conserver ensemble le fichier de suppression, le rapport
de restauration et la maquette de test. Les rapports sont écrits hors du RVT ; le
nouveau code doit être chargé au démarrage de Revit avant la suppression testée.
Le chemin d'archivage explicite utilisé par les tests reste disponible sans attente.

### Priorité de préparation pour un second essai

Au chargement de la maquette, les murs, sols, toits, portes, fenêtres, fondations,
poteaux/ossatures, équipements mécaniques, gaines et tuyaux passent avant le reste
du catalogue. Le filtre est appliqué par Revit sur les ID, sans ouvrir chaque élément
juste pour le classement. La sélection continue de capturer immédiatement ses
recettes ; ses racines natives (y compris les murs hôtes des portes) sont classées
dès la sélection à partir des instantanés en cache, sans opération de copie. Elles
passent ensuite en tête de la prochaine vague d'archivage au repos. L'archivage s'exécute avant le lot de
préchargement du même événement d'inactivité. Les budgets et délais de repos ne
changent pas, et aucune catégorie de modèle n'est supprimée de la couverture.

Le journal de suppression expose `PriorityCandidates`, `PriorityVisited`,
`PriorityCatalogMilliseconds` et `Categories` dans `PreloadAtDeletion`. `Categories`
décompte les éléments **déjà visités** par catégorie ; ce n'est un inventaire complet
des candidats qu'après la fin du préchargement. `NativePriorityPendingRootsAtDeletion`
et `NativePriorityClassificationMilliseconds` mesurent la file native prioritaire.
Chaque archive indique désormais `NativeArchiveStartedUtc` : une valeur nulle avec
`NativeArchiveReadyAtDeletion=false` signifie qu'elle attendait encore son tour,
alors qu'une date permet de distinguer une copie commencée mais non terminée.
La priorité enregistrée ne contourne pas la condition d'inactivité ni la pause entre
deux vagues ; un nombre positif de racines prioritaires en attente ne signifie donc
pas qu'elles étaient déjà prêtes à la suppression.
`NativeBackgroundAtDeletion` indique le dernier contrôle de la file, le motif
`cooldown` ou `user_active` s'il a bloqué le travail, la prochaine heure admissible
et les heures de début/fin de la dernière vague. Un dernier contrôle antérieur à
la sélection signale qu'aucun passage au repos n'a relancé la file entre-temps.
L'estimation du temps restant est laissée à `null` tant que la file prioritaire
n'est pas terminée et qu'au moins 10 % du catalogue (avec un minimum adapté aux
petits modèles) n'a pas été visité : les premiers murs et familles sont trop coûteux
pour extrapoler leur cadence à tout le modèle.

Validation intermédiaire : `2025-history-idle-fix-full/result.json`, 76 contrôles
réussis, dont le scénario empêchant l'archivage inutile des murs et tuyaux.
Validation finale : `2025-history-idle-fix-final/result.json`, 77 contrôles réussis,
dont la suppression/réapplication d'une attache avec mise à jour incrémentale du cache.
Les DLL complètes Revit 2023, 2024 et 2025 et le banc Revit 2024 compilent.
