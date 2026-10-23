# Antibes — correctifs de fidélité et validations arrêtées

État définitif de cette campagne, le 22 octobre 2026 : **régressions Revit terminées avec 8 cas entièrement passés et 2 échecs de classification ; massif r3 bloqué avant création ; massifs r4 et r6 arrêtés avant résultat final**. Aucun PASS de restauration massive, de fidélité finale ou de persistance des correctifs n’est établi. Le déploiement expérimental a été retiré.

Le produit Revit 2025 r3 avait le SHA256 `97D005017FAF863FEBF124B91FD23D3C79F670ED87A383072674C7439F0B368D`, confirmé chargé par le `ready.json` des régressions `tmp/history-stress/antibes-fixes-regressions-v3`, PID initial `58776`. Le premier massif correctif `tmp/history-stress/antibes-physical-fixed-early-v1`, PID initial `57784`, utilisait ce même produit ; il a échoué avant toute création puis a été fermé sans sauvegarde.

La révision **r4 a été compilée et installée pour l’essai**, SHA256 **AC1FB7A59628B436C8E14617B1901191D491BC51C8F8C1E066D606D40CB60D28**. Elle retire la préparation des identités dans le document source. Les douze hashes de sources historiques avant/après compilation sont identiques, d’après `tmp/history-stress/product-antibes-fixes-r4/history-source-before-build.json` et `history-source-after-build.json`. Le run **`tmp/history-stress/antibes-physical-fixed-early-v2`**, PID initial **44936**, confirme ce produit chargé dans `ready.json` ; son arrêt est détaillé ci-dessous.

Le dernier essai **r6**, `tmp/history-stress/antibes-physical-fixed-early-v3`, PID initial **24600**, chargeait le SHA256 **DE2B33C69E192B786C54001C3829D0442181250404136C4FD3B3D1AE53A3BE8E**. L’hydratation des recettes directes depuis le checkpoint et le refus de copie native des systèmes MEP logiques n’ont pas suffi à supprimer les transactions vides d’environ une minute. L’objet responsable de chaque pause n’est pas identifié par l’observateur.

## Référence avant correctifs

Le [rapport physique préparé v2](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-stress-tests/VALIDATION_HISTORY_PHYSICAL_ANTIBES.md>) reste inchangé. Il décrit l’ancien produit, une suppression effective de **2 025 cibles**, dont **524 isolants exclus par politique**, et la sauvegarde/réouverture d’un résultat qui ne satisfait pas la fidélité attendue.

Pour les **1 501 cibles hors isolants**, le bilan était : **743 contrôles stricts passés + 496 écarts dus uniquement aux références vers les isolants exclus + 80 autres objets présents avec écart + 182 objets sans identité correspondante = 1 501**. Les 182 se décomposent en **176 courbes du groupe 440709 — 108 ModelLine et 68 ModelArc —** et **six barreaux 557051–557056**. Cette décomposition ne remplace aucun booléen brut par un PASS.

Les 80 écarts présents regroupent des contrôles qui se recoupent : **40 écarts de géométrie/transformée**, dont 35 translations de plus de 1 cm ; **51 écarts bbox**, dont 50 de plus de 1 cm et un de 0,116 mm ; **29 graphes encore différents après exclusion diagnostique des seuls isolants**, trois types de système non conservés et deux jonctions géométriques différentes. Les **26 raccords** qui ont aussi un défaut de placement restent dans les 80, même si leur seul écart de graphe concerne les isolants.

Exemples de référence : réduction **956337**, origine déplacée de 300 mm et repère inversé de 180° ; canalisation **947448**, voisin classé ReturnHydronic devenu SupplyHydronic ; plante **520877**, altitude diminuée de 457,966 mm ; sol **557310**, bbox différente de 0,116 mm. Les 336 axes comparés conservaient leurs extrémités et leur milieu à l’arrondi numérique près. Il ne s’agissait pas d’un écart général d’identité de type ou de nombre de ports.

Les bruts de référence sont conservés dans `tmp/history-stress/antibes-physical-prepared-v2/`, notamment `inventory.json`, `restore-result.json`, `verification.json`, `reopened-verification.json`, `independent-fidelity.json` et `independent-native-root-findings.json`. L’[analyse des causes](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-stress-tests/RESTORE_FINDINGS.md>) conserve aussi les observations de l’ancien essai early v3 ; les périmètres et les recettes disponibles diffèrent entre ces essais.

