# Contrat de preuves du prototype

Ce contrat est une cible pour le nouveau harnais, pas une affirmation de capacités natives déjà établies. Le collecteur accepte les propriétés supplémentaires des anciens rapports sans leur attribuer de signification automatique.

`run.json` :

```json
{
  "version": "2025",
  "pid": 12345,
  "fixturePath": "C:/.../CustomGroupFixture.rvt",
  "headerLabel": "BIMaestro — Essai natif",
  "insertedRowCount": 2,
  "memberParameterIds": [-1010106, -1001007, -1001108, -1001107],
  "nativeInsertionVerified": true,
  "operationExports": ["export exact addGroupHeader@ElemGrid vérifié par le harnais"],
  "moduleHashes": {"og1100asu.dll": "SHA256 du binaire réellement chargé"}
}
```

Les champs de provenance native sont obligatoires pour qualifier la série. Ils ne remplacent pas une revue de la signature et du code du harnais.

Captures obligatoires : `baseline.json`, `inserted.json`, `grouped.json`, `collapsed.json`, `expanded.json`, `restored.json`. Chaque capture expose :

- `version`, `pid`, `path`, `time` UTC, `rows`, `mappings`, `parameters` comme le harnais d'ordre.
- `parameterSchema`, dictionnaire avec les mêmes clés stables `ElementUniqueId|ParameterId|Name` que `parameters`. Chaque valeur expose `storage` (nom StorageType), `groupTypeId` et `readOnly`. Stocker aussi GUID partagé et nature instance/type si disponibles.
- Chaque ligne `rows` expose `row`, `label`, `value`, `marker`, `editable` et `rect` avec `top`, `bottom`. Le marqueur d'en-tête doit être celui observé dans la bibliothèque installée ; ce collecteur reconnaît `^`, `v` et `V`.
- Chaque `mappings` expose `id`, `nativeRow`, `actualLabel`, `expectedLabel`, `matches`. Les paramètres présents dans la baseline doivent être retrouvés après insertion/déplacement. La ligne créée n'est jamais une identité de paramètre.
- Chaque nouveau header expose `rows[].isNativeGroupHeader:true` issu d'un appel natif indépendant `isCellPartOfAGroupHeader`, ou le rapport expose `nativeGroupHeaderTest:{row:NUMERO,result:true}`. Un marqueur et un booléen de manifeste seuls ne suffisent pas.

`baseline` est l'état original sans groupe créé. `inserted` est pris après l'insertion d'un seul en-tête, avant les transferts ; `grouped` après transfert et `expanded` après un cycle de repli/dépli. `insertedRowCount` explicite le nombre réel de lignes créées : `addGroupHeader` peut ajouter l'en-tête et son spacer (deux lignes). Les lignes supplémentaires autres que l'en-tête doivent être vides et ne porter aucune identité de paramètre. Le collecteur n'infère pas ce contrat à partir du seul booléen `nativeInsertionVerified`.

Les snapshots métier des six captures sont identiques, car aucune saisie ne doit être faite pendant cette séquence structurelle. Les essais de saisie sont des paires séparées ; la restauration après saisie doit comparer aux valeurs validées courantes, pas à la baseline initiale.

`interactions.json` facultatif, tableau d'essais indépendants :

```json
[
  {
    "name": "text-enter",
    "kind": "text",
    "before": "text-before.json",
    "after": "text-after.json",
    "targetKey": "WALL_UID|-1010106|Commentaires",
    "expectedChanges": {"WALL_UID|-1010106|Commentaires": "UNIQUE_TEST_VALUE"},
    "tolerance": 0
  },
  {
    "name": "escape",
    "kind": "cancel",
    "before": "cancel-before.json",
    "after": "cancel-after.json",
    "expectedChanges": {}
  }
]
```

`kind` : `text`, `boolean`, `double`, `elementId`, `neighbor`, `cancel`, `undo`, `redo`, `rollback`, `active-editor-refusal`. Le collecteur n'invente pas de scénario manquant. `expectedChanges` énumère toutes les valeurs qui doivent changer, y compris les dépendances autorisées : pas de liste blanche vague. Pour `double`, une tolérance absolue en unités internes peut être fournie ; les clés supplémentaires restent interdites.

Les écritures doivent préciser `targetKey` : le collecteur vérifie que son type API correspond à l'éditeur revendiqué, qu'il est modifiable et qu'il appartient aux membres déplacés (ou, pour `neighbor`, qu'il reste extérieur). `--baseline FICHIER` choisit le rapport de baseline complet. Par défaut le collecteur utilise `original-baseline.json` s'il existe, puis `baseline.json` ; cela évite de confondre le rapport complet et le dictionnaire de valeurs du test de saisie.

Les paires de saisie normale exigent le groupe créé et ses membres présents dans la capture avant. Les paires d'annulation/rollback/refus doivent avoir zéro delta de modèle. Un `rollback` exige également zéro différence de lignes entre ses deux captures : prendre la référence de structure avant l'opération soumise à injection d'échec.

Un `rollback` fournit aussi `failureSnapshot`, nom d'une capture intermédiaire où la mutation est visible avant sa restauration ; le collecteur rejette une simple paire de snapshots identiques ne démontrant aucune opération. Le refus pendant saisie exige `nativeState.activeCell=1`, la même cellule et le même buffer visible GXEDIT avant/après. Une absence de delta de modèle ne démontre pas, à elle seule, qu'un buffer non validé a été préservé. Annuler et Rétablir doivent chacun changer une valeur attendue ; une simple absence de différence n'est pas une preuve de ces actions.

La synthèse distingue `passed`, `failed`, `not_recorded`. Une série n'est déclarée complète que si les six étapes structurelles et chaque type d'éditeur de la fixture sont présents. Même complète, elle reste un prototype Revit 2025 ; aucune qualification 2023/2024/2026/2027 ou d'une version installée différente n'en est déduite.
