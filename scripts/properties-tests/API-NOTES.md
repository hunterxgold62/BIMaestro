# Palette Propriétés native : périmètre API

Recherche du 19 octobre 2026 sur Revit 2023, 2024, 2025 et 2026. Les conclusions documentaires ci-dessous ne constituent pas une validation des modifications visuelles à l’exécution.

## Séparation entre apparence et données

La phase UI doit conserver la palette Propriétés native et ne modifier aucun modèle, aucune valeur de paramètre, aucun groupe enregistré dans le document et aucun ordre de paramètres de famille. Les essais visuels doivent être réversibles et restaurer les contrôles natifs. Les API métier mentionnées plus bas sont des possibilités distinctes, pas des actions autorisées automatiquement pour cette phase UI.

## Résultats documentaires

| Possibilité | État et limites |
| --- | --- |
| Couleurs des lignes, des groupes ou des textes | Aucun point d’extension public dédié trouvé dans les API installées. Inspecter les contrôles internes à l’exécution avant de conclure. L’absence d’API publique ne prouve pas une impossibilité technique. |
| Recherche et favoris dans la palette native | Aucun contrat public dédié trouvé. Faisabilité et stabilité à établir par inspection des contrôles natifs. |
| Tri A–Z ou Z–A | Réglage natif `PropertiesPaletteParameterSorting` dans la section `[UserInterface]` de Revit.ini : 0 = défaut, 1 = croissant, 2 = décroissant. Documentation 2024 vérifiée. |
| Ordre personnalisé des paramètres de famille | `FamilyManager.ReorderParameters` réordonne les paramètres dans leur groupe, sans changer l’ordre des groupes. Cette opération modifie la famille : hors phase UI sans modification du modèle. |
| Groupe d’un paramètre personnalisé | `InternalDefinition.SetGroupTypeId` ne fonctionne que pour les définitions de paramètres non intégrés. Cette opération modifie les données du document. |
| Masquage des paramètres vides | `ExternalDefinitionCreationOptions.HideWhenNoValue` concerne les définitions partagées appropriées. Ce n’est pas un filtre visuel universel pour les paramètres natifs. |
| Taille, ancrage et zoom | Fonctions natives de la palette. La documentation utilisateur indique notamment Ctrl+molette pour zoomer et la persistance de la disposition. |
| Afficher ou masquer | `PostableCommand.TogglePropertiesPalette` est disponible. `DockablePane.Hide()` n’a aucun effet sur les palettes intégrées. |

Les XML locaux de `RevitAPI.xml` et `RevitAPIUI.xml` confirment, pour chacune des versions 2023 à 2026, la présence de `FamilyManager.ReorderParameters`, `InternalDefinition.SetGroupTypeId`, `DockablePanes.BuiltInDockablePanes.PropertiesPalette` et `PostableCommand.TogglePropertiesPalette`.

Aucune preuve officielle de migration du moteur de la palette entre 2023 et 2026 n’a été trouvée. Le changement de moteur de la page Home ne permet pas de déduire celui de la palette Propriétés.

## Validation à l’exécution : à compléter

- Identifier les fenêtres et contrôles natifs de la palette dans chaque version réellement testée.
- Vérifier les styles accessibles sans changement des données et leur restauration.
- Vérifier sélection simple, multiple, vue active sans sélection, éléments MEP et champs en lecture seule.
- Vérifier les changements de sélection, les mises à jour natives, les menus, boutons et éditeurs de valeur.
- Vérifier les thèmes disponibles, le DPI et les performances avant de qualifier une piste de fonctionnelle.
- Conserver les captures et résultats mesurés séparément de ces conclusions documentaires.

## Sources Autodesk

- [Palette Propriétés : fonctionnement natif](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-GetStarted/files/GUID-A764EA7A-FE26-469B-857C-F3A70812FC34.htm)
- [UserInterface dans Revit.ini, documentation 2024](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-Customize/files/GUID-30C62836-F3F1-4B78-B1AF-DB4E3FD79FDF.htm)
- [FamilyManager.ReorderParameters : limites du réordonnancement](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f3e5375b-28d7-d6c6-ea49-bf6f6289fd9a.htm)
- [InternalDefinition.SetGroupTypeId : paramètres non intégrés](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/62a8a155-a7a6-e019-8cd8-9a7c9b4cd80a.htm)
- [ExternalDefinitionCreationOptions.HideWhenNoValue](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/2a3a90d6-cf4b-ae88-b587-612dc96c0bc2.htm)
- [Dockable Dialog Panes : restrictions de Hide](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Advanced_Topics/Revit_API_Revit_API_Developers_Guide_Advanced_Topics_Dockable_Dialog_Panes_html.html)
- [PostableCommand : TogglePropertiesPalette](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f6ccdc1b-6ac3-9c49-d0bb-8a7d1877eab0.htm)
## Actualisation : réordonnancement natif
Le 20 octobre 2026, un accès vérifié à la véritable instance C++ a permis le déplacement natif du bloc Étendues avant Niveau en fond de plan en Revit 2025. Cette preuve dépasse la limite des premières séries, sans constituer une API publique ni une qualification de tous les éditeurs. [Résultats et garde-fous](REORDER-RESULTS.md).
