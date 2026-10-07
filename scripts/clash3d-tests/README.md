# Clash 3D : fonctionnement et validation

Clash 3D propose un parcours court : choisir le périmètre, analyser, puis voir les résultats en 3D. Les réglages sont repliés par défaut. La fenêtre de détail rassemble l'explication, les deux objets, l'aperçu et la décision enregistrée avec son commentaire.

## Périmètre de la refonte

- Périmètres : maquette entière, vue active ou sélection. La sélection limite les objets contrôlés ; les obstacles environnants restent recherchés.
- Objets contrôlés : canalisations, gaines, chemins de câbles, conduits, raccords et accessoires ; équipements et modèles génériques en option.
- Obstacles : réseaux, équipements, modèles génériques et principales catégories du bâtiment. Les liens Revit chargés et les imports peuvent être choisis dans les réglages.
- Détection : présélection spatiale, puis intersection des solides physiques. Les pièces MEP directement raccordées sont exclues. Le calorifuge existant est pris en compte lorsque l'option est activée.
- Une intersection confirmée est distinguée d'une suspicion. Une géométrie composée seulement de maillages ou une opération géométrique indécidable ne devient pas artificiellement une collision confirmée. Les géométries non exploitables sont signalées dans le bilan.
- L'analyse progresse par tranches dans des `ExternalEvent` Revit. Elle peut être annulée ; un résultat partiel est identifié. Une modification de la maquette rend les résultats périmés et annule une analyse en cours.
- La vue 3D montre l'objet contrôlé en orange et l'obstacle en bleu. Le retour à la vue précédente rétablit la sélection, la boîte de coupe et les remplacements graphiques conservés.
- Les décisions et préférences sont persistées. Une décision « Traité » repasse à « À revoir » lorsque l'empreinte géométrique du conflit change. « Traité » reste une décision humaine, pas une validation automatique.
- Les exports suivent exactement les filtres affichés. Le HTML est autonome, avec les aperçus intégrés ; le CSV conserve les commentaires multilignes et neutralise les valeurs interprétables comme des formules.

Les contrôles complémentaires de connecteurs ouverts et de proximité des parois restent optionnels. Une intersection réseau/paroi ne prouve pas à elle seule qu'une réservation est incorrecte. Les imports IFC sont lus à travers leur représentation Revit : une géométrie maillée peut donc produire une suspicion. Le volume affiché est un volume témoin d'intersection entre solides, pas la somme de tous les volumes superposés. Une opération géométrique individuelle particulièrement coûteuse peut dépasser la durée cible d'une tranche.

## Affichage progressif

Les résultats sont publiés à chaque retour de tranche API, dès leur détection. Les nouveaux lots sont ajoutés à une collection observable sans réinitialiser la liste, la sélection ou le défilement. La recherche, les filtres, le détail autonome et les décisions restent accessibles pendant l'analyse. Le bilan reste explicitement provisoire ; annuler conserve les résultats déjà trouvés comme bilan partiel.

Les aperçus des nouveaux résultats sont préparés dans un callback suivant leur publication. Une ligne peut donc apparaître avec « Aperçu en préparation… », puis recevoir sa vignette sans être remplacée ; une fenêtre de détail déjà ouverte reçoit également sa géométrie. Le travail visuel utilise au plus 20 ms de budget cible avant de reprendre la recherche, avec une file bornée à 32 résultats. Un appel géométrique individuel reste indivisible. Les décisions sont restaurées depuis un instantané par analyse, et le bilan est compté au fil des ajouts pour éviter de reparcourir tous les résultats à chaque tranche.

La collecte des sources et la préparation de l'index des obstacles restent nécessaires avant les premières détections. Leur étape, les objets, les obstacles et la durée sont visibles pendant cette phase. Le cadrage et les captures dans Revit, la vue d'ensemble et l'export sont disponibles une fois l'analyse terminée ou annulée. Une décision prise pendant l'analyse n'est pas écrasée lors du bilan final. Les réglages ouverts sont bornés à 140 pixels pour conserver les commandes à la taille minimale.

Les passages `2023-progressive-verified` et `2024-progressive-final` valident chacun 42 scénarios, sans échec, avec sortie propre et sans exception d'interface. Cinq scénarios supplémentaires vérifient le bilan provisoire vide, la publication avant triangulation, la mise à jour du détail, les lots successifs avec recherche et décisions, l'annulation après publication et la limite de 32 résultats avant de rendre la main. La fixture contient 80 objets et 40 collisions distinctes. Les compilations complètes pour Revit 2023 et 2024 et isolée pour Revit 2025 passent. Les images `main-progressive-first.png` et `main-progressive-complete.png` documentent l'arrivée progressive. Ces fixtures ne mesurent pas la durée d'une grande maquette utilisateur.