## Causes traitées dans les sources

| Sujet | Défaut ou risque établi | Correctif intégré ; preuve native encore attendue |
|---|---|---|
| **176 ModelLine/ModelArc du groupe 440709** | Treize paires ont des courbes et bbox identiques dans la source. Une correspondance par classe/catégorie/bbox est donc insuffisante. La racine Group avait aussi été refusée à l’hydratation malgré son événement journalisé. | Le filtre d’ouverture accepte la vraie instance de groupe de modèle. **Méthode finale r4 : source consultée en lecture seule.** Si ses membres sont ambigus et n’ont pas déjà une provenance exploitable, chaque membre est copié séparément ; l’identité est écrite uniquement sur sa copie dans le document destination, puis les copies sont regroupées. La provenance déjà présente peut être lue ; aucune préparation ou écriture d’identité n’est faite dans l’archive source. Les candidats consommés sont retirés et le dernier fallback qui attribuait un candidat unique sans preuve a été supprimé. Le chemin sans provenance initiale a passé la régression native à deux courbes ; les 176 membres réels restent à retester en massif. |
| **Six barreaux 557051–557056** | Ils sont générés par les garde-corps ; HostId/SuperComponent/Level ne suffisent pas à retrouver leur propriétaire. Les six copies supplémentaires de même nom dans v2 ne prouvaient pas leur équivalence géométrique. | Le propriétaire est recherché parmi les dépendants de Railing. La racine suit ensuite le garde-corps et son escalier hôte. Les garde-corps sont appariés avant les barreaux, puis la recherche est limitée au garde-corps correspondant. Un propriétaire ambigu est refusé. Le cache est invalidé lors des changements et avant l’appariement du document cible. |
| **Altitudes, points et orientations** | Une copie peut exister et porter le bon type tout en ayant une altitude ou un repère incorrect. La comparaison des seuls connecteurs permettait aussi un raccord symétrique inversé. | Capture du point, du repère complet, des inversions et des paramètres d’altitude. Recalage limité aux familles recréées, avec refus de déplacer un composant déjà connecté ou généré indépendamment de son ensemble. Vérification après création/commit et après les relations. Les arcs conservent aussi leur définition exacte ; l’ancien format à trois points reste lisible. |
| **Réseaux et raccords** | Le redimensionnement par tronçons temporaires choisissait le premier type de système du projet. ConnectTo pouvait également modifier un propriétaire, un voisin ou une connexion existante sans contrôle complet. | Choix par type de système historique ou voisin identifié et classification compatible, avec refus de l’ambiguïté. Vérification du repère du raccord et des ports avant acceptation. Le garde capture les deux propriétaires et leurs voisins directs : points, bases, inversions, axes/milieux, flexibles, ports, classes, systèmes et connexions physiques déjà présentes. Il contrôle après régénération et après commit ; une tentative incorrecte peut être annulée. Les nouvelles connexions demandées sont autorisées, les isolants restent exclus. |
| **Trois gaines initialement sans type de système** | La copie native de 604614/744177/744330 attribue CML_Fumées alors que le type source est indéfini. Une normalisation vers un voisin n’était pas démontrée. | Rétablissement explicite d’InvalidElementId pour les seules sources attestées indéfinies, après reconnexion. Le type relu doit réellement être absent ; classes, placement, voisins et connexions doivent rester conformes, y compris après commit. Un refus est rapporté ; il n’est pas remplacé par une tolérance de classification. |
| **Jonctions d’extrémité de murs** | L’ancien essai early v3 montrait une redistribution de 175 mm autour du mur 556631, compatible avec un ordre de nettoyage différent. JoinGeometryUtils ne décrit pas cet ordre. | Capture de l’autorisation, du type et des participants ordonnés aux deux extrémités. La restitution impose une permutation exacte des participants existants, avec contrôle réciproque et vérification avant assimilation. Elle ne crée pas un voisin absent par simple permutation. L’effet géométrique réel reste à mesurer dans Revit. |
| **Excavations topographiques** | Quatre excavations existent dans la source et n’étaient pas contrôlées séparément par v2. | Capture de la paire terrain/élément coupeur et du volume, restitution par l’API d’excavation, puis vérification. Le banc compare les identités résolues et les volumes avant/après restauration et réouverture. |

