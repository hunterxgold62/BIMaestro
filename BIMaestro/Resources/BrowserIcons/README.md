# Icônes du navigateur de projets

Dans **Couleurs → Arborescence du projet → Icônes devant les noms**, activer les icônes,
associer un nom exact à une image puis enregistrer. Les règles sont locales, indépendantes
des profils de couleurs, et ne modifient pas le modèle Revit. La première règle correspondante
gagne (espaces normalisés et casse ignorée). Les icônes natives sont conservées.

L'injection est limitée à Revit 2024–2027. Elle utilise le navigateur interne déjà ciblé par
la personnalisation des couleurs ; le rendu réel reste à vérifier dans chaque version Revit.
Le test `node scripts/test-browser-icons.cjs` couvre le comportement DOM sur une fixture,
pas une session Revit. Il produit des aperçus clair/sombre dans `tmp/browser-icons`.

Les dix PNG ont été créés avec l'outil intégré imagegen, puis copiés ici. Ils sont incorporés
dans la DLL et décodés à 64 px pour un affichage de 18 px. Les imports PNG/JPEG/BMP sont réencodés
en PNG et conservés dans `iconesArborescence.json`, à côté de `couleursRuban.json` : ils restent
disponibles si leur fichier d'origine est déplacé. Aucun appel IA n'est fait depuis Revit.

## Prompts utilisés

Ajouts : les six images suivantes sont également produites avec imagegen intégré, puis copiées dans ce dossier. Les associations déjà enregistrées restent inchangées.

- `architecture.png`: Use case: logo-brand. Single tiny Revit browser icon for ARCHITECTURE: emerald green house silhouette with a simple pitched roof and one large doorway cutout. Bold flat geometric shape recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, border, shadow, gradient, texture or fine details.
- `structure.png`: Use case: logo-brand. Single tiny Revit browser icon for STRUCTURE: solid steel-blue I-beam cross section, straight-on uppercase I shaped structural beam silhouette with thick horizontal flanges and thick central vertical web. Bold flat geometric shape recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, border, shadow, gradient, texture or fine details.
- `coordination.png`: Use case: logo-brand. Single tiny Revit browser icon for BIM COORDINATION: two interlocking chain links diagonally arranged, vivid violet, very thick rounded strokes and large open centers. Bold flat geometric symbol recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, border, shadow, gradient, texture or fine details.
- `heating.png`: Use case: logo-brand. Single tiny Revit browser icon for HEATING: coral orange radiator seen straight-on, four thick rounded vertical bars joined by two short horizontal pipes, simple silhouette. Bold flat geometric symbol recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, border, shadow, gradient, texture, heat rays or fine details.
- `fire.png`: Use case: logo-brand. Single tiny Revit browser icon for FIRE SAFETY: vivid red fire extinguisher silhouette with broad cylindrical body, short top lever and one thick curved hose on the right. Extremely simplified bold flat geometric symbol recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, label, border, shadow, gradient, texture or fine details.
- `data.png`: Use case: logo-brand. Single tiny Revit browser icon for DATA NETWORKS: magenta network topology symbol, one solid square at top linked by thick simple T-shaped lines to two solid squares below. Exactly three square nodes, clear large gaps. Bold flat geometric symbol recognizable at 18x18 pixels. Square canvas, centered symbol occupying 85 percent. True transparent alpha background. No text, border, shadow, gradient, texture or fine details.

- `lighting.png`: Use case: logo-brand. Create one tiny UI icon asset for a Revit project browser: a single bright golden yellow light bulb silhouette, flat solid geometric shape with thick clean outlines, extremely simple and recognizable at 18 by 18 pixels. Square canvas, centered bulb occupies 85% of canvas, true transparent background with alpha. No text, no shadows, no gradient, no border, no decorative rays, no fine details. Save the generated image.
- `power.png`: Use case: logo-brand. One UI icon asset: a single orange lightning bolt. Flat solid geometric silhouette, very bold simple shape recognizable at 18x18 pixels. Square canvas, centered bolt occupies 85 percent canvas, true transparent background alpha. No text, no border, no shadow, no gradient, no other objects.
- `ventilation.png`: Use case: logo-brand. One tiny UI icon asset for ventilation: a single turquoise three-blade fan silhouette with round central hub. Flat solid geometric shapes, very bold simple rounded blades recognizable at 18x18 pixels. Square canvas, centered fan occupies 85 percent canvas. True transparent background alpha. No text, no border, no shadow, no gradients, no fine details, no other objects.
- `plumbing.png`: Use case: logo-brand. One tiny UI icon asset for plumbing: a single vivid blue water droplet silhouette. Flat solid geometric teardrop shape, extremely simple recognizable at 18x18 pixels. Square canvas, centered droplet occupies 85 percent canvas, true transparent background alpha. No text, no border, no shadow, no gradient, no inner details, no other objects.
