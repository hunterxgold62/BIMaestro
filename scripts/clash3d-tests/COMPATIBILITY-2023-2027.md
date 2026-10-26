# Clash 3D — validation du 26 octobre 2026

Les cinq versions de production compilent contre leurs API respectives. Le même banc natif termine ses 57 scénarios sans échec dans chacune des cinq versions installées de Revit, soit 285 contrôles réussis sur les passages finaux.

| Revit | Runtime sur ce poste | Tests réussis | Échecs | Sortie normale | Passage retenu |
| --- | --- | ---: | ---: | --- | --- |
| 2023 | .NET Framework 4.8 | 57 | 0 | Oui | `2023-clash-matrix-20261026-r2` |
| 2024 | .NET Framework 4.8 | 57 | 0 | Oui, code de sortie 0 | `2024-clash-matrix-20261026-r4` |
| 2025 | .NET 8 | 57 | 0 | Oui | `2025-clash-matrix-20261026-r2` |
| 2026 | .NET 8 | 57 | 0 | Oui | `2026-clash-matrix-20261026-r2` |
| 2027 | .NET 10 | 57 | 0 | Oui | `2027-clash-matrix-20261026-r2` |

Les résultats détaillés sont dans `tmp/codex-native-validation/<passage>/result.json`, avec les rendus PNG et `exited-cleanly.txt`. Les journaux Revit des cinq passages finaux confirment une fin normale, sans filtre d’exception non gérée. La synthèse est dans `output/clash-matrix-20261026-results.json` ; les compilations sont consignées dans `output/clash-matrix-20261026-builds.json`.

## Périmètre vérifié

- Intersections physiques, seuils, contacts sans volume, géométries maillées et solides multiples, transformations et liens.
- Canalisations, gaines, chemins de câbles, conduits, calorifuge et exclusion des raccordements physiques.
- Scène exacte du tutoriel : trois tuyaux, deux intersections confirmées en Sélection et Maquette entière, puis une seule après correction.
- Réservations murales et de sol : dimensions, profils, doublons, annulation des modifications en cas d’erreur et famille CML réelle.
- Analyse progressive, annulation, modifications de maquette, sélection de 563 éléments actualisée après ouverture de la fenêtre.
- Aperçus, détail, cadrage, retour à la vue, exports HTML/CSV, filtres et conservation des décisions.
- Fenêtres WPF aux tailles normale et minimale, véritables ExternalEvents et fermeture de la fenêtre dans le contexte API.

Le banc compile les fichiers Clash de production et référence une compilation complète du produit pour le service de réservation. Ses stubs isolent la traduction, le chargement global du thème, la télémétrie, le stockage personnel et l’orchestration du guide. Il ne valide pas la licence, tous les projets réels, ni les douze étapes interactives du tutoriel de bout en bout.

## Écarts rencontrés et dernier contrôle 2024

La copie de test de la fenêtre avait perdu le chemin relatif vers son thème. Le banc remplace maintenant ce chemin par une référence explicite à son propre assembly. Le gabarit générique métrique français est absent de l’installation 2024 ; le banc cherche aussi le gabarit anglais de cette même année.

Un passage 2024 a ensuite terminé 55 contrôles sans échec mais a planté avant la fermeture complète, avec une violation d’accès dans `CefSharp/libcef.dll`. Ce passage n’est pas retenu comme validation. Après correction du manifeste 2024 et lancement sans autre instance Revit, le passage `r4` termine les 57 contrôles, produit une sortie normale et retourne le code 0. Ce résultat ne démontre pas à lui seul la cause du plantage Chromium antérieur, qui reste conservé dans les traces du passage `r3`.

## Installations locales corrigées

Les manifestes 2024, 2025 et 2027 pointaient encore vers la DLL commune compilée contre Revit 2023. Chaque année dispose maintenant de son propre dossier `AppData/Local/BIMaestro/Bin/Revit<année>-Clash3D-<hash>`, avec sauvegarde du manifeste précédent. Les références RevitAPI/RevitAPIUI et le runtime sont contrôlés avant copie ; les hash SHA-256 des cinq DLL installées correspondent aux compilations de production vérifiées. Voir `output/clash-matrix-20261026-installations-final.json`.

Le script `InstallRevit2025Fix.ps1` conserve son nom historique et accepte désormais 2023 à 2027. Il refuse une DLL d’une autre année, un runtime incompatible et les assemblies de test. Les quatre essais de refus sont consignés dans `output/clash-installer-rejections.json`. Un redémarrage de Revit est nécessaire pour charger une nouvelle DLL.

## Rejouer

Choisir un nom de passage inédit ; les résultats déjà démarrés sont protégés contre l’écrasement.

```powershell
./scripts/clash3d-tests/BuildMatrix.ps1 -RunName clash-nouveau-passage
./scripts/clash3d-tests/StartMatrix.ps1 -RunName clash-nouveau-passage
```

Les scripts utilisent les API installées de chaque année, Visual Studio MSBuild pour 2023/2024 et le SDK privé `tmp/dotnet10` pour les hôtes modernes. Ils n’ouvrent que leurs fixtures générées. Les inscriptions temporaires sont retirées après chargement du banc. Le lanceur contrôle aussi le code de sortie du processus.
