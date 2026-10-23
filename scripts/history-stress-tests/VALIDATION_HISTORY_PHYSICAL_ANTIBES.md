# Antibes — validation physique avec préparation

**Protocole terminé : suppression, restauration, sauvegarde, fermeture, réouverture dans un nouveau document, vérification et fermeture finale sont prouvées. La fidélité de restauration échoue : aucun PASS global.**

Run : `tmp/history-stress/antibes-physical-prepared-v2`. Capture **Détaillée**. Produit réellement chargé : SHA256 `A5B1DD546FB4FE6246F4FCB739DCD63C0846E0D52BB2C424B562BC3A90796947`. L’audit du binaire chargé confirme le plafond récent de **10 000 événements** ; le test réel en contient 3 600 et ne valide donc pas un batch dépassant 10 000.

## Limite récente relevée à 10 000

Chargement et affichage récents partagent désormais une constante de **10 000**.
Une saturation est signalée et **« Compléter la période »** utilise le chargeur
sans plafond sur les dates concernées. Au-delà de 2 000 lignes, les cartes utilisent
un panneau vertical virtualisé avec recyclage ; les petits historiques gardent
la mosaïque. La grille utilise aussi la virtualisation.

Le test isolé `scripts/history-tests/TestHistoryEventLimits.ps1` passe : 2 501
événements conservés, les 10 000 plus récents de 12 075 correctement sélectionnés,
puis les 12 075 identités récupérées sur la période, avec tri, dates, maquette et
dédoublonnage contrôlés. Les appels WPF et les bascules de panneaux sont testés hors
Revit ; cela ne mesure pas la performance de 12 075 cartes dans une fenêtre réelle.

Le produit 2025 a été compilé et installé dans un dossier versionné, avec sauvegarde
du manifeste. La DLL du run A5B1 a été comparée aux 2 236 méthodes historiques de
la version History10k compilée 5F8069 : aucun écart métier observé après normalisation
des temporaires compilateur. Le manifeste a ensuite évolué avec les travaux
parallèles ; `current-install-limit-check.json` confirme encore les trois limites
10 000 dans la DLL installée C5B3EC5F5D47. Aucun manifeste concurrent n'est écrasé.

## Résultat sur les objets ciblés

| Périmètre | Supprimés attendus | Correspondant d’origine unique retrouvé | Vérification stricte passée | Présents avec contrôle échoué | Sans correspondant identifié |
|---|---:|---:|---:|---:|---:|
| Hors isolants exclus par politique | **1 501** | **1 319** | **743** | **576** | **182** |
| Isolants exclus par politique | 524 | 0 | 0 | 0 | 524 |
| Total déclaré | **2 025** | **1 319** | **743** | **576** | **706** |

Les 576 échecs présents ne sont pas 576 réseaux matériellement incorrects :

- **496** ont uniquement des références vers des isolants volontairement non restaurés ; aucun autre contrôle ne leur est défavorable.
- **80** ont d’autres défauts ou écarts de fidélité. Ce nombre conserve notamment **26 raccords** dont le graphe diffère seulement par les isolants, mais dont la géométrie ou la bbox diffère aussi.

Décomposition complète hors isolants : **743 stricts + 496 uniquement politique + 80 autres défauts présents + 182 non identifiés = 1 501**. Ces sous-groupes servent au diagnostic ; les booléens bruts et le verdict global restent inchangés.

## Ce que révèlent les contrôles de fidélité

Les contrôles peuvent se recouper sur un même objet.

| Contrôle | Résultat sur les objets présents |
|---|---|
| Identité de famille/type d’élément, classe, dimensions, hôte/niveau/groupe | Aucun écart signalé |
| Axes comparables parmi les objets en échec | 336 ; extrémités identiques, milieu au plus `3,18e-14 ft` |
| Position de l’instance | **35** déplacements dépassant 1 cm |
| Géométrie/transformée | **40** échecs : les 35 translations et 5 changements d’orientation sans translation ; directions des bases différentes sur 30 objets, jusqu’à 180° |
| Bbox mondiale | **51** écarts : **50 dépassent 1 cm**, un seul vaut **0,116 mm** |
| Jonctions géométriques | **2** échecs |
| Connexions brutes | **551** échecs |
| Graphe après retrait diagnostique des seuls voisins isolants | **522** ne diffèrent plus ; **29** conservent un écart physique ou sémantique de voisin |
| Cardinalité des ports | Aucun changement observé |
| Type de système MEP | **3** cas ne satisfont pas la preuve de normalisation autorisée |

Les directions de base sont des vecteurs sans unité ; leurs différences ne sont pas présentées comme des déplacements en millimètres. La vérification compare points, axes, transformées, bbox, dimensions et relations enregistrées ; elle ne constitue pas une comparaison exhaustive des solides.

Exemples mesurés :

