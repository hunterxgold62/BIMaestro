# Évolution du viewer MEP

Objectif : rendre la navigation utilisable par un chef de projet et permettre un aller-retour des réservations avec Revit.

1. Sélection visible de l'élément complet ; cadrage ; orbite et zoom adaptés à la distance.
2. Conservation des notes et réservations entre révisions avec identifiants persistants, repère d'export et signalement des rattachements incertains.
3. Déplacement et redimensionnement des réservations par poignées avec aperçu, annulation et enregistrement explicite avant découpe.
4. Export des volumes en IFC et fichier d'échange Revit ; import dans Revit en volumes identifiés, mis à jour sans doublons.
5. Vérifier les transformations de coordonnées, la conservation entre révisions, les exports et les compilations ; publier le viewer et livrer la DLL.

Les anciennes publications sans repère nécessitent une nouvelle exportation pour un retour Revit fiable. Les annotations anciennes restent accessibles et peuvent être repositionnées. L'IFC de réservation représente des volumes à coordonner ; il ne modifie pas automatiquement les murs d'une maquette tierce.

## Livraison du 11 septembre 2026

Étapes 1 à 5 réalisées. Viewer publié en version 32 ; fonction de sauvegarde mise à jour ; DLL Release compilée. Les tests et les points restant à valider dans une maquette réelle figurent dans MEP-guide-tests.md.
