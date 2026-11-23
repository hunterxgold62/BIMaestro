# Tournage automatique dans Revit 2026

Claude ne pilote pas la souris dans Revit. À la place, une **build de test** recharge l'apparence quand le fichier de préférences change, et un script écrit une variante toutes les N secondes pendant qu'ffmpeg filme la zone de l'arborescence.

## Mode démo (code)

- Tout est sous `#if BIMAESTRO_DEMO` : `ColoringStateManager.cs` (minuterie 150 ms qui surveille `couleursRuban.json` et `iconesArborescence.json`, puis `Reset()` + `Apply()`), `RibbonColorPreferences.cs` et `ProjectBrowserIcons.cs` (`DemoInvalidate()` qui vide les caches).
- La constante n'existe qu'avec `-p:BimaestroDemo=true` (`BIMaestro.Revit2025.csproj`). **Jamais en release** ; vérifier qu'une DLL release ne contient pas `DemoInvalidate`.
- Compiler : `node motion-design/capture/build-demo.mjs DemoN` (processus direct sans shell, voir AGENTS.md). Revit verrouille la DLL chargée : compiler dans un **nouveau** dossier (`Demo2`, `Demo3`…).
- Pointer Revit dessus : `node motion-design/capture/set-addin.mjs <chemin\BIMaestro.dll>` (sauvegarder d'abord le `.addin` en `.bak`), puis redémarrer Revit. Remettre la DLL installée (`%LOCALAPPDATA%\BIMaestro\Bin\Revit2026\BIMaestro.dll`) après le tournage.

## Capture

- `motion-design/capture/capture.mjs` (ffmpeg gdigrab, 60 i/s, zone de `zone.json`). Actuellement `{x:0, y:218, w:476, h:1128}` = arborescence ancrée à gauche sur l'écran de gauche (bureau 5120×1440, deux écrans).
- Avant chaque prise : filmer 0,1 s de la **zone seule** pour vérifier que Revit est au premier plan. Ne pas faire de capture plein écran si ce n'est pas nécessaire ; supprimer immédiatement toute image montrant autre chose que Revit.
- Demander à Paul : projet de démo (« Projet d'architecture (ex.) » / Snowdon Towers), arborescence au même endroit, ne pas toucher l'écran de gauche pendant la prise.

## Défilé de variantes

`node motion-design/capture/demo-cycle.mjs <secondes> <nomPrise> <jeu>` — jeux existants : `effets` (16 looks), `icones`, `config` (réglages cumulatifs), `metiers` (5 thèmes métiers). Il sauvegarde puis **restaure** les deux fichiers de préférences, et écrit `rushes/<prise>.txt` avec l'horodatage de chaque variante (variante k ≈ `1,1 + 5·k` s).

Réglages pilotables (section `Arborescence` de `couleursRuban.json`) : `ModeFond` (13 fonds : Uni, Verre dépoli, Plan d'architecte, Encre dans l'eau, Bulles pastel, Vagues pastel, Rubans fluides, Courbes topographiques, Grille d'architecte, Aurore boréale, Constellation douce, Lucioles pastel, Dégradé pastel animé), `Fond`/`Texte`/`Accent` (#AARRGGBB), `ColorerTypesVues`, `CibleCouleursVues` (Fond/Texte), `ColorerCategories` + `ReglesCategories` [{Nom, Couleur, Chemin = libellé exact du dossier, Effet, Portee: Branche}]. Effets : Fond, Texte, Souligné, Gras, Italique, Barré, Bordure, Pastille, Icône, Badge, Dégradé, Animation.

Icônes (`iconesArborescence.json`) : `Rules [{Name = libellé exact et complet de la ligne, IconId}]` ; IconId intégrés : lighting, power, ventilation, plumbing, architecture, structure, coordination, heating, fire, data, existing, demolition, new-project, study, validation, publication, archive, levels, zone, garden, room, reception, restaurant, roof, details, presentation.

## Pièges connus

- **Délai d'application** : prendre le plan ~1,8–2 s après le changement (`media-start = 1,1 + 5·k + 1,9`). Certaines variantes peuvent mettre ~5 s (vu sur « Vagues pastel + Dégradé ») : toujours vérifier chaque plan en snapshot et remplacer un look vide.
- Ré-encoder les rushes avant montage : `ffmpeg -i rush.mp4 -vf fps=30 -c:v libx264 -crf 16 -g 15 -pix_fmt yuv420p` (sinon images vides au seek).
- Les effets « Bordure » et « Animation » sont discrets dossiers repliés : déplier quelques dossiers avant de tourner pour les montrer.
- Libellés du projet Snowdon : « Elévations » (sans accent sur le É), « Feuilles (all) ».
