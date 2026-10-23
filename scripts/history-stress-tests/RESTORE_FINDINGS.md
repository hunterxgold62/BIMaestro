# Causes ciblées des écarts de restauration Antibes v3

Analyse en lecture seule du 22 octobre 2026, sans modification du produit ni appel
Revit supplémentaire. Elle porte sur l’essai **early v3**, produit SHA256
`C9CED231881FBCDE747CCD3E3CAEA34907B4A76B3868CF176899ED45A0DA34A0`.
Les événements ciblés n’avaient aucun snapshot individuel ; ils ont utilisé le
checkpoint RVT d’ouverture et une recette `native_archive` hydratée à la demande.

Le [rapport de validation v3](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-stress-tests/VALIDATION_HISTORY_MASS_EARLY_V3.md>)
conserve les dénominateurs, exclusions, durées et limites du banc. La restauration
a retourné après l’annonce de fermeture forcée ; aucune sauvegarde/réouverture du
résultat v3 n’est validée. Aucun succès global n’est déduit de cette analyse.
Le complément physique préparé v2, terminé ensuite, est présenté séparément en
fin de document. Les observations v3 restent conservées avec leur contexte.

## Canalisations : ports de piquage perdus, confirmé

| Source | Ports avant/après | Éléments conservés | Piquages concernés |
|---|---:|---|---|
| Pipe `819865` | 4 → 2 | Axe, diamètre, type, niveau et bbox identiques | `923378`, `923374` |
| Pipe `945923` | 5 → 2 | Axe, diamètre, type, niveau et bbox identiques | `982848`, `982660`, `978412` |

Les ports manquants sont de type `ConnectorType.Curve`, sur la canalisation
porteuse. Les cinq raccords de piquage sont présents, mais leurs connexions au
porteur ne sont pas rétablies. Les cinq entrées correspondantes de
`restore-result.json.ConnectionFailures` indiquent un connecteur voisin
introuvable ; ce sont cinq des sept échecs de connexion du batch.

La voie native copie actuellement une racine à la fois puis régénère. La phase
`Reconnect` cherche ensuite deux connecteurs déjà présents et coïncidents ; si le
port latéral a disparu, `Match` ne peut pas le produire. Elle ne remplace pas cet
échec par la création d’un piquage. Le code possède une voie
`NewTakeoffFitting`, mais le réparateur invoqué en fin de lot filtre les recettes
`family` et les résultats `Existing`, donc pas ces créations `native_archive`.

Sources : [copie native unitaire](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:1156>),
[réparateur de raccords](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs:584>),
[reconnexion](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs:674>),
[recherche de port](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs:530>),
[création de piquage existante](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs:235>).

Correction à étudier : copier ensemble les composantes de réseau, ou reconstruire
explicitement les piquages dont le port porteur manque. Chaque tentative doit
vérifier le type, le graphe et l’absence de déplacement du porteur, puis annuler
en cas d’écart. Aucun correctif ni benchmark de cette alternative n’a été exécuté.

## Mur : ordre des jonctions d’extrémité, inférence forte

Le mur `556631`, épaisseur **350 mm**, conserve exactement son axe, son niveau et
son type. Sa bbox perd pourtant **175 mm** à chacune des deux extrémités X.
Les murs perpendiculaires `556634` et `556637` touchent exactement ces extrémités ;
leurs bboxes gagnent en retour **175 mm** en Y. Cette redistribution d’une
demi-épaisseur est une forte indication que l’ordre de nettoyage des jonctions
d’extrémité a changé après les copies successives.

Ce n’est pas une preuve API directe de l’ordre avant/après : le banc v3 n’a pas
capturé `LocationCurve.ElementsAtJoin`, `LocationCurve.JoinType` ni
`WallUtils.IsWallJoinAllowedAtEnd`. La liste `JoinGeometryUtils` du mur `556631`
est vide avant/après ; elle ne prouve pas l’absence de jonctions d’extrémité.
Le champ `JoinsMatch=true` du banc est donc insuffisant pour expliquer sa bbox.

La recette native capture les connexions MEP et l’archive ; elle ne conserve pas
séparément cet ordre de jonction de mur. La documentation API Revit 2025 locale
distingue bien l’ordre des participants à la jonction (`ElementsAtJoin`) du type
de nettoyage (`JoinType`). Le setter `ElementsAtJoin` attend une permutation des
participants déjà joints, et ne doit pas servir à introduire de nouveaux voisins.

Sources : [contenu de la recette native](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:412>),
`C:/Program Files/Autodesk/Revit 2025/RevitAPI.xml:123141`,
`C:/Program Files/Autodesk/Revit 2025/RevitAPI.xml:123154`,
`C:/Program Files/Autodesk/Revit 2025/RevitAPI.xml:81665`.

