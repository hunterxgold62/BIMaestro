# Maquette de référence Revit 2024

`BuildReference.ps1 -RevitVersion 2024 -RunName <nom>` compile le générateur
complet, les ressources et le contrôle natif. `StartReference.ps1` avec les mêmes
arguments lance une instance dédiée sans modifier les projets de l'utilisateur.

Le contrôle crée `BIMaestro_Apprentissage_2024.rvt` dans le dossier du passage,
vérifie le format, les vues, les familles et les intersections initiales de
Clash 3D, puis ferme Revit. `finished.txt` doit contenir `0` ; le détail est dans
`progress.txt` et `exited-cleanly.txt` confirme la fermeture.

Après validation, copier le RVT dans `Demo/BIMaestro_Apprentissage_2024.rvt`.
Conserver la précédente référence avant de la remplacer lors d'une évolution.

Compiler et lancer ensuite le contrôle avec `-RevitVersion 2025` et un nouveau
nom : il ouvre une copie de la même référence, contrôle ses scènes après
conversion et la ferme sans enregistrer. Le fichier de référence reste en 2024.

Les builds de distribution copient cette référence dans
`Demo/Maquette/BIMaestro_Apprentissage_2024.rvt` à côté de leurs DLL. Le bouton
Maquette d'essai en fait une copie personnelle, horodatée si nécessaire, avant
de l'ouvrir. Les deux installateurs refusent la préparation si le RVT manque.