Sources concernées : [OpeningArchive](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryOpeningArchive.cs>), [NativeArchive](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs>), [Reconstruction](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs>), [placement étendu](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.Extended.cs>), [Network](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs>), [garde réseau](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.Guard.cs>), [Relations](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryRelations.cs>) et [Restoration](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs>).

## Diagnostic réel v4 : ce qui est prouvé

Run : `tmp/history-stress/antibes-balusters-diagnostic-v4/`. Les JSON indiquent le produit réellement chargé **8A029591BA27208B66A7A0E8CCE090BC2E473B913A6F2956B1066019DF3ED10A**. Ce binaire précède les correctifs r3 ; ses résultats diagnostiquent la source et l’API, ils ne valident pas r3.

`baluster-diagnostic.json` décrit un diagnostic sans transaction, suppression, capture de recette, restauration ou sauvegarde. **ReadOnly=true, ModifiedBefore=false, ModifiedAfter=false, aucun ID cible manquant et aucune QueryError.** Les dépendances établissent les propriétaires garde-corps 556842/556846 et l’escalier 556837 ; les six barreaux ont tous le même point apparent mais trois hauteurs bbox répétées. Le nom et le point seuls ne constituent donc pas une identité.

Le terrain **433508** porte les quatre intersections source de type Excavate suivantes. Les volumes sont en pieds cubes et servent de référence brute, sans conversion arrondie pour les contrôles.

| Terrain | Élément coupeur | Volume source, ft³ |
|---:|---:|---:|
| 433508 | 556829 | 1 198,7260561585426 |
| 433508 | **557310 — sol** | **15 000,266887187958** |
| 433508 | 790432 | 5 130,487306177616 |
| 433508 | 835624 | 39 781,99431806803 |

L’existence de l’excavation par 557310 est confirmée. Elle ne prouve pas, à elle seule, la cause de son ancien écart bbox de 0,116 mm. Le nouveau contrôle compare aussi le volume avec une tolérance `max(1e-5 ft³, abs(volume)*1e-8)` et les deux identités résolues ; une erreur d’interrogation reste non vérifiée.

La sonde distincte `unset-probe.json`, avec état source dans `unset-probe-source.json`, travaille uniquement sur de nouvelles copies natives des gaines **604614, 744177 et 744330**, dans des groupes de transactions ensuite annulés. Pour les **3/3** cas :

- le type source est absent et les ports sont ExhaustAir ; la copie reçoit d’abord le type 313805, CML_Fumées ;
- le paramètre RBS_DUCT_SYSTEM_TYPE_PARAM est modifiable, Set(InvalidElementId) retourne true et, après régénération, le type relu est absent ;
- la classification ExhaustAir est conservée, sans exception ; l’annulation est prouvée, la copie temporaire a disparu et la source est inchangée ; Errors est vide.

**Limite de cette sonde :** la relecture est faite avant commit de la transaction et sur une copie indépendante avant reconnexion. Elle ne prouve pas la conservation de l’état indéfini après ConnectTo, commit ou sauvegarde/réouverture. Ces contrôles ont été ajoutés au produit et restent à confirmer par les nouveaux essais.

`integrity.json` et `finished.json` prouvent la fermeture sans sauvegarde, l’invalidation du document et son absence de la collection des documents ouverts. La source et la copie de départ gardent le SHA256 **EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA**. Aucun fichier restauré n’a été sauvegardé par ce diagnostic.

## Tests ciblés exécutés

