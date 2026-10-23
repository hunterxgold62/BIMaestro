# Modes Léger / Détaillé après la protection d’ouverture

Audit du code au 22 octobre 2026. Ce document décrit le comportement actuel et une
proposition d’architecture. Il ne contient aucun résultat du stress massif en cours.

## Protection initiale commune

Les deux modes utilisent le même inventaire de base et les recettes directes
prises en charge. À l’ouverture d’un projet enregistré, non modifié, dont le RVT
est accessible comme fichier, une copie immuable est préparée par un worker sans
ouvrir ni sauvegarder un second document Revit. Cette protection ne dépend pas
du choix Léger / Détaillé.

Les objets initiaux inchangés peuvent ainsi disposer d’une restauration native
même en Léger, y compris avant leur première capture individuelle. Une recette
directe peut également conserver cette copie comme secours. Ce secours intervient
si la recette directe échoue, pas systématiquement après une reconstruction réussie.

Sources : `BIMaestro/commands/qui est le coupable/ElementHistoryOpeningArchive.cs:42`,
`BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:1953`,
`BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs:440`.

## Léger fait moins de travail ; Détaillé ajoute des données

Léger suspend les enrichissements différés. Une recette directe de base conserve
la géométrie et ses paramètres de reconstruction, mais ne contient pas
systématiquement les connexions aux voisins, les circuits et les relations
géométriques. Une reconstruction géométrique réussie ne prouve donc pas que ces
relations seront rétablies, même si une copie d’ouverture existe en secours.

Détaillé autorise ces compléments ainsi que les archives natives nécessaires aux
objets complexes nouveaux ou modifiés. Il est plus complet sur ces points, pas
plus rapide. Les copies natives et certains appels géométriques restent sur le
thread Revit et sont non interruptibles. Après une modification, un aperçu peut
aussi être capturé immédiatement ; ce chemin n’impose pas toujours de timeout.

Sources : `BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:676`,
`BIMaestro/commands/qui est le coupable/ElementHistoryNetwork.cs:406`,
`BIMaestro/commands/qui est le coupable/ElementHistoryReconstruction.cs:185`,
`BIMaestro/commands/qui est le coupable/ElementHistoryRestoration.cs:651`,
`BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:1650`,
`BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:1989`.

## Limites de couverture et aperçus

La copie représente l’état enregistré à l’ouverture. Elle ne couvre pas les ajouts
ultérieurs ni les modifications non enregistrées. Les changements d’éléments,
types, familles ou matériaux peuvent rendre son recours inéligible. Un document
sans RVT accessible ne bénéficie pas de cette copie. Une sauvegarde pendant sa
préparation peut l’annuler. Détaillé ne garantit ni la fin des préparations avant
une suppression, ni la prise en charge de toute variante d’objet.

Le maillage d’aperçu est distinct d’une recette de restauration. Une archive
d’ouverture déjà prête évite volontairement une nouvelle capture détaillée ; un
objet peut être restaurable sans aperçu précis disponible. Le mode actif lors
de la suppression gouverne aussi l’inclusion du maillage dans le journal. Activer
Détaillé après suppression ne crée pas rétroactivement les données manquantes.

Sources : `BIMaestro/commands/qui est le coupable/ElementHistoryOpeningArchive.cs:149`,
`BIMaestro/commands/qui est le coupable/ElementHistoryOpeningArchive.cs:210`,
`BIMaestro/commands/qui est le coupable/ElementHistoryOpeningArchive.cs:233`,
`BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:706`,
`BIMaestro/commands/qui est le coupable/ElementHistoryTracker.cs:2766`,
`BIMaestro/commands/qui est le coupable/ElementHistoryWindow.xaml.cs:2889`.

## Proposition, non implémentée

Séparer une **protection automatique complète** des **aperçus précis**. La première
viserait les données nécessaires à la récupération : identités, paramètres,
géométrie, connexions et dépendances, avec un état explicite des préparations
restantes et des exclusions. La seconde piloterait seulement les aperçus coûteux.
« Complète » désignerait un objectif fonctionnel, jamais une garantie universelle.

Cette séparation demande une évolution du code : retirer simplement le choix
Détaillé ferait aujourd’hui perdre des compléments utiles. Les libellés devraient
également distinguer « recette disponible », « relations préparées » et « aperçu
disponible », sans assimiler la fin de l’inventaire à une restauration exhaustive.
