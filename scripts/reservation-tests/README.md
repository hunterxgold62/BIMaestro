# Autoréservation : compilation isolée et géométrie native

```powershell
& ./scripts/reservation-tests/Build.ps1
# Ou une seule version :
& ./scripts/reservation-tests/Build.ps1 -RevitVersion 2025
```

Le banc compile les sources de production de la commande V3, du pont Clash 3D,
de la configuration et de la fenêtre, y compris son XAML et le thème réel. Il
utilise les API réellement installées de chaque version demandée. Les sorties et
journaux sont placés dans `tmp/reservation-tests/<année>/`. La restauration utilise
uniquement les SDK et références déjà présents sur la machine.

Le passage courant cible Revit 2023 et 2025 par défaut. Revit 2027 nécessite un
SDK .NET 10, actuellement absent ; le SDK .NET 9 disponible ne peut pas compiler
cette cible. Le projet accepte toujours `-RevitVersion 2027` lorsqu'un SDK .NET 10
est installé ou présent dans `tmp/dotnet10`.

`CompileStubs.cs` remplace uniquement les services extérieurs à l'outil : entrée
licence/télémétrie, tutoriels, traduction, chargement global du thème et exécution
Dynamo. La géométrie, les filtres et la logique Autoréservation sont ceux de la
production. La compilation ne déploie rien, ne lance pas Revit et n'écrit pas les
préférences utilisateur.

`NativeReservationProbe.cs` est un `IExternalCommand` à exécuter dans un callback
API Revit valide, par exemple avec un chargeur de commandes de développement.
Son assemblage porte une identité distincte du plugin installé. Ce banc n'installe
aucun manifeste dans le profil utilisateur. Les tests natifs sont préparés et
compilés ; ils ne sont pas exécutés par `Build.ps1`.

Le probe crée un document temporaire, vérifie les calculs de production puis le
ferme sans enregistrer :

- intersection physique et centroïde d'objets génériques `DirectShape` ;
- refus de solides séparés par `TryGetElementHostIntersectionCenter`, y compris
  lorsque leurs boîtes englobantes se croisent ;
- centre de réservation cohérent avec les dimensions d'une section asymétrique ;
- section rectangulaire inclinée et cercle englobant ses coins ;
- transformations de rotation et translation des objets et hôtes génériques liés ;
- acceptation d'un objet physique et refus des types et niveaux.

Le complément `NativeHeightMappingProbe.cs` crée de vraies familles sans hôte et
un objet dans un autre document, transformé vers un mur local. Il vérifie les
paramètres personnalisés, la hauteur `COM_Hauteur` d'instance avec mapping vide,
et le refus explicite des hauteurs de type ou verrouillées avant de modifier les
autres dimensions. Il couvre également l'ordre objet/mur, le refus des faux hôtes
nommés « wall mounted fan console » et la conservation d'un décalage vertical de
100 mm. Il est lui aussi compilé, sans exécution native automatique.

Les vrais liens IFC/RVT, la sélection graphique et la découpe par une famille de
réservation nécessitent ensuite une validation dans Revit sur un projet de test.
Le refus testé concerne la méthode d'intersection physique ; le placement manuel
conserve un repli sur les boîtes pour les éléments sans solides exploitables et
les portes ou fenêtres dont le support est déjà évidé.