Correction à étudier : capturer l’autorisation, le type et l’ordre des jonctions
aux deux bouts, puis les rétablir après création des murs participants. La copie
par lot peut également préserver davantage de contexte ; cela reste à tester.

## Plantes : translation confirmée, étape exacte non instrumentée

La plante `520877` conserve X/Y, les trois bases de transformation, le type et le
niveau ; elle est déplacée de **−457,966 mm en Z**. Les cinq instances de la famille
`CML_Arbre-22` présentes dans l’inventaire sont décalées verticalement : environ
−110, −319, −278, −249 et −458 mm. Deux siphons d’une autre famille perdent également
leur décalage de −183 mm et se retrouvent à l’altitude du niveau.

Le comportement commun est compatible avec un recalcul de placement ou de
réhébergement. Les bruts ne distinguent cependant pas un changement au moment de
l’ouverture du document historique d’un changement pendant `CopyElements` ou une
régénération ultérieure. Ils ne contiennent pas le point de la source historique
juste avant copie. Le même UID de niveau ne prouve pas, à lui seul, la conservation
de tous les paramètres d’altitude de l’instance.

La faiblesse du contrôle produit est certaine : la recette `native_archive` ne
porte pas de point/transformée ou de paramètres de placement à réappliquer.
Après copie, le choix de racine peut accepter l’unique candidat de même
classe/catégorie lorsque la bbox historique ne correspond plus. Après commit,
le résultat est déclaré créé si l’objet existe, sans comparaison de sa position
historique. L’instance v3 peut ainsi être déclarée `Created=true` avec une altitude
incorrecte.

Sources : [hydratation historique](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:260>),
[champs de recette](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:422>),
[acceptation du candidat](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:1160>),
[validation après commit](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs:285>).

Diagnostic minimal futur : enregistrer le point, la transformée et les paramètres
d’altitude de la source à l’hydratation, puis les mêmes valeurs juste après copie
et après commit. Une correction de position doit rester limitée à un objet créé
par cette restauration, vérifier ses contraintes et sa géométrie, et annuler en
cas de désaccord. Aucune translation automatique n’est appliquée par cette note.

## Bruts et portée

Les observations proviennent de `inventory.json`, `verification.json`,
`restore-result.json`, `independent-fidelity.json` et
`independent-fidelity-portcoords.json` dans
`tmp/history-stress/antibes-mass-early-v3/`.
Les erreurs de position et de ports ci-dessus sont indépendantes des références
vers les isolants volontairement exclus. Elles ne justifient pas d’assimiler tous
les échecs de connexion bruts à des pertes de graphe physique : le rapport v3
distingue ces cas de politique des écarts matériels restants.

## Complément : essai physique préparé v2

Run `tmp/history-stress/antibes-physical-prepared-v2`, produit chargé SHA256
`A5B1DD546FB4FE6246F4FCB739DCD63C0846E0D52BB2C424B562BC3A90796947`,
mode Détaillé. La sauvegarde, la fermeture effective de l’ancien document et
l’ouverture d’un document distinct sont attestées par `persistence.json` ;
`reopened-verification.json` vérifie le résultat rechargé. Le fichier source et
la copie de départ sont inchangés d’après leurs hashes (`integrity.json`).
Ces preuves de persistance ne constituent pas un succès global de fidélité.

Le [rapport physique v2](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/scripts/history-stress-tests/VALIDATION_HISTORY_PHYSICAL_ANTIBES.md>)
présente le bilan complet. Le rapprochement ciblé, par UID source et ID d’audit,
est conservé dans `independent-native-root-findings.json` du run v2.

| Cas v3 | Résultat physique préparé v2 | Conclusion limitée à ce cas |
|---|---|---|
| Pipe `819865`, 2 ports de piquage perdus | Recette `network`, vérification passée avant/après réouverture | Perte v3 non reproduite sur cette voie préparée |
| Pipe `945923`, 3 ports de piquage perdus | Recette `network`, 5 ports dont 3 `Curve` ; graphe physique conforme après retrait diagnostique du seul voisin isolant exclu | Perte v3 non reproduite ; le contrôle brut de connexions reste faux à cause du calorifuge |
| Mur `556631`, bbox modifiée de 175 mm | Recette `wall`, vérification passée, bbox incluse, avant/après réouverture | Écart v3 non reproduit sur la recette directe ; aucun correctif général des jonctions n’est démontré |
| Plante `520877`, altitude fausse | Recette `native_archive`, même translation de −457,966 mm, conservée après réouverture | Écart reproduit ; l’étape source historique/copie/régénération reste non instrumentée |

