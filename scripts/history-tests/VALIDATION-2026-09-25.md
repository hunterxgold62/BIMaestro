# Validation de « Qui a fait ça » — 25 septembre 2026

Code testé : HEAD `65f1ba3`, fichiers de restauration sans modification locale.
Aucune correction du code applicatif effectuée pendant cette validation.

## Résultats exécutés aujourd'hui

| Contrôle | Résultat | Preuve locale depuis la racine du dépôt |
| --- | --- | --- |
| Banc natif Revit 2024 | 25 scénarios réussis, 0 échec | `tmp/codex-native-validation/2024-history-audit-20260925/result.json` |
| Banc natif Revit 2025 | 25 scénarios réussis, 0 échec | `tmp/codex-native-validation/2025-history-audit-20260925/result.json` |
| Compilation complète Debug Revit 2025 | Code de sortie 0, avec avertissements | `tmp/codex-native-validation/2025-history-audit-20260925/full-build-console.log` |

Il s'agit des mêmes 25 scénarios exécutés sur deux versions, soit 50 exécutions.
Les processus Revit distincts ont créé leurs propres maquettes de test. Les
inscriptions temporaires des bancs ont été retirées par le lanceur, puis leur
absence a été vérifiée. Aucune maquette utilisateur n'a été ouverte par ces bancs.

## Couverture

- Murs droits/courbes, sol troué avec décalage, familles tournées, hébergées et en miroir.
- Capture JSON, suppression puis reconstruction, dimensions, volumes et aperçus après annulation.
- Restauration native persistante après sauvegarde/réouverture, prévention des doublons,
  nouvelle restauration après suppression et annulation des transactions.
- Tuyaux, gaines, conduits électriques et chemins de câbles ; pentes, verticales et sections.
- Flexibles de tuyauterie et de gaine, points, tangentes et diamètres.
- Connexions physiques à des voisins existants ou restaurés en plusieurs lots,
  persistance après réouverture et accessoire tourné connecté à deux tuyaux.
- Protection d'un accessoire déplacé, refus d'inclinaison avec annulation,
  types/hôtes/niveaux absents, données invalides et lots partiellement restaurables.
- Exclusion du calorifuge sans masquer les erreurs des autres éléments.

## Limites confirmées par les rapports réels existants

Deux rapports du 22 septembre, lus dans `%LOCALAPPDATA%/BIMaestro/HistoryReports`,
contiennent encore des échecs hors calorifuge :

| Rapport | Objets physiques créés | Échecs | Connexions rétablies |
| --- | ---: | --- | ---: |
| `restauration-20260922-150848-9f1c8a31.json` | 6 canalisations | 6 `PipingSystem` non pris en charge | 0 |
| `restauration-20260922-150937-d63f405f.json` | 6 conduits et 3 raccords | 3 `ConduitRun` non pris en charge | 6 |

Ces deux rapports n'indiquent aucune erreur de connexion. Ce sont des preuves
historiques, pas des scénarios rejoués aujourd'hui. Les systèmes logiques et les
longueurs de conduits ne sont pas restaurés comme des objets indépendants.

Les familles sur face, adaptatives, en place ou sur deux niveaux, les pièces de
fabrication et certaines géométries particulières restent hors de la couverture
de restauration décrite dans le README. Les types/familles et les références
nécessaires doivent exister, et une capture exploitable doit précéder la suppression.

Les familles CML réelles (coudes, réductions et piquages), l'enregistrement complet
de l'historique, les filtres de la fenêtre et le survol n'ont pas été rejoués dans
cette session. Le banc compile directement les trois composants de reconstruction,
restauration et réseau ; il ne teste pas tout le parcours utilisateur du complément.

## Conclusion

La restauration passe les 25 scénarios du banc sur Revit 2024 et 2025. Cela ne
constitue pas une garantie que tout fonctionne hors calorifuge : des exclusions
supplémentaires sont confirmées et certains parcours restent à tester.
