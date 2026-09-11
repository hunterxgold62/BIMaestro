# BIMaestro Navisworks — V1

Module .NET Framework 4.8 x64 indépendant, sans référence au projet Revit.
Binaires distincts pour Manage/Simulate **2025 et 2027**, compilés avec les API installées.
Freedom n'est pas pris en charge. Les autres années ne sont pas déclarées compatibles.

## Compilation et installation

Depuis la racine du dépôt :

```powershell
./Installer/Build-Navisworks.ps1
./BIMaestro.Navisworks/Tests/ReferencePath.Tests.ps1
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' '/OInstaller\Output' '/FBIMaestroInstaller-Navisworks-V1' 'BIMaestro\BIMaestro 22 à 27.iss'
```

Le projet figure dans la solution, mais sa compilation est volontairement décochée dans
les configurations globales : construire Revit ne nécessite pas d'installer le SDK Navisworks.
Le script dédié compile les deux versions requises par l'installeur et échoue si un SDK manque.
Pour compiler individuellement avec Simulate ou un autre emplacement, définir
`/p:NavisworksInstallDir=...` et `/p:NavisworksYear=...` avec MSBuild.

Les deux scripts Inno Setup existants incluent le module via `Installer/Navisworks.Files.iss`.
Leurs règles de copie, manifestes et code d'installation Revit restent identiques.
Les fichiers Navisworks installés sont suivis et supprimés par la désinstallation Inno Setup.
Les DLL Autodesk ne sont pas redistribuées.

Destination par utilisateur :

```text
%APPDATA%\Autodesk\ApplicationPlugins\BIMaestro.Navisworks.bundle\
  PackageContents.xml
  Contents\2025\BIMaestro.Navisworks.dll
  Contents\2025\fr-FR\BIMaestroRibbon.xaml
  Contents\2025\en-US\BIMaestroRibbon.xaml
  Contents\2027\BIMaestro.Navisworks.dll
  Contents\2027\fr-FR\BIMaestroRibbon.xaml
  Contents\2027\en-US\BIMaestroRibbon.xaml
```

Les installeurs générés se trouvent dans `Installer/Output`. Leur génération n'installe
pas le plugin sur le poste. Fermer Navisworks avant d'exécuter l'installeur.

## Test dans Navisworks

1. Utiliser une copie de travail d'un NWF enregistré contenant deux NWC, des Clash Tests
   (Manage), Search Sets, sélections, points de vue et apparences. Noter leur état initial.
2. Installer puis démarrer Navisworks 2025 ou 2027. Ouvrir le NWF et vérifier l'onglet
   **BIMaestro**, panneau **BIMaestro**, unique bouton **Modifier les liens**.
3. Vérifier que les chemins affichés correspondent aux fichiers ajoutés (NWC, pas RVT source).
4. Sélectionner une référence, choisir un nouveau fichier de la même maquette.
   Le remplacement et l'actualisation sont déclenchés immédiatement.
5. Si le succès est confirmé, contrôler le nouveau chemin et la géométrie, les autres liens,
   les tests/sets et apparences. Enregistrer avec Ctrl+S, fermer et rouvrir le NWF pour
   confirmer la persistance du chemin et des données.
6. Tester aussi annulation, même chemin, fichier déjà ajouté, chemin UNC, deux fichiers
   homonymes dans des dossiers différents, fichier cible illisible et document NWD/vide.
7. Tester une référence devenue introuvable **après son chargement** et un fichier initial
   toujours accessible : une absence de résolution doit produire un avertissement, jamais
   une confirmation de remplacement. Les sources doivent rester à leur emplacement.

## Méthode et limites connues

- L'inventaire utilise `Document.Models` et `Model.FileName`. `SourceFileName` désigne
  parfois le fichier d'export et ne convient pas. Une référence manquante ignorée à
  l'ouverture peut ne pas figurer dans cette collection ; l'inventaire n'est donc pas
  un inventaire garanti de toutes les entrées enregistrées dans le NWF. Les références
  imbriquées/embarquées et cloud ne sont pas prises en charge par cette V1.
- Le service installe temporairement `Application.FileResolving`, restreint le traitement
  au NWF actif et au chemin complet sélectionné, renseigne `ResolvedFileReference`,
  `FileNameToOpen` et `Handled`, puis appelle `Document.UpdateFiles()`.
  Le gestionnaire est retiré en `finally`, y compris en cas d'erreur.
- **Limite fonctionnelle : le remplacement n'est pas garanti pour une référence déjà
  accessible.** `UpdateFiles()` actualise les fichiers modifiés ; il ne garantit pas
  d'émettre `FileResolving`. Il n'existe pas dans l'API consultée de setter public du
  chemin chargé. Si le callback n'est pas appelé, si le parent n'est pas identifié ou
  si le nouveau chemin n'est pas constaté, le module signale le remplacement non validé.
  Il ne renomme/déplace pas les fichiers partagés pour provoquer artificiellement l'événement.
  Cette V1 nécessite donc une recette dans Navisworks avant diffusion et ne constitue
  pas une solution universelle de repointage.
- Aucune suppression/réinsertion de modèle, aucune recréation/réouverture du NWF,
  aucune sauvegarde implicite. L'actualisation s'applique à tout le document et peut
  actualiser d'autres fichiers modifiés. L'API ne fournit pas ici de rollback atomique :
  après une erreur, inspecter le document avant de l'enregistrer.
- Les données du NWF ne sont pas modifiées directement. La conservation des liens aux
  objets dépend des identifiants et de la structure du nouveau modèle ; les apparences,
  sélections explicites et résultats de clash ne peuvent être garantis à l'identique.
  Les Clash Tests ne sont pas relancés automatiquement.
- Aucune détection automatique de versions de maquettes.

## Vérification effectuée

Compilation réelle contre les API Autodesk installées 2025 et 2027, tests de normalisation
des chemins et compilation des installateurs. Pas de validation interactive effectuée
dans Navisworks : affichage du ruban, résolution effective, sauvegarde/réouverture et
conservation des données restent à vérifier avec la procédure ci-dessus.

Sources : documentation XML officielle installée à côté de `Autodesk.Navisworks.Api.dll`
(`Document.UpdateFiles`, `Model.FileName`, `FileResolvingEventArgs`),
[déploiement Autodesk](https://aps.autodesk.com/marketplace/publisher-center/navisworks-publisher-guidelines),
[ruban Autodesk](https://blog.autodesk.io/custom-ribbon-of-navisworks-part-2/comment-page-1/).

## Fichiers

Créés : ce README, `BIMaestro.Navisworks.csproj`, `Commands/ModifyLinksCommand.cs`,
`UI/ModifyLinksWindow.cs`, `UI/BIMaestroRibbon.xaml`, `Services/LinkService.cs`,
`Services/ReferencePath.cs`, `Tests/ReferencePath.Tests.ps1`,
`Installer/Build-Navisworks.ps1`, `Installer/Navisworks.Files.iss`,
`Installer/Navisworks.PackageContents.xml`, `Installer/.gitignore`.

Modifiés : `BIMaestro.sln`, `BIMaestro/BIMaestro 22 à 27.iss`,
`BIMaestro/BIMaestro 22 à 27 - Bundle.iss`. Aucun fichier source ou projet Revit modifié.
