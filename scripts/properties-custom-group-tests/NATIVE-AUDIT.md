# Groupe personnalisé natif : audit du prototype Revit 2025

Audit du 23 octobre 2026. Aucun fichier de production n'a été modifié pour cette exploration. Les constats statiques ci-dessous proviennent des DLL installées ; les résultats d'exécution restent séparés. Le prototype est limité au PID explicitement désigné, au fichier jetable `PropertiesCustomGroupFixture.rvt` et aux empreintes exactes ci-dessous.

## Ce qui est effectivement démontré

Dans l'essai `2025-r4`, Revit **2025, build 25.4.41.14, PID 12992**, la commande `group-insert` a fait passer la grille native de **44 à 46 lignes**. L'export natif `isCellPartOfAGroupHeader` a renvoyé **1** pour la ligne 2 intitulée « Mes paramètres métier ». Il s'agit donc d'une ligne reconnue comme en-tête par le contrôle Revit, et pas uniquement d'un bandeau dessiné.

Les identités des paramètres visibles et leurs libellés concordaient après insertion puis création de l'en-tête. L'empreinte des valeurs et groupes de définition du modèle est restée identique : `B3A1560BD2753788F195234BF31ACA1C08AE0140A9500F0A99ABBC107CE4BBC6`.

Cependant, à la commande suivante, la grille contenait de nouveau **44 lignes** et le titre avait disparu. La vérification `before-members` a échoué : les identifiants pointaient de nouveau vers leurs positions d'origine. `mutationAttempted=false` dans `group-error.json` confirme que le déplacement des membres n'a pas été tenté après cet échec.

Preuves locales sous `tmp/properties-custom-group-tests/2025-r4/` :

- `group-step-baseline.json`, `group-step-insert.json`, `group-step-header.json` : étapes de création et correspondances natives.
- `inserted.json` : 46 lignes et `nativeGroupHeaderTest={row:2,result:true,nativeResult:1,error:null}`.
- `group-insert.json` : empreintes métier identiques avant/après.
- `group-step-before-members.json`, `group-error.json` : retour à 44 lignes et arrêt avant tout déplacement.

Ces résultats démontrent une création native temporaire. Ils ne qualifient pas la persistance de ce groupe, ni la saisie de ses membres. Un essai atomique « insertion puis membres dans le même événement » peut distinguer la faisabilité instantanée de la tenue après retour à Revit ; sa réussite instantanée ne suffirait pas à démontrer la tenue au prochain clic ou rafraîchissement.

## Empreintes des modules examinés

| Module Revit 2025 | SHA256 |
| --- | --- |
| `DesktopMFC.dll` | `76FDA8B73A9E155F722FA70C14201D9F144687E11ED8EE0D629EA1DBA42C236E` |
| `Utility.dll` | `E32587919DDC25B9ADF0FA978FC6A03400CE288355E08092858C869CE4D23E0A` |
| `og1100asu.dll` | `19F8A8BA3BD983C42897443EEC79203DEC8F47A31B67D63BD9B0C5BC5B7F7371` |
| `C:\Windows\System32\mfc140u.dll` | `FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC` |

Les trois premiers modules sont dans `C:\Program Files\Autodesk\Revit 2025`. Le pont existant vérifie déjà le MFC chargé avant d'utiliser `CWnd::FromHandlePermanent` à la RVA `0x2957C0`, puis lit les métadonnées RTTI du véritable `ElemGrid`. Les décalages de bases C++ ne doivent pas être supposés identiques entre les différents exports.

## Exports utiles et ABI observée

Sur Windows x64, les délégués transmettent le pointeur `this` en premier argument. Les types `ROWCOL`/`UINT` et `BOOL` restent sur 32 bits ; les pointeurs sont sur 64 bits.

| Module, RVA | Export exact | Signature utilisée ou étudiée |
| --- | --- | --- |
| DesktopMFC, `0x342360` | `?addGroupHeader@ElemGrid@@QEAAXPEAHAEBVAString@@@Z` | `void(ElemGrid complet*, int* row, AString const*)` |
| DesktopMFC, `0x342450` | `?addGroupHeaderCommon@ElemGrid@@AEAAXPEAHAEBV?$optional@VForgeTypeId@@@std@@AEBVAString@@@Z` | Appelé par l'overload précédent ; pas d'appel direct dans le laboratoire. |
| DesktopMFC, `0x347C10` | `?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z` | `int(ElemGrid complet*, uint row, uint col)` ; preuve native de l'en-tête. |
| DesktopMFC, `0x344CD0` | `?findParamIdRow@ElemGrid@@QEAAHVElementId@@@Z` | `int(ElemGrid complet*, long parameterId)` en 2025 ; vérifie les véritables correspondances. |
| Objective Grid, `0xAE530` | `?InsertRows@CGXGridCore@@UEAAHKKPEAHPEAVCObArray@@1KIW4GXCmdType@@@Z` | `int(Core*, uint row, uint count, heights*, cells*, rowStyles*, uint columns, uint flags, int command)` |
| Objective Grid, `0xAF060` | `?RemoveRows@CGXGridCore@@UEAAHKKIW4GXCmdType@@@Z` | `int(Core*, uint first, uint last, uint flags, int command)` ; non utilisé pour forcer une restauration incertaine. |
| Objective Grid, `0xACCC0` | `?ResetCurrentCell@CGXGridCore@@UEAAXH@Z` | `void(Core*, int storeIfModified)` ; toujours `0`, seulement hors saisie. |
| Utility, `0x1DD7D0` | `??0AString@@QEAA@PEB_W@Z` | Constructeur `AString*(AString*, wchar_t const*)`. |
| Utility, `0x1DD830` | `??1AString@@QEAA@XZ` | Destructeur `void(AString*)`. |