## Contexte Revit et réservation directe

Après « Voir en 3D », une barre apparaît au-dessus des résultats : « Voir autour » désactive la boîte de coupe et élargit le cadrage de 8 mètres autour du conflit. Les deux objets restent colorés et les autres objets retrouvent leurs graphismes. « Isoler le conflit » rétablit le cadrage local. La même bascule se trouve dans l'inspection du conflit affiché. Le retour à la vue d'origine ou la vue d'ensemble masque cette barre.

Le détail et la barre proposent « Créer la réservation » pour une intersection confirmée d'un objet analysé avec un mur ou sol Revit : canalisations, gaines, équipements, bouches d'aération, familles et modèles génériques. Les bouches d'aération (`OST_DuctTerminal`) font partie de la catégorie « Équipements ». La création passe par un `ExternalEvent`, puis par le placement et le dimensionnement de la commande Autoréservation V3. Elle utilise la forme mémorisée, le profil correspondant au mur ou sol, la famille personnelle ou BIMaestro choisie, les paramètres et l'arrondi configurés. Les familles de réservation sur face ne sont pas prises en charge par ce raccourci.

Le service vérifie la famille et le type exact, l'intersection actuelle, les paramètres de longueur distincts et dimensionnables. Une profondeur fixée au niveau du type ou calculée automatiquement peut être conservée si elle couvre l'épaisseur de la paroi ; le service ne modifie jamais ce type. Une réservation circulaire autour d'un objet générique couvre les points projetés de l'intersection physique. Un mapping invalide annule entièrement la transaction. Un identifiant de traversée conservé sur l'instance empêche de recréer la même réservation tant qu'elle existe. La création reste annulable avec Annuler dans Revit. Elle rend l'analyse périmée et ne marque jamais automatiquement le conflit « Traité » : relancer l'analyse pour vérifier le résultat, notamment la coupe réelle fournie par la famille.

Pour un mur ou sol lié, une famille sans hôte est nécessaire et la réservation est créée uniquement dans la maquette active, avec la transformation du lien. La coupe dans la maquette liée reste à coordonner. Les parois IFC maillées ne sont pas prises en charge comme hôte de cette action de création ; il faut un mur ou sol Revit et une intersection solide confirmée.

Le passage `2025-context-reservation-v5` valide 55 scénarios sans échec, avec sortie propre. Il couvre les réservations depuis une canalisation, une gaine, un objet générique créé et une famille de bouche d'aération, sur mur et sol, ainsi qu'un mur lié tourné. Il vérifie les paramètres personnels, le refus d'un doublon, le rollback complet d'un mauvais mapping, l'absence de création sans intersection et le diamètre couvrant une bouche rectangulaire. La vraie famille `CML_Réservation rectangulaire murale.rfa` est également testée : sa profondeur est un paramètre de type, conservé lorsque son épaisseur convient. Les rendus WPF aux tailles minimale et normale et la bascule de coupe via de vrais `ExternalEvent` passent. La compilation complète .NET Framework / Revit 2023 passe également ; les nouveaux scénarios natifs ont été exécutés dans Revit 2025.

## Aperçus autonomes

Les résultats disposent maintenant d'une vignette vectorielle et d'un visualiseur 3D dans la fenêtre de détail. Le visualiseur permet de tourner par glissement, de zoomer avec la molette, de masquer chaque objet et de rétablir le cadrage initial. Les objets contrôlés sont orange, les obstacles bleus. Les surfaces proviennent de la géométrie réellement analysée, y compris les transformations des liens et le calorifuge lorsque celui-ci est inclus.

La triangulation est effectuée dans une tranche API, uniquement pour les objets donnant un résultat. Une copie composée de nombres et de triangles est partagée entre les conflits d'un même objet au cours de l'analyse. Ces copies ne contiennent aucun objet Revit. L'affichage et les exports vectoriels ne changent aucune vue et n'appellent pas `ExportImage`. La vignette est construite au premier affichage puis conservée en mémoire ; la préparation de cette image figée est également testée sur un thread de travail. La vue de détail utilise le rendu 3D WPF avec gestion de la profondeur.

