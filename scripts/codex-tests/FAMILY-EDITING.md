# Édition de familles — première étape

## Utilisation

Ouvrir la famille dans l'éditeur Revit et ouvrir le panneau BIMaestro. Le mode
lecture autorise l'inspection ; les écritures suivent les autorisations du panneau.
La famille peut avoir été construite manuellement et n'a pas besoin de construction.json.

- `revit_inspect_family` : état réel, paramètres, inventaire paginé, identifiants,
  encombrements, profils et limites des extrusions, matériaux et visibilité des formes.
- `revit_edit_family_representation` : ajouter/remplacer/retirer un groupe nommé
  de courbes ou régions. Remplacement et retrait ne concernent que les éléments
  portant le marqueur BIMaestro de ce groupe. Les créations nouvelles marquent aussi
  leurs dessins ; les anciennes familles restent inspectables mais leurs dessins
  non marqués ne peuvent pas être remplacés par cet outil.
- `revit_edit_family_extrusion` : modifier début/fin d'une extrusion existante.
  Les limites associées à des paramètres doivent passer par les outils de paramètres.
- `revit_inspect_family_element` : tous les paramètres natifs d'un élément, leurs
  identifiants, associations existantes et compatibilité avec une association.
- `revit_configure_family` : paramètres internes ou partagés (GUID explicite),
  longueur/angle/entier/nombre/Oui-Non/texte/matériau, portée type ou occurrence,
  formules natives, nouveaux types et associations aux propriétés des éléments.
  Aucun filtre selon l'origine manuelle/BIMaestro ni selon la classe d'élément :
  la possibilité d'association est déterminée par Revit.

Pour une case « Service de table » par table placée : inspecter les éléments,
ajouter un paramètre yesno avec instance=true, group=visibility, value=true,
formula=null et shared_guid="", puis associer la propriété `visibility` de chaque
assiette, couvert et verre au même paramètre dans le même lot. Conserver le plateau
et les pieds hors de ce lot. Le nouveau paramètre est initialisé dans tous les types
existants. Après chargement dans un projet, chaque table a sa valeur d'occurrence.

`parameters.mode` est explicite : add refuse les doublons ; reuse conserve les
valeurs, formules et portée ; update peut changer la portée, la formule et la valeur
du type courant. formula=null conserve, chaîne vide supprime. value=null conserve
ou laisse la formule calculer. Tous les noms sont créés avant de définir les formules.
Les propriétés d'association sont `visibility`, `dimension_label` pour une cote,
ou le `parameter_id` exact lu par l'inspection. Une association existante ne peut
être remplacée ou supprimée qu'avec replace_associations=true ; un nom de paramètre
vide demande alors une dissociation. Les erreurs annulent tout le lot.

Exemples de demandes : « Ajoute un arc symbolique d'ouverture en plan à cette
fenêtre, dans un groupe nommé Ouverture » ; « Modifie le groupe Ouverture » ;
« Passe la profondeur de cette extrusion non pilotée à 120 mm ».

Les coordonnées doivent provenir de l'inspection et de la demande de l'utilisateur.
Le document reste ouvert, sans sauvegarde automatique et sans nouvelle famille.
Un groupe de transactions regroupe l'édition et les éventuels imports de régions :
échec = annulation du lot, succès = une entrée d'annulation Revit.
La suppression est annulée si Revit supprime des dépendances hors du groupe ciblé.
Les outils n'enlèvent pas les verrouillages. Les associations sont conservées sauf
demande explicite de remplacement/dissociation dans revit_configure_family.

## Limites actuelles

Les dessins libres ont des dimensions fixes. Leur visibilité ne masque pas
automatiquement les formes existantes (`hide_model_in=[]`). Les régions utilisent
des familles de détail imbriquées ; leurs définitions inutilisées ne sont pas purgées.
Les plans de travail auxiliaires sont conservés pour éviter de supprimer des dépendances Revit.
L'inventaire n'est pas une extraction exhaustive de la logique constructive.
Les profils longs sont tronqués explicitement. L'édition générale des profils,
des dessins manuels, des contraintes géométriques et de la topologie des connecteurs
reste à développer. Leurs paramètres associables sont déjà accessibles à la configuration.
Le descriptif historique construction.json n'est pas synchronisé après une édition.

## Validation

`Run.ps1` : contrats des nouvelles opérations et tests de non-régression existants.

`BuildNativeHarness.ps1 -RevitVersion 2024 -RunName <nom-unique> -FamilyEditOnly`,
puis `StartNativeHarness.ps1` avec les mêmes version/nom : essais dans une instance
Revit séparée et vide. Famille manuelle temporaire ; refus, add/replace/remove,
préservation des éléments d'origine, annulation après commit, limites d'extrusion,
associations de paramètres, régions et persistance des groupes après réouverture.
Le banc écrit result.json dans tmp/codex-native-validation et ferme son instance
après succès. Il ne modifie pas les documents utilisateur.

Validation du 19 septembre 2026 : tests hors Revit réussis, compilation Debug
(API 2023) et Release2024 réussie. Le banc natif Revit 2024
`2024-family-edit-v5/result.json` valide tous les scénarios ci-dessus, y compris
les régions remplies, le rollback de leurs imports et la persistance après réouverture.
Ces essais ciblés ne constituent pas une certification de toutes les familles,
des autres versions de Revit ni de toutes les orientations et géométries de dessin.

Extension de configuration : `2024-family-config-v2/result.json` valide également
la visibilité commune d'une extrusion, d'une forme libre et d'une famille imbriquée,
les valeurs initiales sur tous les types, la réutilisation sans doublon, les conflits
d'association, le rollback, la création et mise à jour de formules et deux valeurs
de visibilité indépendantes sur deux instances placées dans un projet temporaire.
Tests hors Revit et compilation Release2024 réussis. Ce test n'a pas modifié la table
réelle de l'utilisateur ; il exerce les mêmes opérations sur une famille temporaire.

## Suite du développement

1. Édition ciblée des profils et des courbes manuelles avec lecture des dépendances.
2. Dessins 2D qui suivent les paramètres et réglages de visibilité ciblés.
3. Édition géométrique des connecteurs et extension des réglages non associables.
4. Tests de variation sur les familles existantes, comparaison avant/après et aperçus.
5. Extension aux familles imbriquées, réseaux et géométries plus complexes.