Le constructeur `AString` observé initialise un seul emplacement de taille pointeur et délègue sa construction à la bibliothèque correspondante. Le laboratoire utilise le constructeur et le destructeur du même module vérifié, avec une zone de stockage de huit octets. Il ne passe jamais directement un pointeur UTF-16 à la place d'une référence `AString`.

Le corps de `addGroupHeader` texte construit un `optional<ForgeTypeId>` **vide** puis appelle `addGroupHeaderCommon`. Le corps commun crée les styles natifs, une ligne d'espacement à l'indice fourni, puis l'en-tête à l'indice suivant. Il écrit notamment le titre en colonne 1 et le contrôle/marqueur en colonne 3. Le curseur est incrémenté deux fois. Aucun appel `InsertRows` n'apparaît dans ce corps : les deux lignes sont donc réservées avant son invocation.

Le prototype ne fabrique pas de structure `CGXStyle`, de `CGXRange`, de `ForgeTypeId` ou de `std::optional` manuellement. Les exports `SetStyleRange`, `SetValueRange` et leurs surcharges existent, mais leurs paramètres C++ et métadonnées de cellule rendent une copie brute ou une modification d'un style emprunté moins sûre que l'usine native d'en-tête. Une cellule de paramètre conserve son véritable style et son identité lorsqu'elle est déplacée ; elle n'est pas recréée à partir de son texte.

## Pourquoi les flags ne garantissent pas la tenue du groupe

Le désassemblage de `ElemGrid::UpdateInsertRows`, RVA `0x34EDA0`, montre deux actions :

1. Appel de la mise à jour Objective Grid de base.
2. Si le callback utilisateur existe, appel de sa méthode virtuelle à l'emplacement `+0x20`, indépendamment des flags de redessin.

`UpdateChangedRowCount`, RVA `0x34ED30`, possède la même notification finale. La reconstruction observée après retour à Revit est donc **compatible avec une notification différée de ce callback**. C'est une hypothèse étayée, pas l'identification complète de la tâche native qui reconstruit la palette. Le type concret et l'ordonnancement du callback n'ont pas été tracés à l'exécution.

Passer `flags=0`, ne pas demander de hint, ou suspendre `WM_PAINT` par `WM_SETREDRAW` ne désactive pas cette notification. Une suspension de dessin évite un affichage intermédiaire, mais ne rend pas la nouvelle organisation persistante.

Il ne faut pas tenter de retirer temporairement le callback avec les exports de getter/setter. Le setter `setUpdateElemGridUserCB`, RVA `0x34CAE0`, remplace le pointeur puis appelle le **destructeur virtuel supprimant l'ancien objet**. Une séquence « setter null, puis restaurer l'ancien pointeur » réutiliserait donc un objet détruit. Aucun callback, offset ou vtable n'a été modifié par ce prototype.

Une autre voie de recherche serait `StoreInsertRows`, qui agit sur la structure de données sans emprunter la même commande de haut niveau. Elle n'est pas qualifiée : cette méthode ajuste aussi hauteurs, styles, plages et cellules couvertes, et son corps contient d'autres notifications internes. L'utiliser ne constitue pas une correction démontrée et demanderait un protocole distinct de cohérence native.

## Conditions de sécurité et limites restantes

Les six membres prévus sont les commentaires, le booléen de délimitation de pièce, le décalage inférieur, la contrainte de base, puis les paramètres projet `BIMAESTRO_Code_metier` et `BIMAESTRO_Statut_validation` lorsqu'ils existent. Leur provenance, leur identifiant, leur groupe de définition et leur groupe natif initial sont conservés dans `group-members-plan.json`.

Avant et après chaque mutation d'interface, le laboratoire vérifie les identités natives de tous les paramètres visibles capturés. Une identité ou un nombre de lignes inattendu arrête l'opération. En cas d'échec après mutation, il demande à Revit une reconstruction par désélection/resélection ; il ne force pas une suppression de lignes basée sur des indices devenus douteux. Aucune commande de l'expérience n'appelle `Parameter.Set`, ne réaffecte les groupes de définition, ni ne recrée un paramètre.

Un en-tête textuel sans `ForgeTypeId` peut aussi avoir des limites de repli ou de persistance d'état. La reconnaissance native de l'en-tête ne prouve pas ces comportements. Les essais de saisie, validation, annulation, listes, unités, repli et reconstruction doivent porter sur un groupe encore présent au moment réel de l'interaction. Une réinsertion automatique avant chaque dessin pourrait former une boucle de notifications/reconstructions ; elle ne doit pas être retenue sans mesure et preuve de stabilité.

La conclusion à ce stade est limitée : **création temporaire d'un groupe natif démontrée ; maintien après reconstruction et saisie fiable non qualifiés**. Aucun élargissement de la fonction de production n'est justifié par ces seuls résultats.

## Documentation primaire

- [Perforce — InsertRows](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_8377.htm) : distinction entre stockage des lignes et mise à jour de l'affichage.
- [Perforce — UpdateInsertRows](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_2qib.htm) : flags et création de hint.
- [Perforce — StoreInsertRows](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_2khf.htm) : ajustement des structures internes et contrat de `bProcessed`.
- [Perforce — ResetCurrentCell](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_1nn0.htm) : rôle de `bStoreIfModified`.

`InspectNativeBridge.ps1` permet de reproduire les lectures d'exports et de désassemblage. Il ne compile ni ne lance Revit. Son hash du complément de laboratoire doit viser un artefact encore présent et correspondant réellement au processus testé ; un ancien dossier dont la DLL a disparu ne fournit pas cette preuve.