- Réduction acier concentrique **956337** : origine déplacée de **300 mm**, direction d’axe inversée de **180°**, bbox décalée de **30,5 mm**. L’identité du type reste correcte. Des cas semblables existent pour 959043 et 957696.
- Canalisation **947448** : le voisin conserve sa position, mais sa classification passe de **ReturnHydronic à SupplyHydronic**, avec type de système `…0004c94c → …0004c94b`. Il s’agit d’un écart de sémantique du voisin, pas d’un nombre de ports différent.
- Sol **557310** : seul écart bbox inférieur à 1 mm, **0,116 mm**. Ce cas doit être distingué des 50 écarts centimétriques ; il peut relever du seuil très strict `1e-5 ft`.

Les 29 graphes encore différents hors isolants concernent 6 gaines, 5 accessoires de canalisation, 5 appareils sanitaires, 3 équipements CVC, 2 raccords de canalisation, 4 raccords de gaine, 2 canalisations souples et 2 canalisations. Le diagnostic vérifie identité des voisins, coordonnées, domaine, type, forme et classification. Les références vers les isolants ne prouvent pas à elles seules une connexion physique plutôt que logique.

Les trois cas de système sont les gaines **604614, 744177 et 744330** : source sans type défini, type restauré `…0004c9cd`. La preuve exigée pour accepter la normalisation vers un type de voisin n’est pas satisfaite ; le rapport conserve donc l’échec, sans assimiler un système initialement indéfini à une famille/type d’élément incorrect.

Les pertes de cardinalité des ports de piquage observées sur le run early v3 ne se reproduisent pas sous cette forme sur ce run préparé. Cela ne prouve pas une correction produit : périmètre, préparation et recettes disponibles diffèrent entre les deux essais.

## Les 182 cibles sans correspondant d’origine

**176 courbes** — 108 ModelLine et 68 ModelArc — appartiennent au même **Group 440709, « Groupe1 »**. Ce groupe est une instance réelle, non un GroupType. Sa catégorie ne satisfait pas le filtre de sélection physique du banc : il n’est donc pas une racine demandée, alors que ses membres le sont. Revit annonce bien sa suppression en dépendant dans DocumentChanged ; son événement est journalisé. Sa requête a un UID vide et échoue au contrôle d’archive ; les membres échouent avec « L’identité d’un membre du groupe est ambiguë ».

Treize paires de courbes ont déjà dans la source des géométries et bbox strictement identiques. Ce défaut de correspondance des membres ne prouve pas que les courbes sont intrinsèquement impossibles à restaurer. L’omission de la racine groupe limite aussi la comparaison avec une véritable sélection globale dans l’interface Revit. La gestion native du groupe et de ses identités reste un défaut produit observable ; l’événement du groupe n’a pas disparu du journal.

Les **six barreaux 557051–557056** n’ont pas de correspondant d’origine. Six barreaux supplémentaires de mêmes noms sont présents, mais le brut ne contient pas leur géométrie détaillée. Il serait donc incorrect d’affirmer que les six objets sont physiquement absents : l’échec d’identification est établi, leur correspondance géométrique ne l’est pas.

L’[analyse ciblée des causes](RESTORE_FINDINGS.md) distingue ces défauts confirmés des hypothèses de reconstruction.

## Journal, périmètre et préparation

Le banc sélectionne **2 047 objets**, chacun avec au moins une mesure géométrique enregistrée. Aucun interne connu n’est sélectionné et toutes les exclusions ont une raison explicite. Il demande 1 988 racines ; 2 025 cibles sont supprimées et 22 survivent : deux « Travailleur chantier » et vingt indicateurs de sens de fluide.

Document.Delete retourne 3 570 IDs ; DocumentChanged en annonce **3 600**. Le journal brut du batch `b57c45daf0564fa7a755ac72f6380e86` couvre exactement ces **3 600 IDs**, sans manquant ou doublon. Le chargement complet, le chargement récent et les requêtes réelles couvrent chacun 3 600 événements. **Aucune troncature observée**, mais aucun test réel au-delà de 10 000.

Le banc utilise le vrai chargeur par période et la vraie conversion UI `ToRestoreRequest`, puis le moteur. La sélection, le rendu et les clusters de la fenêtre réelle ne sont pas exercés.

La préparation atteint son timeout de **300 s**, malgré un préchargement de base terminé : 6 113 candidats visités, 4 197 snapshots, dix éléments encore en attente, LightPending=0, NativePending=0. Sur les cibles sélectionnées, 555 snapshots sont absents : 524 isolants et 31 autres objets, principalement dépendants de garde-corps/escalier ou limites. Les événements des cibles effectivement supprimées possèdent tous une recette.

