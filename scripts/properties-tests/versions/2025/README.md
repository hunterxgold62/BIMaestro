# Essais avancés Revit 2025

DLL finale testée : `tmp/properties-tests/experiments/2025/advanced2025-v3.dll`. Entrée commune `BIMaestro.PropertiesTests.Experiment.Run(UIApplication, string)`.

Modes : `green`, `blue`, `night`, `ink`, `inspect`, `repaint`, `restore`. `night` mémorise et restaure le thème natif. `ink` a été validé en thème clair uniquement.

Le sélecteur WPF est habillé sans remplacer son contrôle. La grille GXWND est sous-classée localement ; le dessin natif est exécuté avant toute surcouche. Les fenêtres flottantes sont recherchées dans le processus courant. Aucun pointeur C++ ou message privé de grille n’est utilisé.

Les bandeaux sont reconnus par deux bandes de pixels gris. Le premier prototype faisait trop de lectures GDI individuelles (~81 ms) ; il a été retiré. La version finale utilise des DIB en cache et des copies groupées. Les caractères gris peuvent aussi être recolorés ; ce traitement affecte potentiellement les traits gris des contrôles. Il ne modifie ni noms, ni valeurs, ni géométrie des lignes. Le champ en cours d’édition reste natif.

Mesures : bandeaux seuls ~0,3–4 ms ; texte complet ~3,8 ms de moyenne et ~21 ms de pointe pendant la séquence enregistrée. Coût variable et couverture de contextes insuffisante pour une fonctionnalité de production.

Empreinte identique avant/après : `965F318E2728E54CF63E35F0674B6B39093384AF6788B7338EF4A011DFC92FD5`. Restauration finale : zéro propriété WPF et zéro hook ; DC, bitmaps et thème restaurés.

Compilation : `./scripts/properties-tests/versions/2025/Build.ps1 -RevitVersion 2025 -Name advanced2025-v3`. Exécuter dans le harnais jetable, puis `restore` avec cette même DLL.

Troisième série : voir [nouveaux effets et zoom natif](../../TEXT-EXPERIMENTS.md). Les couleurs du texte et les fonds sont transformés graphiquement ; le zoom utilise le geste natif Ctrl+molette. Restaurer les effets et annuler séparément les crans de zoom demandés.
