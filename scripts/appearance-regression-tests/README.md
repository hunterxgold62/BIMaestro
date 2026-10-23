# Régressions Couleurs, arborescence et Propriétés

Passe du 22 octobre 2026. Les tests utilisent des objets WPF et des fichiers temporaires isolés ; ils ne modifient ni une maquette ni les préférences réelles.

## Exécution

Depuis la racine du dépôt :

```powershell
& scripts/appearance-regression-tests/Run.ps1
& scripts/properties-order-tests/unit/Run.ps1
node scripts/test-browser-color-restore.cjs
node scripts/test-browser-folder-colors.cjs
node scripts/test-browser-active-parent.cjs
```

Le test navigateur utilise Edge en arrière-plan et Playwright du runtime Codex, ou le module indiqué par `PLAYWRIGHT_MODULE`. Le script PowerShell utilise le compilateur Visual Studio et les références .NET Framework 4.8 installés sur ce poste.

Les suites existantes de préférences et d’éditeur s’exécutent avec le SDK .NET 10, disponible ici dans `tmp/dotnet10/dotnet.exe`. Dans un bac à sable Windows, le démarrage d’Edge et les sauvegardes atomiques des fichiers temporaires peuvent demander une exécution autorisée hors du bac à sable.

## Couverture

- 20 vérifications WPF et de dessin : conservation des bindings et ressources dynamiques, restauration des valeurs locales, remise à zéro des seuls contrôles recolorés, réponses de survol arrivant après une réinitialisation ou un changement de vue/navigateur, grille reconstruite avec moins de lignes, identité groupe/paramètre, contraste automatique et protection contre un rafraîchissement avant dessin réentrant.
- 15 scénarios de permutation et restauration, dont une restauration native qui déplace les lignes puis signale un échec, le maintien des lignes vides terminales en bas et la conservation de la référence d’origine après remplacement du contrôle ; 4 scénarios supplémentaires exécutent la qualification de production des versions et catégories, dont le déplacement des groupes d’une vue 3D.
- Arborescence : couleurs natives et priorité CSS conservées après recoloration des vues, cinq effets de texte, 30 rafraîchissements et réinitialisation ; règles de dossiers et navigation du parent actif. La suite existante de restauration de disposition vérifie aussi les dimensions, le défilement et 30 changements de projet simulés dans le navigateur.
- Préférences : 11 vérifications ; éditeur visuel : 20 vérifications existantes.

Le harnais WPF extrait sans réécriture le gestionnaire de valeurs visuelles et les méthodes de restauration/survol du fichier de production. Le renderer, les préférences et le réordonnancement sont compilés depuis leurs sources de production. Les accès à Revit, à la grille native et aux réponses asynchrones du navigateur sont simulés ; ces tests ne remplacent pas une session de validation interactive dans Revit.

Le module Propriétés a été recompilé avec les API installées de Revit 2023, 2024, 2025, 2026 et 2027 via `scripts/properties-order-tests/BuildAll.ps1 -OutputTag '-color-review'`. Ces DLL de sonde ne sont pas installées dans Revit.

## Correction des vues du 23 octobre 2026

Le filtre de production autorisait seulement les murs et laissait les ordres des vues dans l’aperçu. Il autorise maintenant aussi la catégorie Vues, avec les mêmes vérifications natives de correspondance entre lignes et paramètres. Les autres catégories ne sont pas élargies par cette correction.

Une instance réelle de Revit 2024.3 a exécuté le déplacement du groupe **Phase de construction** devant **Graphismes** dans une vue 3D jetable, puis sa restauration et sa réapplication. Les empreintes des paramètres sont identiques, avec zéro différence. La [preuve compacte](evidence/native-view-layout-2024.json) distingue le test natif du contrôle séparé du filtre de production. Il ne s’agit pas d’une qualification de tous les éditeurs de vues dans toutes les versions.

## Ligne vide et retour temporaire à l’ordre d’origine

La ligne vide de remplissage reste en fin de grille lorsque le dernier groupe ou paramètre remonte. Les séparateurs natifs masqués restent associés à leur bloc. Une ligne de remplissage déjà déplacée par une ancienne version est reconnue par sa hauteur et replacée en bas.

Le contrôle natif est synchronisé avant `WM_PAINT`, à partir des métadonnées déjà capturées. Ce chemin n’appelle aucune API de document Revit ; la capture du contexte reste dans `Idling`. Les permutations sont regroupées sans dessin intermédiaire, les éditeurs actifs restent protégés et l’ordre d’origine est conservé quand le contrôle natif est remplacé. Le cache peut retrouver un nouvel objet natif derrière le même HWND après vérification du RTTI.

Les sources actuelles de la sonde compilent avec les API 2023 à 2027 et le plugin complet compile en Release2024. Le [compte rendu Revit 2024](evidence/native-stable-layout-2024.json) documente les reconstructions après modification du niveau de détail par transaction dans le modèle jetable et la saisie native de l’échelle 1:100 → 1:200. Les captures confirment les états affichés ; elles ne constituent pas un enregistrement image par image d’un éventuel scintillement. Le rebinding du cache derrière le même HWND a été ajouté après le chargement de cette sonde et validé par compilation, pas par ce test natif.