`DetailCaptureAttempted=false` concerne 1 050 snapshots sélectionnés et ne donne pas les membres exacts de Pending. Huit d’entre eux ont une recette native : quatre sous-divisions topographiques et quatre DirectShape. Leurs événements persistés contiennent tous un reçu **WholeModel Ready=true**. Les deux seules recettes nulles du journal sont les **FamilySymbol 556714 et 983012**, protégés `definition:type`, hors sélection physique. Ces définitions ne permettent pas d'identifier les deux autres membres de Pending : la file n'est pas exportée et exclut les ElementType. Un reçu Ready ne prouve pas la fidélité de la restauration. La copie d'ouverture était prête en environ **271 ms**.

## Temps mesurés et limites de comparaison

| Phase | Durée |
|---|---:|
| Inventaire du banc | 695 ms |
| Suppression unique | **2 138,111 ms** |
| Sondage jusqu’au journal complet | **2 431,443 ms** |
| Restauration | **359 127,246 ms — 5 min 59,127 s** |

Résultat moteur : **1 138 créations, 347 inclus dans un parent, 0 existant, 2 115 échecs de requête**. Les requêtes comprennent aussi les dépendants et définitions supprimés par Revit. Un échec de requête ne signifie donc pas automatiquement une cible physique manquante.

Pendant l’appel Restore : **2 322 callbacks DocumentChanged**, dont 181 sans IDs ; 4 931 IDs ajoutés ; 180 rafraîchissements. Le mode Détaillé recapture les transactions de restauration. Les mesures `element_capture_full` couvrent **10 034 appels et 45,225 s**, maximum **83,840 ms**. Ces temps ne couvrent pas tous les callbacks, copies natives, transactions ou accès disque. Les compteurs d’IDs ajoutés ne mesurent pas les restaurations réussies ; les phases synchrones imbriquées ne s’additionnent pas comme une décomposition complète du temps total.

Les cinq minutes de préparation font partie de cet essai. Le run early v3 avait un périmètre et une couverture initiale différents. **Aucun facteur d’accélération fiable, aucune comparaison Simple/Détaillé et aucune mesure de fluidité interactive ne sont déduits de ces deux runs.** Les écarts Idling/Dispatcher ne sont pas des temps d’image.

Le [précédent Antibes-stress](VALIDATION_HISTORY_MASS_EARLY_V3.md) a bien terminé
sa restauration en **21 min 55 s**, avec des défauts et sans persistance validée.
Simple est conservé : il réduit les compléments coûteux. Détaillé ajoute des
données utiles, sans être plus rapide ni garantir une récupération totale. Voir
l'[audit des modes](MODES.md). Pendant cette reprise, seules la limite et son
affichage ont été modifiés dans le produit ; les défauts de restauration restent
à corriger, notamment les identités de groupes et les placements natifs.

## Persistance et intégrité : vérifiées

La copie restaurée **Antibes-restaure.rvt**, 142 258 176 octets, est enregistrée, fermée puis réouverte dans un document distinct : runtime `405691406 → -646451633`. Les preuves Closed, OldDocumentInvalid, AbsentFromApplicationDocuments, ReopenedDistinctDocument et FileExists sont vraies.

Après réouverture, les **2 025 correspondances/absences et tous les indicateurs de contrôle sont inchangés**, ainsi que l’ensemble des 999 UID supplémentaires. Les mêmes 706 absences d’identité et 576 échecs de fidélité restent présents. Leur persistance confirme la sauvegarde de l’état restauré ; elle ne transforme pas les échecs en succès.

Aucun doublon d’origine ni mapping inverse non injectif n’est détecté parmi les correspondants vérifiés. Les **999 objets supplémentaires** comprennent 619 axes, 337 membres d’esquisse, 30 systèmes MEP, 7 ConduitRun et 6 barreaux. La liste est complète, sans plafond du banc ; ces objets demandent une analyse de leurs propriétaires et ne constituent pas automatiquement 999 copies physiques injustifiées. Le contrôle « aucun objet supplémentaire » échoue.

La fermeture finale est prouvée à **15:04:21,549 UTC**. La source de copie exacte est `tmp/motor-extraction-builds/antibes-inspection/Projet_AntibesV2-test.rvt` ; le document d’essai est `tmp/history-stress/antibes-physical-prepared-v2/Antibes-stress.rvt`. Les deux fichiers originaux sur disque conservent le SHA256 :

`EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA`

La copie restaurée séparée a le SHA256 :

`831722B831A1333CD340F25F4E2084DB9084DADB816630F622B94B874F2917A2`

La copie d’ouverture utilisée par les reçus est `history/native-history/snapshot-opening-4931c1423e464c4caabc574ace58666e.rvt` dans le run. Aucune sauvegarde du modèle restauré n’a remplacé les RVT d’origine.

Preuves principales : `deletion.json`, `inventory.json`, JSONL brut, `load.json`, `restore-result.json`, `verification.json`, `persistence.json`, `reopened-verification.json`, `finished.json`, `integrity.json`. Analyses indépendantes : **`independent-summary.json`**, **`independent-fidelity.json`**, **`independent-fidelity-reopened.json`**, **`independent-reopen-comparison.json`**, **`independent-timings.json`**.
