# Vérification des badges d’infobulle

## Compatibilité Revit 2023 à 2027 — audit du 11 octobre 2026

Les builds Release ont été compilés séparément contre les API 2023, 2024,
2025.5 et 2026.5 installées. Les quatre builds passent le contrôle des contrats
internes du ruban utilisés par les infobulles : champ `m_RibbonItem`, propriété
`ToolTip`, événement `ToolTipOpened`, `RibbonToolTip.Content`, contrôle natif
et `ContentSourceProperty`. Les 65 associations de boutons et les 29 vidéos
avec leurs posters sont incluses dans chaque sortie ; les API et le runtime
référencés correspondent à l'hôte installé.

`BIMaestro.Revit2025.csproj` lit désormais `RevitAPI.runtimeconfig.json` pour
choisir .NET 8 ou .NET 10 en 2025/2026. Les deux versions installées sur ce
poste utilisent .NET 10. Revit 2027 cible .NET 10. Les API de 2027 ne sont pas
installées : le build échoue explicitement, et cette version reste **non
vérifiée**, y compris ses interfaces internes. Il faudra compiler puis
exécuter le diagnostic dans Revit 2027 pour confirmer son affichage.

Les deux installateurs utilisent désormais des dossiers/DLL distincts pour
2024 à 2027. `Installer/RevitVersioned.Files.iss` exige les quatre sorties ;
le compilateur Inno bloque bien les deux paquets lorsque la DLL 2027 manque.
La syntaxe de leurs sections Pascal a aussi été compilée dans des paquets de
test isolés, jamais installés ni destinés à la distribution. Aucun nouvel
installateur complet 2023–2027 n'a donc été produit.

Audit reproductible (SDK .NET 10 et MSBuild requis) :

```powershell
./scripts/ribbon-badge-tests/VerifyCompatibility.ps1
# Vérifier les sorties déjà compilées :
./scripts/ribbon-badge-tests/VerifyCompatibility.ps1 -SkipBuild
```

Le rapport `tmp/ribbon-compatibility/results.json` distingue PASS, FAIL et
UNAVAILABLE ; une version absente ne compte jamais comme validée.
`CompatibilityCheck.cs` inspecte les métadonnées sans charger les DLL mixtes
de Revit hors de leur processus. `CompatibilityProbe.csproj` réutilise le
diagnostic natif des dix-sept infobulles Couleur et information avec huit
lectures vidéo. Les contrôles de métadonnées ne remplacent pas ce test natif.

### Tests natifs complémentaires, 23 h 04 à 23 h 09

Revit 2023, 2024.3 et 2025.5 : dix-sept PASS, zéro FAIL, huit vidéos dont
la position avance, puis DONE dans chaque session. Largeur native : 334 DIP ;
hauteur maximale : 307,33 DIP. Captures et journaux dans
`tmp/ribbon-compat-probe-2023/`, `-2024/` et `-2025/`.
La première passe 2025 a été interrompue sur une infobulle lors d'un changement
de fenêtre ; la relance complète et isolée est entièrement PASS. Le premier
journal est conservé sous `information-interrupted-window.log`.
Les retouches Flappy Bird, rosace et maquette ont été inspectées sur les
captures natives. Les diagnostics et leurs fichiers d'activation sont
désactivés après le test. Rapport : `tmp/ribbon-compatibility/native-results.json`.
Le test natif 2026.5 des mêmes dix-sept commandes figure plus bas (22 h 40).
Ces tests portent sur l'affichage des infobulles et la lecture des médias,
pas sur l'exécution de toutes les commandes métier du plugin.

