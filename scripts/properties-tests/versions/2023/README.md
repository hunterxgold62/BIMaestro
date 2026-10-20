# Revit 2023 — exploration complémentaire

Sonde autonome `2023advance-v3.dll`, classe `BIMaestro.PropertiesTests.Experiment`, modes `inspect`, `style`, `typography`, `compact`, `restore`.

## Mesuré en instance native
- Revit 2023 lancé avec le harnais `2023-round2`, projet jetable `PropertiesFixture.rvt`.
- MSAA `AccessibleObjectFromWindow` et `AccessibleChildren` parcourus sur GXWND et GXEDIT : pas d'arbre de paramètres, pas de noms de lignes accessibles. Seuls le client, l'éditeur courant et les barres de défilement sont exposés. L'éditeur courant fournit la valeur `1 : 100` sans le nom de propriété. Cela ne suffit pas à construire une recherche de toutes les lignes.
- Mode `style` exécuté : 24 valeurs WPF sauvegardées et 2 hooks GXWND, dont le second correspond à une grille non visible. Les couleurs du sélecteur et le contour sont réversibles.
- Empreinte SHA256 des 89 valeurs de paramètres de vue/murs identique entre inspect et style : `6C8FA7E3B8C6FFE525E9A0A976EC569DD339556E5D3F1B46A09224733885AA25`.
- Les variantes typographiques compilées concernent seulement le sélecteur WPF, pas la grille de cellules. Elles sont en attente d'essai visuel.

Rapports bruts : `tmp/properties-tests/2023-advance/inspect-v2.txt`, `inspect-v3.txt`. Ne jamais présenter une compilation comme une validation visuelle.

## Variantes exécutées
- compact : 8 propriétés WPF enregistrées (taille 11, padding 2), restauration à 0.
- typography : 4 propriétés WPF enregistrées (taille 16), prêt pour capture visuelle.
- Empreinte des paramètres identique sur toute la séquence ; rapports compact.txt, typography.txt et restored1/2.txt sous 2023-advance.
- L'énumération MSAA complète des véritables enfants confirme uniquement les composants Windows (barres, éditeur), sans liste de propriétés. Aucun repère de groupe fiable n'a donc été dessiné sur les cellules.

## Fin de session
Restauration finale exécutée avec la même DLL v3 ; 0 hook natif et 0 propriété WPF sauvegardée restante. Empreinte finale inchangée : 6C8FA7E3B8C6FFE525E9A0A976EC569DD339556E5D3F1B46A09224733885AA25 (89 valeurs). La capture de typographie a été prise par l'agent principal ; une partie des images peut être recouverte par les palettes des autres processus, ne pas les interpréter comme une validation exhaustive du rendu.