Pour `945923`, l’ordre d’énumération des ports change à la réouverture ; leurs
identités, positions et relations contrôlées restent équivalentes. Les résultats
v2 utilisent d’autres recettes et un autre contexte de suppression que v3 : ce
n’est pas une comparaison isolant uniquement le délai de préparation ou le mode.

### Les 176 courbes manquantes échouent sur leur groupe

Les **108 `ModelLine` et 68 `ModelArc`** déclarées manquantes sont toutes membres
du seul groupe `440709`, « Groupe 1 », UID
`1d08190d-9bd6-40af-9ff2-20e4ab0748a3-0006b985`.
Le catalogue identifie une vraie instance `Group` (`ElementType=false`), catégorie
« Groupes de modèles », ID de catégorie `-2000095` ; ce n’est pas un `GroupType`.
Le banc lui attribue `ProtectionReason=not-model-category` : il n’a donc pas de
`Spec`, ni de présence dans les racines ou objets physiques directement ciblés.

Le groupe ne figure pas dans les IDs retournés par `Document.Delete`, mais il
figure dans les IDs supprimés de `DocumentChanged` et dans les dépendants du
batch. Son événement a bien été journalisé et transformé en requête : l’Item
retrouvé par `DeletionAudit.ElementId=440709` contient `SourceUniqueId=""`,
`RecipeKind=native_archive`, `Reason=archive` et le diagnostic exact :

> L’élément n’est pas présent comme objet de modèle dans la copie d’ouverture.

Le filtre de `HydrateOpeningRequest` rejette cet objet au contrôle
`IsEligibleElement`. Le champ `ParentOutcome=not_in_restore_request` des membres
signifie ici qu’aucun parent exploitable n’est indexé par UID après hydratation ;
il ne prouve pas l’absence de l’événement du groupe dans le journal ou le batch.

Les **176 Items membres**, rapprochés un à un par `SourceUniqueId` de l’inventaire,
ont une recette `native_archive`, un snapshot présent, `Reason=creation` et le
même diagnostic exact :

> L’identité d’un membre du groupe est ambiguë.

Cette exception vient de `CopyRoot`, qui exige un unique candidat de même
classe/catégorie/bbox pour chaque membre. L’inventaire source démontre déjà
**13 paires** de `ModelLine` partageant exactement leur bbox et leur courbe ; par
exemple `439377/439876` et `439385/439884`. Ce descripteur ne peut donc pas garantir
leur identité distincte. L’échec annule la tentative de copie du groupe et se
répète pour ses requêtes membres.

Ce résultat ne démontre pas que les courbes sont intrinsèquement impossibles à
restaurer : le produit possède aussi une recette directe `model_curve`. Il
démontre l’échec de la voie d’agrégat retenue ici. Le périmètre du banc devrait
distinguer une racine logique de groupe de ses objets physiques ; cela ne retire
pas les 176 courbes effectivement ciblées et non identifiées du bilan.

Sources : [filtre d’hydratation](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:264>),
[matching des membres de groupe](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:877>),
[descripteur géométrique](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryNativeArchive.cs:958>),
[recette directe de courbe](<C:/Users/plemert/OneDrive - SAS H.C.M. HOLDING CABINET MERLIN/Documents/BIMaestro/BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs:262>).

### Six barreaux sans identité retrouvée : absence physique non prouvée

Les sources `557051` à `557056`, famille « Poteau rectangulaire chanfreiné »,
type « 5x2cm Acier zingué galvanisé », n’ont pas de correspondant portant leur
origine historique. Leurs six Items contiennent `Reason=creation` et le message
Revit exact `Copying one or more elements failed.`

Les six ont le même point source approximatif `(-55,110452 ; 54,897920 ; 0)` pied,
et trois hauteurs de bbox, chacune répétée deux fois : environ 3,458963,
4,037756 et 3,748360 pieds. L’identité serait donc ambiguë même avec une simple
comparaison de nom et de position.

Parmi les 999 objets supplémentaires sans origine d’une cible, six instances
`FamilyInstance` de catégorie « Barreaux » portent le même nom de type : IDs
`1117107` à `1117112`. Les exports `UnexpectedModelObjects` donnent leurs IDs,
UIDs, classe, catégorie et nom, mais **aucune position, bbox, type UID ou origine
de génération**. Le rapprochement par nombre/nom est seulement une piste.
On ne peut ni les déclarer équivalentes aux six sources, ni affirmer que les six
formes physiques sont absentes. Le constat certain reste l’absence de
correspondance d’identité validée. Aucun remappage ni correctif produit n’a été
appliqué.