Le cadrage est local : les surfaces sont coupées aux limites de la zone. Le repère désigne le centre de la zone détectée, pas le volume exact de pénétration. Les vignettes utilisent une projection vectorielle indicative ; l'inspection 3D et « Voir en 3D » permettent d'examiner les masquages entre objets. Les données sont renouvelées à la prochaine analyse, et le détail affiche un avertissement si la maquette a changé.

Les budgets sont explicites : 6 000 triangles par objet, 100 000 triangles copiés par analyse, 3 000 triangles locaux par objet et par conflit, et 60 000 triangles locaux cumulés pour les dessins. Une géométrie trop lourde ou illisible est signalée ; aucune boîte englobante inventée ne remplace sa forme. Ces limites bornent le travail ajouté, sans garantir une durée maximale pour un appel individuel de triangulation Revit.

Le HTML inclut les dessins SVG sans demander une capture Revit. Une capture manuelle existante peut toujours être intégrée à sa place. Le bouton de capture est désormais dans la section repliée « Capture Revit (facultative) ».

## Fermeture et régression

Les premiers passages des tests des aperçus ont révélé une exception après leur exécution : le désabonnement aux événements Revit était effectué depuis le dispatcher WPF. Le désabonnement doit se faire dans un contexte API valide. La fermeture de Clash 3D passe maintenant par l'action `CloseSession`, qui restaure les graphiques et libère l'abonnement dans un `ExternalEvent`, avant de laisser WPF fermer la fenêtre.

Le banc utilise aussi des événements externes explicites pour ses étapes différées. Il teste la fermeture réelle d'une fenêtre modeless depuis le dispatcher, puis le nettoyage natif et la sortie de Revit. Une exception d'interface inattendue est consignée comme un échec dans le banc ; ce dispositif de diagnostic ne fait pas partie du plugin livré. `finished.txt` seul ne prouve pas une sortie propre : vérifier également `exited-cleanly.txt` et l'arrêt du processus dédié.

## Résultats de validation

| Validation | Résultat | Dossier de résultats |
| --- | --- | --- |
| Revit 2023, tests dans Revit | 42 réussis, 0 échec, sortie propre | `tmp/codex-native-validation/2023-progressive-verified` |
| Revit 2024, tests dans Revit | 42 réussis, 0 échec, sortie propre | `tmp/codex-native-validation/2024-progressive-final` |
| BIMaestro complet, Debug / Revit 2023 | Compilation réussie | `BIMaestro/bin/ClashProgressive2023` |
| BIMaestro complet, Release2024 | Compilation réussie | `BIMaestro/bin/ClashProgressive2024` |
| Module Clash 3D, Revit 2025 / .NET 8 | Compilation réussie | `tmp/codex-native-validation/2025-progressive-final` |
| Revit 2025, clic WPF et tests dans Revit | 45 réussis, 0 échec, sortie propre | `tmp/codex-native-validation/2025-compatibility-real-final` |
| Revit 2025, contexte et réservations pour objets analysés | 55 réussis, 0 échec, sortie propre | `tmp/codex-native-validation/2025-context-reservation-v5` |
| BIMaestro complet avec actions de réservation, Revit 2025 | Compilation réussie, installation locale mise à jour | `BIMaestro/bin/ClashActions2025` |
| BIMaestro complet, Revit 2025 / .NET 8 | Compilation réussie, installation locale 2025 mise à jour | `BIMaestro/bin/ClashFix2025` |
| Renderer autonome, sans référence Revit | Cadrage adaptatif, pixels, SVG, cache et interaction réussis | `tmp/clash-visual-probe` |

Le projet BIMaestro complet pour Revit 2025 compile dans `BIMaestro/bin/ClashFix2025` contre les API Revit 25 et .NET 8. Les assemblages `ClashProgressive2023` et `ClashProgressive2024` sont des sorties de compilation ; leur création ne remplace pas automatiquement une installation utilisateur.

## Correction de l'installation Revit 2025

Le journal Revit 2025 du test utilisateur révélait le chargement de la DLL commune compilée contre Revit 2023 et .NET Framework, avec des conflits de versions des API et de WPF. Les installateurs utilisent désormais un dossier séparé pour la DLL .NET 8 de Revit 2025 ; leur compilation exige ce binaire. La commande Clash refuse explicitement une DLL dont la version des API ne correspond pas à Revit 2025 ou supérieur.