| Contrôle | État et portée réelle |
|---|---|
| [TestNetworkConnectionGuard.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-tests/TestNetworkConnectionGuard.ps1>) | **PASS, 42 assertions**, confirmé par la sortie outil de session ; aucun log séparé de cette exécution n’a été conservé. Exécute le vrai Guard.cs et des méthodes Network extraites : déplacements, rotations de 180°, voisin direct, axes/flexibles, ports, classes/types, choix historique du système, connexions existantes et refus d’un unset qui débranche. Les contrôles couvrent aussi les métadonnées Native/OpeningFallback et la priorité d’une source attestée définie. L’adaptateur Revit est simulé ; aucun effet du solveur réel n’en est déduit. |
| [TestNetworkApiCompatibility.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-tests/TestNetworkApiCompatibility.ps1>) | **PASS.** Compile les deux vrais fichiers Network contre l’API Revit 2025 installée, cible net8.0-windows/WPF. Les dépendances métier externes au réseau sont des stubs ; aucune exécution Revit. |
| [TestHistoricalPlacement.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-tests/TestHistoricalPlacement.ps1>) | **PASS.** Comparateur de placement de production : translation, bases, inversions, bbox, valeurs invalides et anciennes recettes ; sérialisation des arcs exacts et anciens. Ce n’est pas une preuve de déplacement ou d’hébergement correct dans Revit. |
| [TestWallJoinOrder.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-tests/TestWallJoinOrder.ps1>) | **PASS.** Planification de permutation de production, participants exacts, bijection, anciens index et absence de mutation des entrées ; intégration setter/vérification contrôlée statiquement. La géométrie finale du mur et le rollback API restent à tester dans Revit. |
| [TestHistoryEventLimits.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-tests/TestHistoryEventLimits.ps1>) | **PASS.** 2 501 événements conservés ; sélection exacte des 10 000 plus récents parmi 12 075, puis chargement des 12 075 sur la période. Bascule WPF vers la virtualisation avec recyclage contrôlée hors Revit. |
| [TestSummaryExcavations.ps1](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-stress-tests/TestSummaryExcavations.ps1>) | **PASS.** Fonctions d’analyse de production extraites : anciens runs inchangés ; un nouveau contrôle ExcavationsMatch faux ou partiellement absent empêche le succès. Aucun ancien résultat v2 n’est réécrit. |

Les sorties d’exécution de la session confirment ces résultats. Le plafond récent reste **10 000**, avec chargement complet par période. L’ancien essai natif v2 avait 3 600 événements et ne démontrait donc ni l’affichage réel ni une suppression/restauration réelle de plus de 10 000 objets.

## Régressions Revit v3 : résultats réels

Le fichier `regressions.json` contient **10 cas, 8 Passed=true, 2 Passed=false, aucune erreur d’exécution et Passed global=false**. Le rapport dérivé `regressions-placement-validation.json` sépare la géométrie de la sémantique, sans réécrire ces booléens : IdentityAndPlacementPassed=true, mais FullSourceFidelityPassed=false, ConnectedNetworkReconciliationVerified=false et WholeModelRestorationVerified=false.

| Cas réellement exécuté | Verdict brut | Ce qui est établi |
|---|---|---|
| Groupe de deux courbes coïncidentes, source vivante sans provenance initiale | **PASS** | Les deux membres sont appariés avec des UID cibles distincts ; la géométrie coïncidente ne les confond pas. |
| Groupe de deux courbes coïncidentes, source historique avec identités temporaires | **PASS — ancienne option désormais retirée** | La petite fixture autorisait l’écriture après validation du groupe puis un appariement injectif. Ce résultat ne garantissait pas le commit sur l’archive Antibes réelle : le massif l’a ensuite refusé. Cette préparation source n’est plus utilisée en r4. |
| Six vrais barreaux Antibes générés par les garde-corps de l’escalier | **PASS** | Six barreaux cibles distincts sont retrouvés, avec racine propriétaire conforme et placement contrôlé. |
| Familles à décalage d’altitude : 520659, 808923 et 926315 | **3 PASS** | Le helper refuse une perturbation volontaire puis rétablit le placement attendu ; la vérification indépendante de géométrie et de type est conforme. |
| Piquages : 982660 et 982848 | **2 PASS** | Points, repères, bbox et coordonnées des ports sont conformes ; la perturbation volontaire est rejetée et le recalage contrôlé. La classification DomesticColdWater correspond aussi à la source. |
| Réductions : 956337 et 930776 | **2 FAIL** | Le recalage géométrique passe : points, bases du repère, bbox et positions/identité/domaine/type/forme des ports sont conformes ; une perturbation volontaire est détectée et refusée. La classification source SupplyHydronic devient toutefois UndefinedSystemType sur ces copies déconnectées. Ce défaut de fidélité sémantique conserve les deux échecs bruts. |

