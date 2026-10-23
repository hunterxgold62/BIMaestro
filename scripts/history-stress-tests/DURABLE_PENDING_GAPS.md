# Historique durable : écarts encore ouverts

État du code examiné le 23 octobre 2026. Cette note est un audit de lecture du produit, pas un résultat Revit. La conversion massive des capsules compactes doit fonctionner avant d’élargir son périmètre. Les tests isolés de lecture et d’identité des wrappers ne prouvent pas l’autonomie des recettes directes après modification du projet.

## Chemins actuellement incomplets

| Cas | Preuve dans les sources | Conséquence |
| --- | --- | --- |
| Recette directe sans artifact | `ElementHistoryDurableBridge.EventsPersisted` conserve les nouveaux événements via `currentRecord`, mais `ProcessPending` rejette ensuite `SourceArtifact == null` avec « Travail de conversion sans source native ». | Le travail reste visible en échec ; ses types et autres définitions ne sont pas figés. Ce n’est plus un faux `Complete`, mais ce n’est pas une conversion opérationnelle. |
| Ancienne petite archive native sans version | `ConvertSmallNative` exige désormais `TypeVersion` avant de publier les octets. Les anciennes archives n’ont pas de définitions immuables versionnées. | Une simple enveloppe JSON/GZ ne suffit pas à en faire une restauration autonome : le travail reste explicitement en attente de migration, au lieu d’annoncer un succès que `ValidateDurableTypeCompatibility` refuserait ensuite. |
| Modifications en mode Simple | `ElementHistoryReconstruction.Capture(automaticCapture:true)` utilise `CaptureCore(... includeDetails:false)`. `ElementHistoryTracker.ProcessDeferredRestorationCapture` retourne sur `!CaptureDetailedDeletedMesh`. | Attendre 60 secondes ne complète pas les joins, relations, circuits et connexions différés des objets modifiés. Le choix d’affichage du mesh commande encore une partie de la restauration. |
| Copie d’ouverture devenue obsolète | `ElementHistoryOpeningArchive.Invalidate`, `TryCreateRecipe` et `FreezeForDeletion` refusent les éléments/ensembles/types changés ; `BuildDeleteDelta` retire alors leur fallback d’ouverture. | Ce refus protège l’intégrité. Il ne faut pas remettre l’ancien fallback pour faire disparaître une erreur de conversion : il restaurerait potentiellement la mauvaise géométrie. |
| Définitions incomplètes | `ElementHistoryNativeArchive.Durable.DurableDefinitionReferences` parcourt `parameter.Reference`, mais pas `parameter.Definition`. `DurableDefinitions` ne contient que types, niveaux, matériaux et familles. Le calcul de `required` ignore les UID absents de cette collection. | Les définitions/bindings de paramètres projet et certaines références non prises en charge peuvent être exclues silencieusement de la preuve d’autonomie. Elles doivent être classées et conservées, ou bloquer la conversion. |
| Import direct incomplet | `ElementHistoryRestoration.ImportDurableDefinition` importe et remappe le type primaire. | Une future capsule `DefinitionOnly` pour les recettes géométriques doit aussi assurer les niveaux, matériaux, références de paramètres et définitions de paramètres nécessaires, avant de créer l’objet. |

La conservation des paramètres d’instance en JSON ne suffit pas à conserver la famille, ses formules et ses types. Une référence `Host` désigne une dépendance physique à restaurer/résoudre ; elle ne doit pas être traitée comme une simple définition de type.

## Correction bornée proposée, non appliquée

1. Conserver comme autorité la **recette de l’état réel**, capturée après le dernier `DocumentChanged` et gelée à la suppression : axe/point, niveau et décalage, placement, paramètres, joins, ports et connexions. La conversion doit partir d’une copie détachée de cette recette. Elle ne doit pas remplacer sa géométrie par celle de la copie d’ouverture ni mélanger des relations anciennes aux relations actuelles.
2. Figer les définitions dans des capsules compactes réutilisables par `(session, UID de définition, révision)`, puis publier leurs octets avec le Store JSON.GZ existant. Une même définition inchangée sert à plusieurs événements. La copie d’ouverture peut fournir **seulement** une définition dont l’absence de modification est prouvée ; elle ne fournit pas la géométrie d’un objet modifié.
3. Capturer les révisions de définition au moment du snapshot. Une définition encore présente après la suppression peut être exportée depuis le document vivant si sa révision correspond exactement à celle du snapshot. Si elle a changé/disparu avant son gel, garder le job en échec explicite. Ne pas substituer une définition actuelle de même nom.
4. Publier la recette directe avec un artifact `DefinitionOnly` validé, puis importer/remapper toute la fermeture de définitions avant restauration. `PublishConversion`, relecture validée et acquittement restent dans cet ordre. Une dépendance non conservée interdit le statut terminé.
5. Pour les ensembles complexes dont aucun état natif actuel n’était prêt avant la suppression, conserver l’échec explicite. Une tâche exécutée après suppression ne peut pas copier un objet qui n’existe plus. Une protection systématique de ce cas exige une capture avant suppression ; elle dépasse la seule branche « recette directe + définitions ».