Le clic sur « Analyser » demande systématiquement une actualisation du périmètre dans un `ExternalEvent`, puis démarre l'analyse dans son callback API. Une sélection modifiée après l'ouverture de la fenêtre est ainsi prise en compte immédiatement.

Le passage `2025-compatibility-real-final` valide 45 scénarios sans échec et se ferme proprement. Il vérifie notamment un clic WPF hors contexte API après une sélection de 563 objets, la publication avant la fin et la fermeture via les événements externes. Sur une copie de `Testi crousty.rvt`, le contrôle de 563 sources face à 1 217 obstacles examine 311 paires et produit 31 résultats en 11,47 secondes, avec résultats disponibles avant la fin. Deux diagnostics géométriques restent signalés dans le bilan. Un premier passage avait mesuré 4,62 secondes pour les mêmes comptes ; ce temps varie avec la charge et n'inclut pas l'ouverture du fichier. Le contrôle porte sur cette sélection et ne constitue pas une validation exhaustive de toute la maquette.

L'installateur per-user a été compilé avec `Revit2025Bin=bin\ClashFix2025` dans `tmp/clash-installer-check/BIMaestroInstaller-Clash2025.exe`. La compilation de contrôle du bundle est bloquée par l'absence du binaire Navisworks 2025 exigé par `Installer/Navisworks.Files.iss` ; elle ne valide donc pas encore l'ensemble de ce paquet. L'installation locale corrigée n'utilise pas ce bundle.

`InstallRevit2025Fix.ps1` contrôle les références API 25 / .NET 8 sans charger l'assembly, copie le plugin dans un dossier distinct identifié par le hash de la DLL, puis remplace atomiquement le seul manifeste 2025 en sauvegardant sa version précédente. Le redémarrage de Revit est nécessaire. Le script ne ferme pas les instances Revit.

```powershell
dotnet build ./BIMaestro/BIMaestro.Revit2025.csproj --no-restore /p:Configuration=Release /p:BaseIntermediateOutputPath=obj\Revit2025\ /p:IntermediateOutputPath=obj\ClashActions2025\ /p:OutputPath=bin\ClashActions2025\
& ./scripts/clash3d-tests/InstallRevit2025Fix.ps1 -Preview
& ./scripts/clash3d-tests/InstallRevit2025Fix.ps1
```

Les 42 scénarios couvrent les transformations de boîtes et de liens, les faux positifs de réseaux diagonaux, les contacts sans volume, le seuil volumique, les maillages, les familles tournées avec plusieurs solides, les identités distinctes dans un lien, la déduplication, les périmètres, l'index spatial, les quatre catégories de réseaux réels, une collision uniquement avec le calorifuge, les raccordements MEP, l'annulation, les modifications du document, la persistance, les identifiants 64 bits, les exports et les aperçus. Ils vérifient aussi les rendus WPF aux tailles normale et minimale ainsi que le cadrage et la restauration par un véritable `ExternalEvent`. Les contrôles supplémentaires portent sur les formes courbes, les triangles traversant le cadre, les budgets, les maillages importés, le cache d'image figé préparé hors du thread API, le visualiseur autonome, le SVG sans capture et la fermeture réelle de la fenêtre.

Le dernier ajustement du cadrage des vignettes est vérifié séparément par `VisualProbe.csproj`, qui compile le fichier de production `SmartClashVisual.cs` sans aucune référence aux DLL Revit. Sur son cas de test, les objets couvrent 6 538 pixels et une étendue de 111 × 89 dans une image de 160 × 110. Ce test vérifie aussi le cadre du SVG, les couleurs, le cache figé et les commandes de caméra. Il ne démarre pas Revit.

`result.json` contient chaque résultat et sa durée. `finished.txt` confirme la fin de l'exécution. Les PNG `main-*.png` et `inspection-*.png` permettent de contrôler les rendus des fenêtres.

## Rejouer le banc

Prérequis : Revit 2023, 2024 ou 2025 installé et la dépendance `BIMaestro/bin/Release/Newtonsoft.Json.dll`. Les versions 2023/2024 demandent MSBuild Visual Studio 2022 Community et .NET Framework 4.8 ; 2025 demande le SDK .NET 8 et ses packs WPF. Les fixtures utilisent les gabarits de familles et de plomberie installés.