Ce sont donc **10 cas conformes pour l’identité et le placement, mais seulement 8 validations complètes selon les critères bruts de ces régressions**. Les deux réductions isolées n’ont pas de voisin connecté. L’export ne contient pas l’état géométrique/sémantique initial complet de la fixture avant perturbation ; il ne permet pas d’attribuer à lui seul le changement dynamique de classification au recalage plutôt qu’à la copie déconnectée. Le test ne prouve pas la fidélité d’une reconnexion de réseau. Le massif conserve l’exigence de classification et de type de système source exacts, y compris un type initialement nul.

Les preuves sont `regressions.json` — SHA256 **44FD94CB7C11E56A2E5C84B5091924B7622223AE02F71BEE1AF7615B67D186B1** — et `regressions-placement-validation.json` dans `tmp/history-stress/antibes-fixes-regressions-v3/`. Les sondes s’exécutent sur des projets temporaires ou dans des groupes de transactions annulés ; elles ne constituent pas une restauration massive sauvegardée.

`finished.json` et `integrity.json` du même run attestent la clôture effective à **15:46:09,514 UTC** : Closed=true, OldDocumentInvalid=true, AbsentFromApplicationDocuments=true et ClosedWithoutSaving=true. SourceUnchanged et CopyAndSourceUnchanged sont vrais ; la source et la copie de départ conservent **EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA**. RestoredSavedPath est nul : aucune persistance de modèle restauré n’est revendiquée pour ces régressions.

## Premier massif correctif : échec avant création, code corrigé à retester

L’essai `tmp/history-stress/antibes-physical-fixed-early-v1/` a bien validé la suppression : **2 025 cibles physiques supprimées**, **3 600 IDs DocumentChanged**, **3 600 événements persistés**, et **3 600 requêtes envoyées**. Le défaut se situe ensuite dans la préparation de restauration, avant la première création ; il ne s’agit pas d’une troncature du journal ou du chargeur.

`last-error.json` contient l’exception de production **« Les identités de la source historique n’ont pas pu être préparées. »**, levée par `Sources.PrepareIdentities` lorsque le commit de cette préparation n’a pas été validé. L’état final du banc indique **RestoreChangedAddedIds=0 et RefreshCount=0**. Aucun résultat de restauration réussi n’a été produit et aucun fichier restauré n’a été sauvegardé. La réussite de la petite fixture de groupe avec identités temporaires n’autorisait donc pas à déclarer cette préparation fiable sur le modèle complet.

La méthode **PrepareIdentities et son appel ont été entièrement retirés du produit r4**. La méthode conservée utilise le chemin déjà testé de copie séparée des membres ambigus, avec SetOrigins uniquement dans la destination. Les six barreaux réels avaient aussi passé leur régression via le propriétaire Railing sans écriture d’identité dans la source. Cette modification élimine le commit source qui a bloqué r3 ; elle ne constitue pas encore une preuve de réussite massive.

La fermeture effective de ce run est prouvée à **15:51:36,923 UTC** dans `finished.json` et `observer.jsonl` : document fermé, invalide et absent de la collection des documents ouverts, sans sauvegarde. SourceUnchanged et CopyAndSourceUnchanged sont vrais, avec le même SHA256 **EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA** pour la source et la copie de départ. **Statut historique : BLOQUÉ sous r3. La cause a été retirée en r4 ; aucun massif ultérieur de cette campagne n’a terminé sa validation.**

## Massifs r4 et r6 : arrêts définitifs de ces essais

Les deux essais ont repris le même périmètre : **2 047 cibles sélectionnées, 2 025 supprimées et 22 survivantes ; 3 600 événements persistés et transmis à la restauration**. La suppression intervient avant l’inventaire de production : zéro snapshot et zéro élément visité au départ. Ce succès de capture/chargement ne vaut pas validation de restauration.

| Essai | Arrêt UTC et dernière progression observée | Statut final |
|---|---|---|
| r4, `antibes-physical-fixed-early-v2`, PID 44936 | **16:09:44,188**, après plus de quatorze minutes ; **4 451 IDs ajoutés / 406 rafraîchissements** dans le dernier heartbeat. Plusieurs transactions d’environ une minute ne produisent aucun changement visible. | **ARRÊT AVANT RÉSULTAT**. Aucun bilan final de fidélité ni fichier restauré sauvegardé. |
| r6, `antibes-physical-fixed-early-v3`, PID 24600 | **16:23:23,496** ; **2 381 IDs ajoutés / 75 rafraîchissements**, sans progression de ces compteurs après 16:16:45,997. Les transactions vides d’environ une minute persistent. | **ARRÊT AVANT RÉSULTAT**. Aucun bilan final de fidélité ni fichier restauré sauvegardé. |