Sources Autodesk : [migration Revit 2027 vers .NET 10](https://help.autodesk.com/cloudhelp/2027/FRA/Revit-WhatsNew/files/GUID-8D7A4715-EAF8-4BD1-BE78-061F900D0BCE.htm)
et [transition des produits Revit vers .NET 10](https://help.autodesk.com/view/RVT/2025/ENU/?caas=caas%2Fsfdcarticles%2Fsfdcarticles%2FRevit--Requirements-for-products-affected-by-the-Microsoft--NET-10-transition.html).

Le diagnostic est volontairement désactivé sans `enabled.flag` à côté de sa DLL.
Il utilise le contrôle natif `Autodesk.Internal.Windows.ToolTip`, puis vérifie que
NEW et MAJ sont présents dans son arbre visuel et dans la zone visible de l’en-tête.
Il enregistre les images et referme les infobulles. Aucun document Revit n’est modifié.

## Résultat du 11 octobre 2026

- Revit 2023 : NEW et MAJ PASS ; largeur native 334 DIP ; positions des badges
  respectivement (289,33 ; 12) et (291,33 ; 12).
- Revit 2026.5 : NEW visible dans le contrôle natif et confirmé par la capture
  de l’utilisateur lors du survol du bouton Gabarit de vue.
- Compilations BIMaestro pour Revit 2023 et 2026 réussies.

Les captures et le journal 2023 sont dans `tmp/ribbon-badge-probe-2023-final/`.

## Compilation du diagnostic

```powershell
./tmp/dotnet10/dotnet.exe build scripts/ribbon-badge-tests/Probe.csproj -p:RevitVersion=2023 -p:OutputPath=../../tmp/ribbon-badge-probe-2023-final/
```

Le manifeste temporaire `ZZ.BIMaestro.RibbonBadgeProbe.addin` doit être désactivé
après la vérification. La présentation personnalisée dépend des contrôles internes
Autodesk ; il faudra la revérifier lors d’une évolution de ces contrôles.

## Exemples de contenus enrichis

| Bouton | Exemple |
| --- | --- |
| Calcul des canalisations | Schéma des trois étapes |
| Qui a fait ça ? | Avant/après illustrant un déplacement fictif |
| Analyse de poids | Encadré d’information courte |
| Temps par projet | Badge violet SUIVI |
| Clash 3D | Capture du rendu 3D de deux volumes en intersection |
| Sélection d’objet | MP4 illustré de six secondes, sans son |

Les médias locaux sont dans `BIMaestro/Content/ToolTipExamples` et sont copiés
avec chaque compilation. Les exemples sont définis dans `RibbonToolTipExamples.cs`.
La vidéo démarre au chargement du contenu, boucle et ferme son lecteur lors
de la fermeture ou du déchargement de l’infobulle. Un aperçu fixe est prévu
si la lecture échoue.

`ExamplesProbe.cs` ouvre les six infobulles avec le contrôle Autodesk natif.
Il vérifie les contenus, la durée et l’avancement de la vidéo, enregistre les
captures `example-0.png` à `example-5.png`, puis referme chaque infobulle.
Pour l’activer, enregistrer la classe `RibbonExamplesProbe` dans le manifeste
temporaire et créer `examples.flag` à côté de la DLL du diagnostic.

### Vérification native du 11 octobre 2026, à 21 h 23

Les six exemples sont PASS dans Revit 2026.5. Les captures ont été inspectées :
schéma, comparaison, encadré, badge violet, image et vidéo sont visibles.
Le MP4 a une durée de six secondes ; le lecteur a avancé à 2,408 secondes
avant la capture. Chaque infobulle a ensuite été refermée ; le code ferme le
lecteur vidéo à la fermeture du popup et lors du déchargement du contenu.
Journal et captures : `tmp/ribbon-examples-probe-2026/`.

## Quatre effets visuels supplémentaires

- Sélection d’éléments : surbrillance progressive des réseaux ciblés (`Highlight.mp4`).
- Clash 3D : transparence d’une paroi révélant une traversée (`ClashCut.mp4`).
- Gabarit de vue : balayage comparant les graphismes avant/après (`ViewWipe.mp4`).
- Calcul des canalisations : trois vignettes réseau, diamètres, Excel (`PipeSequence.png`).

Les animations durent six secondes et sont illustratives. Elles sont générées
par `GenerateVisualEffects.py`. `VisualEffectsProbe.cs` vérifie les quatre
contenus dans le contrôle natif et la lecture des trois MP4. La présentation
affiche chaque animation quatre secondes et les vignettes trois secondes.
Elle nécessite `effects.flag` et le manifeste temporaire ciblant
`RibbonVisualEffectsProbe` ; désactiver ce manifeste après vérification.

### Vérification native du 11 octobre 2026, à 21 h 45

Les quatre effets sont PASS dans Revit 2026.5. Les captures ont été inspectées.
Les trois vidéos ont une durée de six secondes et leur lecteur a avancé
respectivement à 3,535, 3,528 et 3,481 secondes. Le badge NEW reste visible
sur Gabarit de vue. Journal et captures : `tmp/ribbon-effects-probe-2026/`.
Les versions 2023 et 2026 ont été compilées et installées ; ces quatre effets
ont été vérifiés à l'exécution dans la version 2026 uniquement.
Le manifeste temporaire a été désactivé après la présentation.

## Panneau Modification

Les quinze commandes actives, y compris les sous-menus, ont un exemple visuel :

| Commande | Exemple |
| --- | --- |
| Auto Réservation | Animation d'une ouverture autour d'un réseau |
| Bride auto | Ajout animé de brides |
| Choix bride | Trois types, référence mise en bleu |
| Suppression de brides | Retrait animé et reconnexion |
| Dynamo 1 à 5 | Entrées, script numéroté, résultat générique |
| Réglages Dynamo | Cinq associations de fichiers et boutons |
| Gestion Excel | Échange visuel entre deux tableaux |
| Surcharges | Comparaison animée des affichages |
| Organisateur | Noms avant / après |
| Phases | Existant, démoli et nouveau |
| Purge | Sélection des éléments inutilisés |

Génération : `GenerateModificationExamples.py`. Les quatre MP4 sont locaux,
silencieux et durent six secondes ; les onze images sont des schémas illustratifs.
Les scripts Dynamo étant configurables, leurs aperçus n'annoncent aucun résultat
métier particulier. Les commandes existantes ne sont pas modifiées.
`ModificationProbe.cs` vérifie les quinze contrôles natifs, la présence des images,
les dimensions et l'avancement des quatre vidéos ; il produit `modification.log`
et quinze captures. Activation temporaire : `modification.flag` et classe
`RibbonModificationProbe` dans le manifeste du diagnostic.

### Vérification native du 11 octobre 2026, à 21 h 53

Les quinze commandes sont PASS dans Revit 2026.5, y compris les sous-menus.
Largeur : 334 DIP ; hauteur maximale : 307,33 DIP. Les quatre lecteurs ont
avancé à 2,474 / 2,640 / 2,622 / 2,837 secondes, pour une durée de six secondes.
Les captures de chaque type visuel ont été inspectées ; le diagnostic a terminé
avec `DONE`. Résultats : `tmp/ribbon-modification-probe-2026/`.
Les versions 2023 et 2026 sont compilées et installées, avec zéro erreur ;
la vérification d'exécution de ces nouveaux contenus porte sur Revit 2026.
Le manifeste temporaire est désactivé après le test.

## Panneau Outils de visualisation

Les neuf commandes, dont Face 3D dans le menu Peinture, ont un aperçu :

| Commande | Exemple |
| --- | --- |
| Sélection d'éléments | Surbrillance progressive du réseau |
| Ouvrir la vue | Navigation animée entre la vue et sa feuille |
| Export de nomenclature | Tableau vers fichiers Excel et PDF |
| Sélection d'objet | Sélection animée des objets similaires |
| Gabarit de vue | Comparaison des graphismes par balayage |
| Miniature | Apparition d'un aperçu au survol |
| DWG Exp. | Plusieurs vues vers un fichier DWG |
| Peinture | Zone cliquée et composition du mur |
| Face 3D | Alignement animé sur la face sélectionnée |

Les six nouveaux assets sont générés par `GenerateVisualizationExamples.py`.
Trois animations locales de six secondes complètent les trois animations déjà
validées ; les trois images restantes sont des schémas illustratifs.
`VisualizationProbe.cs` vérifie les neuf contrôles natifs et les six lecteurs,
avec `visualization.flag` et la classe `RibbonVisualizationProbe`.

### Vérification native du 11 octobre 2026, à 22 h 13

Le test final est PASS pour les neuf commandes dans Revit 2026.5, sans FAIL.
Largeur : 334 DIP ; hauteur maximale : 306 DIP. Les six vidéos ont avancé
entre 2,459 et 2,803 secondes, avec une durée de six secondes. Les six nouveaux
contenus ont été inspectés sur les captures natives. Journal et captures :
`tmp/ribbon-visualization-probe-2026-final/`. La première séquence a subi des
tests superposés ; le diagnostic final empêche les réentrées et prend un verrou
exclusif pendant le test. Le manifeste temporaire a été désactivé ensuite.
Les versions 2023 et 2026 sont compilées et installées ; la vérification native
de ces nouveaux aperçus porte sur la version 2026.

## Sélection d'éléments et panneau Bêta

`GenerateBetaExamples.py` remplace l'ancien visuel abstrait de Sélection
d'éléments par trois étapes : objet déjà sélectionné, filtre famille/type
et portée vue/maquette, puis éléments correspondants encadrés en bleu.
Il génère aussi les trois aperçus Bêta :

- MEP Booster : rotation d'un raccord libre, rosace et aperçu au survol.
- Couleurs réseaux : passages du gris aux couleurs des types de systèmes.
- Maquette MEP : visite illustrée à la première personne avec touches ZQSD.

Les quatre MP4 sont locaux, silencieux et durent six secondes. Les dessins
sont illustratifs ; les actions MEP restent soumises aux contraintes réelles
du raccordement. `BetaProbe.cs` vérifie les quatre contrôles et lecteurs avec
`beta.flag` et la classe `RibbonBetaProbe`.

### Vérification native du 11 octobre 2026, à 22 h 21

Les quatre contenus sont PASS dans Revit 2026.5, avec `DONE` et sans FAIL.
Les quatre lecteurs ont avancé entre 2,373 et 2,639 secondes ; durée : six
secondes. Les captures et les posters ont été inspectés. Largeur : 334 DIP,
hauteur maximale : 307,33 DIP. Résultats : `tmp/ribbon-beta-probe-2026/`.
Versions 2023 et 2026 compilées et installées ; vérification d'exécution en
2026 uniquement. Le manifeste temporaire est désactivé après le test.

## Panneau Outils IA

Les six commandes actives, bibliothèque commune comprise, ont un aperçu :

| Commande | Exemple |
| --- | --- |
| Famille IA | Demande de table → famille avec largeur paramétrique |
| Bibliothèque commune | Galerie par catégories avec téléchargement |
| Chat él. | Objet sélectionné relié à une conversation |
| Corr. IA | Phrase avant / proposition corrigée |
| Audit IA | Feuille avec anomalies classées Erreur / Mineur |
| Rendu plan IA | Balayage plan / rendu illustré à géométrie identique |

`GenerateAiExamples.py` génère deux animations locales de six secondes et
quatre schémas statiques. Le rendu est une illustration de principe, et non
un résultat généré par l'outil IA. `AiProbe.cs` vérifie les six contrôles et
les deux vidéos via `ai.flag` et la classe `RibbonAiProbe`.

### Vérification native du 11 octobre 2026, à 22 h 27

Les six contrôles sont PASS dans Revit 2026.5, sans FAIL et avec `DONE`.
Les deux vidéos ont avancé à 2,454 et 2,595 secondes ; durée : six secondes.
Les six captures natives ont été inspectées. Dimensions : 334 × 290,67 DIP.
Résultats : `tmp/ribbon-ai-probe-2026/`. Versions 2023 et 2026 compilées et
installées ; vérification d'exécution en 2026 uniquement. Le manifeste du
diagnostic temporaire est désactivé après le test.

## Panneau Spécifique aux familles

Les huit commandes, y compris les menus déroulants, ont un aperçu :

| Commande | Exemple |
| --- | --- |
| Navigateur de familles | Dossiers, galerie et chargement animé |
| Rosace | Choix d'une famille dans un menu radial animé |
| Convertir paramètres | Partagé → famille, propriétés conservées |
| Purge | Paramètres utilisés conservés, inutilisé retiré, sauvegarde |
| Traduction paramètres IA | Width / Height / Material → français |
| Traduction de vues IA | Floor Plan / Front / Section → français |
| Export unités | Unités et précision → fichier JSON |
| Import unités | Fichier JSON → unités et précision |

Génération : `GenerateFamilyExamples.py`. Deux MP4 locaux silencieux de six
secondes et six images illustratives. Le titre de la rosace dans l'infobulle
est explicite (« Rosace des familles ») malgré le libellé minimal du bouton.
`FamilyProbe.cs` vérifie les huit contrôles, les dimensions et les deux lecteurs,
avec `family.flag` et la classe `RibbonFamilyProbe`.

### Vérification native du 11 octobre 2026, à 22 h 31

Les huit contrôles sont PASS dans Revit 2026.5, sans FAIL et avec `DONE`.
Les deux lecteurs ont avancé à 2,555 et 2,595 secondes ; durée : six secondes.
Les huit captures natives ont été inspectées. Largeur : 334 DIP ; hauteur
maximale : 307,33 DIP. Résultats : `tmp/ribbon-family-probe-2026/`.
Les versions 2023 et 2026 sont compilées et installées ; les nouveaux contenus
ont été vérifiés à l'exécution en 2026 uniquement. Le manifeste du diagnostic
temporaire est désactivé après le test.

## Panneau Analyse — présentation visuelle

Les sept commandes, sous-menus compris, ont des aperçus visuels :

| Commande | Exemple |
| --- | --- |
| Calcul des canalisations | Vignettes réseau, diamètres et Excel |
| Qui a fait ça ? | Déplacement animé et chronologie |
| Infos objet | Objet sélectionné relié à son dernier changement |
| Analyse de poids | Classement animé par taille, plus gros résultat encadré |
| Temps par projet | Anneau de répartition et barres de durées animés |
| Suivi maquette | Maquette et chronologie des ouvertures/fermetures |
| Clash 3D | Transparence de paroi révélant une intersection |

`GenerateAnalysisExamples.py` crée trois MP4 silencieux de six secondes et
deux schémas. Tous les noms, valeurs et événements sont illustratifs ; les
médias ne lisent pas les données de la maquette. Les badges MAJ et SUIVI
restent présents. `AnalysisProbe.cs` vérifie les sept contrôles et les quatre
vidéos via `analysis.flag` et la classe `RibbonAnalysisProbe`.

### Vérification native du 11 octobre 2026, à 22 h 35

Les sept contrôles sont PASS dans Revit 2026.5, sans FAIL et avec `DONE`.
Les quatre vidéos ont avancé entre 2,423 et 2,632 secondes ; durée : six
secondes. Les cinq nouveaux contenus ont été inspectés sur leurs captures
natives, avec les badges MAJ et SUIVI visibles. Largeur : 334 DIP ; hauteur
maximale : 307,33 DIP. Résultats : `tmp/ribbon-analysis-probe-2026/`.
Versions 2023 et 2026 compilées et installées ; vérification d'exécution en
2026 uniquement. Le manifeste du diagnostic temporaire est désactivé.

## Panneau Couleur et information

Les dix-sept commandes, sous-menus compris, ont un aperçu :

| Commande | Exemple |
| --- | --- |
| Couleurs | Palette et panneaux colorés animés |
| Guide couleurs | Ruban, palette, résultat |
| Couleur reset | Onglets colorés / réinitialisés |
| Papa Noël | Guirlande animée |
| Onglets | Apparition des miniatures |
| Compagnon | Personnage pixel suivant la souris sur les onglets |
| Compagnon fenêtre | Personnage pixel suivant la souris dans la fenêtre |
| Exemple | Page de présentation du plugin |
| Maquette d'essai | Plan vers maquette de démonstration |
| Parcours guidés | Choisir, essayer, terminer |
| Note | Page des nouveautés |
| Snake | Mini-jeu illustré animé |
| Flappy Bird | Oiseau et obstacles animés |
| Option | Ruban et curseurs de réglages |
| Contact | Retour vers LinkedIn |
| Rosace boutons | Seize positions et favoris animés |
| Soutenir | Café avec un cœur, page Ko-fi |

`GenerateInformationExamples.py` génère huit MP4 silencieux de six secondes
et neuf images. Les médias sont des illustrations de principe, notamment les
personnages, pages et jeux ; ils ne sont pas des captures des outils réels.
`InformationProbe.cs` vérifie les dix-sept contrôles, dimensions et huit vidéos
via `information.flag` et la classe `RibbonInformationProbe`.

### Retouches visuelles après retour utilisateur

Snake dispose désormais d'un corps continu, d'une tête avec yeux et d'une pomme.
Cinq autres aperçus ont ensuite été repris : Flappy Bird (oiseau, ailes animées,
tuyaux et décor), Rosace boutons (ouverture, icônes et favori sélectionné),
Maquette d'essai (plan, bâtiment avec fenêtres et réseau), Onglets (plans de
niveaux et coupe distincts), Parcours guidés (choix, essai et validation illustrés).
Les compagnons sont conservés. Médias installés dans les versions 2023 et 2026.
Les trois MP4 retouchés sont intégralement décodables : 120 images, six secondes,
640 × 360 ; les deux PNG ont été vérifiés et les aperçus inspectés visuellement.
Cette retouche des médias n'a pas fait l'objet d'un nouveau test natif Revit.

### Vérification native du 11 octobre 2026, à 22 h 40

Les dix-sept contrôles sont PASS dans Revit 2026.5, sans FAIL et avec `DONE`.
Les huit vidéos ont avancé entre 2,362 et 2,741 secondes ; durée : six
secondes. Les captures natives confirment les illustrations intégrées.
Largeur : 334 DIP ; hauteur maximale : 307,33 DIP.
Résultats : `tmp/ribbon-information-probe-2026/`.
Versions 2023 et 2026 compilées et installées ; vérification d'exécution en
2026 uniquement. Le manifeste du diagnostic temporaire est désactivé.
