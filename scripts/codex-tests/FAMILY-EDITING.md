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

Exemples de demandes : « Ajoute un arc symbolique d'ouverture en plan à cette
fenêtre, dans un groupe nommé Ouverture » ; « Modifie le groupe Ouverture » ;
« Passe la profondeur de cette extrusion non pilotée à 120 mm ».

Les coordonnées doivent provenir de l'inspection et de la demande de l'utilisateur.
Le document reste ouvert, sans sauvegarde automatique et sans nouvelle famille.
Un groupe de transactions regroupe l'édition et les éventuels imports de régions :
échec = annulation du lot, succès = une entrée d'annulation Revit.
La suppression est annulée si Revit supprime des dépendances hors du groupe ciblé.
Les outils n'enlèvent pas les verrouillages ni les associations de paramètres.

## Limites actuelles

Les dessins libres ont des dimensions fixes. Leur visibilité ne masque pas
automatiquement les formes existantes (`hide_model_in=[]`). Les régions utilisent
des familles de détail imbriquées ; leurs définitions inutilisées ne sont pas purgées.
Les plans de travail auxiliaires sont conservés pour éviter de supprimer des dépendances Revit.
L'inventaire n'est pas une extraction exhaustive de la logique constructive.
Les profils longs sont tronqués explicitement. L'édition générale des profils,
des dessins manuels, des contraintes, des formules et des connecteurs reste à développer.
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

## Suite du développement

1. Édition ciblée des profils et des courbes manuelles avec lecture des dépendances.
2. Dessins 2D qui suivent les paramètres et réglages de visibilité ciblés.
3. Matériaux, paramètres/formules et connecteurs éditables avec contrôles adaptés.
4. Tests de variation sur les familles existantes, comparaison avant/après et aperçus.
5. Extension aux familles imbriquées, réseaux et géométries plus complexes.