Depuis la racine du dépôt, utiliser un nom de passage inédit :

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' ./BIMaestro/BIMaestro.csproj /t:Build /p:Configuration=Release2024 /p:AssemblyName=BIMaestro.ClashActionValidation /p:IntermediateOutputPath=obj\ClashActionValidation2024\ /p:OutputPath=bin\ClashActionValidation2024\ /m
& ./scripts/clash3d-tests/Build.ps1 -RevitVersion 2024 -RunName clash-check-01
& ./scripts/codex-tests/StartNativeHarness.ps1 -RevitVersion 2024 -RunName clash-check-01
```

Le lanceur enregistre temporairement le banc puis ouvre une instance Revit dédiée. Il retire le manifeste dès que le banc est chargé ; l'exécution continue dans cette instance. Attendre `finished.txt`, puis lire `result.json`. Le banc refuse de démarrer si cette instance contient déjà des documents ; il crée, ferme et enregistre uniquement ses fixtures dans son dossier de sortie. Sa persistance est redirigée vers ce dossier pour préserver les décisions utilisateur.

Le banc compile les véritables fichiers du moteur, de l'interface, des événements externes, de la persistance et des rapports contre les DLL Revit installées. Les stubs isolent la commande d'entrée, la traduction et le gestionnaire de thème ; la commande d'entrée est vérifiée par la compilation complète du plugin. Le thème WPF réel est inclus. Le banc ne vérifie pas les mécanismes de licence ni la télémétrie.

Le service de réservation vient d'une compilation complète du produit avec le nom d'assembly `BIMaestro.ClashActionValidation`, afin de tester ses vrais helpers sans remplacer l'installation Revit pendant les essais. `Build.ps1` recherche cet assembly dans `BIMaestro/bin/ClashActionValidation<année>` ; `-ReservationAssembly` permet un chemin explicite. Les avertissements de types masqués dans le banc concernent les stubs et les fichiers Clash compilés localement, présents aussi dans cette référence produit.

Pour compiler et exécuter directement le banc 2025 :

```powershell
dotnet build ./BIMaestro/BIMaestro.Revit2025.csproj --no-restore /p:Configuration=Release /p:BaseIntermediateOutputPath=obj\Revit2025\ /p:IntermediateOutputPath=obj\ClashActionValidation2025\ /p:OutputPath=bin\ClashActionValidation2025\ /p:AssemblyName=BIMaestro.ClashActionValidation
& ./scripts/clash3d-tests/Build.ps1 -RevitVersion 2025 -RunName clash-net8
& ./scripts/codex-tests/StartNativeHarness.ps1 -RevitVersion 2025 -RunName clash-net8
```

`CheckRevit2025.ps1` reste un raccourci compatible vers la compilation directe 2025. Le banc copie le fichier de persistance en remplaçant uniquement la racine Documents par un dossier isolé du passage, sans modifier les champs readonly ni la logique de production. En option, `real-fixture-path.txt` peut désigner une copie de maquette située sous `tmp/codex-native-validation` : le banc ouvre cette copie, analyse jusqu'à 563 sources éligibles et la ferme sans l'enregistrer. Le temps de ce contrôle mesure l'analyse, pas l'ouverture de la maquette.

Pour tester uniquement le renderer, avec les packs WPF .NET 8 installés :

```powershell
dotnet build ./scripts/clash3d-tests/VisualProbe.csproj --configfile ./scripts/clash3d-tests/VisualProbe.NuGet.Config /p:BaseIntermediateOutputPath=../../tmp/clash-visual-probe/obj/ /p:OutputPath=../../tmp/clash-visual-probe/bin/
dotnet ./tmp/clash-visual-probe/bin/VisualProbe.dll ./tmp/clash-visual-probe
```

## Fichiers du module

Le module se trouve dans `BIMaestro/commands/analyse erreur maquette 3D` :

- `SmartClashCommand.cs` : ouverture et unicité de la fenêtre par document.
- `SmartClashEngine.cs` : périmètres, catégories, géométrie, index spatial et sessions d'analyse.
- `SmartVisualCapture.cs` : copie bornée des géométries pendant les callbacks API.
- `SmartClashVisual.cs` : données indépendantes de Revit, découpe locale, vignettes, SVG et visualiseur 3D.
- `SmartExternalHandler.cs` : accès Revit, cadrage, restauration et captures.
- `SmartCheckWindow.xaml` et `.xaml.cs` : écran principal, réglages, progression et filtres.
- `SmartIssueInspector.cs` : inspection et décisions.
- `SmartCheck.Types.cs` : identités, résultats et libellés.
- `SmartCheckState.cs` : décisions, empreintes et préférences.
- `SmartClashReport.cs` : exports et chemins des aperçus.