Les preuves définitives sont les deux `aborted-test.json`, accompagnés des heartbeats et événements bruts conservés dans leurs répertoires de run. Les IDs ajoutés incluent des dépendants et ne sont pas un nombre d’objets sources restaurés correctement. Les deux fichiers d’arrêt attestent **SavedRestoredCopy=false et SourceAndDiskCopyUnchanged=true**, avec SHA256 **EB536F0B72E7D60F9E4D079AD6B520032E4DB33F7A869D8A257D43616CAFB2AA** pour la source et la copie initiale. L’arrêt du processus de test ne constitue pas une preuve de fermeture API suivie d’une réouverture d’un résultat restauré.

| Étape | Statut définitif de la campagne | Portée des preuves |
|---|---|---|
| Régressions natives `antibes-fixes-regressions-v3` | **TERMINÉES — 8 PASS / 2 FAIL** | Identités de groupes et six barreaux conformes ; géométrie/recalage des cas vérifiés, mais deux classifications de copies déconnectées différentes. Clôture et source/copie inchangées prouvées ; aucun PASS global brut. |
| Suppression/restauration massive `antibes-physical-fixed-early-v1`, produit r3 | **ÉCHEC AVANT CRÉATION — BLOQUÉ** | Suppression/journal/chargement couvrent 3 600 IDs ; commit de préparation des identités source non validé, zéro ajout/zéro rafraîchissement pendant Restore. Fermeture sans sauvegarde et hashes inchangés prouvés. |
| Massifs r4 et r6 sur copies neuves | **ARRÊTÉS — AUCUN PASS FINAL** | Capture et chargement complets sur le périmètre de l’essai ; restauration interrompue avec pauses longues, sans résultat final. |
| Fidélité, objets supplémentaires et cohortes 176 courbes / 6 barreaux / 80 écarts antérieurs | **NON VALIDÉS EN MASSIF** | Les régressions ciblées ne remplacent pas les vérifications complètes après restauration des objets, connexions, jonctions et quatre excavations. Les 524 isolants exclus restent explicitement comptés. |
| Sauvegarde et réouverture d’un résultat corrigé | **NON EXÉCUTÉES POUR r4/r6** | Aucun fichier restauré sauvegardé ; aucune preuve de persistance. Source et copies initiales inchangées sur disque. |
| Temps et gêne utilisateur | **OBJECTIF NON ATTEINT PAR CES ESSAIS** | Les pauses longues restent observées. Les écarts Idling/Dispatcher ne sont pas des temps d’image et ne prouvent pas une navigation fluide. |

## Retrait du déploiement expérimental

`tmp/history-stress/experimental-deployment-reverted.json` atteste le retour du manifeste à **16:30:57,198 UTC** vers `Revit2025-Maintenance-8A029591BA27/BIMaestro.dll`, SHA256 **8A029591BA27208B66A7A0E8CCE090BC2E473B913A6F2956B1066019DF3ED10A**. La DLL expérimentale r6 n’est plus la cible du manifeste. La sauvegarde du manifeste est référencée dans ce JSON ; **RunningUserRevitNotTouched=true et SourceChangesPreserved=true**. Le retour concerne les prochains chargements ; il n’implique pas un remplacement d’assemblée dans un Revit utilisateur déjà ouvert.

## Exigence clarifiée : historique durable autonome

La copie RVT n’est autorisée que comme **passerelle temporaire** pour capturer rapidement les données initiales. La restauration attendue doit rester possible après fermeture de Revit **sans dépendre de la présence de cette copie**. Retrouver un objet par recopie immédiate depuis le checkpoint ne suffit donc pas à satisfaire le besoin.

L’architecture testée utilise encore le fichier indiqué par les reçus Native/OpeningFallback pour certaines restaurations. Cette campagne **ne valide pas un historique autonome après disparition de la copie**, ni la fidélité massive ou sa persistance. Les corrections et les preuves restent conservées pour analyse ; aucun achèvement total n’est revendiqué.
