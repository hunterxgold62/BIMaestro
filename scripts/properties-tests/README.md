# Exploration de la palette Propriétés native

Branche `codex/properties-palette`. État documenté le 20 octobre 2026 ; les dates exactes des essais figurent dans les journaux. Ce dossier contient un protocole expérimental, pas une fonctionnalité de production ni un bouton permanent. Le bouton sera traité ultérieurement.

## Résultats établis

- Les sondes et la version finale de `Experiment.cs` compilent pour Revit 2023, 2024, 2025 et 2026.
- Les essais dans Revit natif ont été réalisés sur **2024.3 et 2025.5**, dans des projets jetables `PropertiesFixture` contenant un mur. La compilation de 2023 et 2026 ne vaut pas validation de leur comportement à l’exécution.
- La grille de propriétés est un contrôle natif MFC **GXWND**, avec éditeur **GXEDIT**, de la famille Objective Grid. La barre `TypeSelectorToolBarControl` est WPF.
- Le type managé `ManagePropertiesPalette` inspecté ne présente qu’un membre d’alignement Int64 ; aucun pont managé vers les lignes de la grille n’a été trouvé.
- Le prototype **v3** modifie les fonds, textes et bordures du sélecteur de type WPF, les titres ciblés de la palette et un contour de 1 px du GXWND ancré par `SetWindowSubclass`/`WM_PAINT`. Il ne redessine jamais le contenu des cellules.
- Les séquences inspecter → appliquer → restaurer → appliquer ont été vérifiées en 2024 et 2025. Les empreintes des paramètres de la vue active et des murs restent identiques pendant ces séquences : voir [evidence/runtime-summary.json](evidence/runtime-summary.json).

Limite observée : le contour v3 est validé en 2025 sur palette ancrée (deux hooks). Le cadre natif avait été testé avec v2 en 2024 avant détachement. Pendant la séquence finale v3 en 2024, la palette flottante produit zéro hook : le parcours des enfants de la fenêtre principale exclut cette palette détachée. Le contour v3 ne s’applique donc pas dans cette configuration. Le sélecteur WPF reste modifié et restaurable dans les deux versions, avec 27 propriétés de dépendance enregistrées par version. Les captures finales montrent les deux sélecteurs recolorés ; elles ne prouvent pas le fonctionnement du contour dans toutes les positions.

Le ciblage reste local : sélecteur de type, titres de palette français/anglais et fenêtres natives reconnues dans les versions testées. Les modifications WPF et les sous-classes natives doivent être restaurées avant de terminer une session de test.

## Possibilités et limites

La proposition **Lecture claire** repose sur ce qui a été obtenu : hiérarchie visuelle du sélecteur, contraste du titre et contour discret de la grille native. Les éditeurs de valeurs et leur comportement restent natifs.

Les couleurs par catégorie ou paramètre, la recherche, les favoris et le réarrangement des véritables lignes restent **non prouvés**. Objective Grid propose ces possibilités à l’application hôte via ses objets C++, mais aucun accès exploitable à ces objets depuis ce plugin Revit n’a été établi. Un HWND seul ne constitue pas une API de cellules. Aucun message de ListView, pointeur C++ déduit ou changement de binaire n’est requis par ce prototype.

Aucun changement du modèle n’est effectué par la phase d’apparence : ni valeurs, ni groupes, ni ordre enregistré des paramètres. Seule la commande explicite `fixture` crée les données du projet de test. Les empreintes vérifiées portent sur les éléments cités, pas sur une preuve exhaustive de tout le document. Les transitions complexes, le changement de DPI et tous les contextes de sélection ne sont pas encore couverts.

Les possibilités API documentées, distinctes des résultats runtime, figurent dans [API-NOTES.md](API-NOTES.md).

## Reproduire un essai contrôlé

Depuis la racine de ce worktree, pour chaque année souhaitée :

```powershell
./scripts/properties-tests/Build.ps1 -RevitVersion 2025 -OutputTag '-v4'
./scripts/properties-tests/BuildExperiment.ps1 -RevitVersion 2025 -Name 'experiment-v3'
```

1. Installer manuellement le manifeste temporaire généré pour la sonde, puis lancer une nouvelle instance de la version correspondante. Accepter manuellement le chargement si Revit le demande.
2. Après apparition du fichier `started.txt` dans le dossier de sortie de la sonde, retirer le manifeste temporaire afin de ne pas charger cette sonde aux prochains démarrages.
3. Utiliser de préférence un projet jetable. Déposer une commande dans `control.txt`, à côté de la DLL de la sonde : `fixture`, `inspect` ou `select-wall`. Attendre le résultat de chaque commande avant la suivante.
4. Pour exécuter le prototype, écrire `inspect`, `style` ou `restore` dans `experiment.txt`, dans ce même dossier ; écrire ensuite `run CHEMIN_ABSOLU_DE_LA_DLL_EXPERIMENT` dans `control.txt`.
5. Comparer les rapports, les captures et les empreintes avant/après. Exécuter impérativement `restore` avec **la même DLL déjà chargée** : son état mémoire conserve les valeurs et bindings à restaurer. Charger une autre DLL ne récupère pas cet état.

Les sorties de build et rapports sont sous `tmp/properties-tests/`. Le fichier de commande est consommé par la sonde ; ce mécanisme de laboratoire ne doit pas être distribué comme interface utilisateur de production.

## Sources primaires complémentaires

- [Perforce : GXWND et CGXGridWnd natif](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridWnd_5jvo.htm)
- [Perforce : couleurs par cellule via SetStyleRange et CGXStyle](https://help.perforce.com/stingray/2025.1/stingray_doc/Content/Objective_Grid_KB/html/ogfaq54.htm)
- [Perforce : FindText, composant facultatif de recherche](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_4d10.htm)
- [Perforce : masquage de lignes conditionné par ImplementHideCells](https://help.perforce.com/stingray/current/stingray_doc/Content/Stingray_Studio_API_Documentation/Grid/CGXGridCore_7lv7.htm)

Ces références décrivent la bibliothèque, pas une autorisation ou un contrat d’extension Autodesk de la grille intégrée.
## Environnement de compilation et fin des essais

Les versions 2025/2026 installées sur ce poste utilisent des correctifs .NET 10 ; les chemins du compilateur ciblent cet environnement local, et ne constituent pas une configuration SDK universelle. Les styles et hooks ont été retirés à la fin des essais ; les rapports restored et runtime-summary.json en conservent la vérification. Les captures montrent l’état appliqué avant restauration.