**Révision :** `Element.VersionGuid` ne suffit pas pour les changements pendant la session. Les remarques de l’API Revit 2025 (`RevitAPI.xml`, propriété `Element.VersionGuid`) indiquent qu’une version couvre plusieurs transactions entre enregistrements/synchronisations/rechargements. Utiliser des compteurs `DocumentChanged` de session pour les définitions et leurs dépendances ; invalider aussi sur les transactions internes qui changent réellement le modèle.

## Diff A proposé : séparer restauration légère et mesh

Ce diff de principe n’est pas appliqué. Il conserve les budgets existants et ne débloque pas la copie native complexe en mode Simple.

```diff
 // ProcessDeferredRestorationCapture : les captures légères ont déjà été traitées.
 if (inventoryOnly) return;
 if (selectionOnly && state.LightPending.Any(id => selected?.Contains(id) == true)) return;
-if (!CaptureDetailedDeletedMesh) return;
 if (!selectionOnly && HasPendingBasicInventory(doc)) return;

 // Après le garde des checkpoints WholeModel, avant Enrich :
+if (!CaptureDetailedDeletedMesh && (current.RequiresDetailedCapture
+    || current.Recipe == null || current.Recipe.Native != null))
+{
+    // Ce cas complexe reste explicitement en attente ; aucun détail annoncé.
+    state.Pending[idValue] = current;
+    continue;
+}

 current.Recipe = ElementHistoryReconstruction.Enrich(element, current.Recipe,
-    reason => failure = reason, allowHeavyTerrain: true);
+    reason => failure = reason, allowHeavyTerrain: CaptureDetailedDeletedMesh);

 // Conserver les conditions existantes CaptureDetailedDeletedMesh pour GhostMesh/OBB.
```

Conserver les limites de 20/25 ms, le nombre de candidats et les contrôles d’inactivité. Donner priorité aux recettes devenues sales après modification, pour éviter qu’une longue file initiale retarde leurs relations récentes. Ces budgets coopératifs bornent le nombre de lectures ; ils ne peuvent interrompre un appel Revit indivisible. Ce diff ne résout à lui seul ni le gel des définitions ni une suppression précédant le prochain passage de capture.

## Tests requis avant toute affirmation de réussite

- **Conversion pure :** comparer la recette source gelée et la conversion relue. Géométrie, type source, niveau/décalage, paramètres, joins, ports et connexions doivent rester identiques ; seuls les liens de capsule et les métadonnées de version associées peuvent s’ajouter. Une modification de la recette d’entrée par le convertisseur doit échouer. Le contrat est préparé dans `TestDurableSourceRecipePreservation.ps1` ; il attend les sorties réelles du futur chemin direct.
- **Révision :** même type modifié entre deux transactions sans Save ; la capsule ancienne ne doit jamais satisfaire la nouvelle recette. Tester aussi définition modifiée après suppression, avant conversion.
- **Paramètres :** type/famille purgés, niveau et matériau référencés absents, paramètre projet non partagé et paramètre partagé, référence `ElementId`, circuit imbriqué. Vérifier définitions/bindings et valeurs, pas seulement la présence d’un artifact.
- **Scénario réel 60 s :** ouvrir une copie neuve ; pendant la minute déplacer/modifier un axe, modifier une jonction, une dimension/valeur et une définition ; supprimer environ la moitié du périmètre physique. Ne pas forcer une préparation cachée ni déduire la couverture du temps écoulé. Relever pour les éléments modifiés les versions/révisions et le snapshot réellement gelé avant suppression.
- **Autonomie :** fermer le document, terminer la conversion durable, rendre indisponibles les passerelles complètes, restaurer depuis JSON.GZ après purge des définitions courantes, puis sauvegarder et rouvrir. Comparer identité injective, géométrie, joins, ports, paramètres et définitions. Une restauration immédiate depuis le document encore ouvert ne valide pas cette étape.

Le statut final reste **EN ATTENTE** pour ce périmètre. Un job conservé, un fichier GZ existant, une conversion de capsule non testée en restauration ou 60 secondes d’attente ne constituent pas un PASS d’autonomie.
