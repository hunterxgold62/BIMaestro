# Base commune des tutoriels

La maquette actuelle constitue la base des exercices livrés avec BIMaestro.
Chaque utilisateur la crée avec **Maquette d’essai**, puis choisit un parcours.
Les dix parcours sont proposés : Auto réservation, Qui a fait ça ?, Couleurs
et vues, Calcul des canalisations, Organisateur, Gabarit de vue, Gestion Excel,
Navigateur de familles, MEP Booster et Clash 3D.

## Distribution et copie personnelle

La référence est `Demo/BIMaestro_Apprentissage_2024.rvt`, enregistrée dans
Revit 2024 avec les exercices intacts. Les versions suivantes de Revit ouvrent
une copie et la convertissent. Revit 2023 ne peut pas ouvrir cette référence.
Le tutoriel Couleurs et vues reste proposé sans grisé ni restriction de version.
Les huit familles de `Familles` et le catalogue
`Demo/NavigateurFamilles` sont copiés à côté de chaque DLL lors de la compilation.
Les deux installateurs les distribuent avec les binaires correspondants et
refusent une préparation sans le RVT, les huit familles et les témoins du catalogue,
y compris pour Revit 2025.

Le bouton copie la référence puis l'ouvre avec la version de Revit utilisée,
dans `Documents/RevitLogs/BIMaestro_Apprentissage_<version>.rvt`.
Si ce fichier existe, une nouvelle copie horodatée est créée. L'utilisateur
peut donc modifier ses exercices sans remplacer la base ni ses anciens fichiers.
L'utilisateur n'a pas besoin d'un gabarit Autodesk MEP pour ouvrir cette copie.

## Améliorations futures

Modifier le générateur, les classes `Demo*Exercise` et les familles de formation
dans le dépôt, puis régénérer la référence dans Revit 2024 avec
`DemoProjectBuilder.BuildReference`, vérifier ses exercices et reconstruire
les binaires et les installateurs. Les nouvelles copies utiliseront cette base
améliorée. Les fichiers RVT déjà créés restent
des copies personnelles ; ils ne sont pas remplacés automatiquement.

Un fichier RVT créé pendant un exercice peut contenir des corrections ou des
objets supprimés. Il ne doit pas servir de base de distribution par simple copie.
Le générateur et ses ressources restent dans le dépôt pour les prochaines
améliorations ; le fichier RVT validé sert de référence distribuée.

Ces modifications préparent la distribution. Elles ne publient pas un nouvel
installateur sur le site et ne mettent pas à jour une installation existante.
