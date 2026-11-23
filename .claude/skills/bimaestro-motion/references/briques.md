# Briques visuelles BIMaestro

Toutes respectent le contrat HyperFrames : `fromTo` dans les scènes, pas de transform CSS sur un élément animé par GSAP (utiliser `tl.set` au temps 0), vidéos `muted playsinline` avec `data-media-start`.

## Carte d'arborescence

Rush 476 px de large, affiché à 620 px de large (échelle ≈ 1,30) et rogné en hauteur pour garder les dossiers lisibles sur téléphone :

```css
#card { position:absolute; left:330px; top:260px; width:620px; height:1000px; border-radius:28px; overflow:hidden;
        background:#fff; box-shadow:0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
.v    { position:absolute; left:0; top:0; width:620px; height:1469px; object-fit:fill; }
#bar  { position:absolute; left:0; top:0; bottom:0; width:10px; background:#0F5132; z-index:51; }  /* liseré gauche */
```

Libellé à gauche de la carte (colonne 330 px) : pastille numéro (`#0F5132`, blanc, 28–30 px) + nom en 44–50 px 900.

## Balayage vert (transition signature)

Chaque nouveau plan est une `<video>` empilée (z-index croissant) qui commence `W` secondes avant son affichage complet :

```js
tl.fromTo("#v3", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: W, ease: "power3.inOut" }, t);
tl.fromTo("#line", { y: 0, opacity: 1 }, { y: CARD_H, opacity: 1, duration: W, ease: "power3.inOut", immediateRender: false }, t);
tl.to("#line", { opacity: 0, duration: 0.03 }, t + W);
```
```css
#line { position:absolute; left:0; right:0; top:0; height:8px; background:#0F5132; box-shadow:0 0 24px #7FD6A6; z-index:50; opacity:0; }
```
Variante horizontale (finale de la Battle) : `clip-path: inset(0% 100% 0% 0%)` → `inset(0)` avec une ligne verticale qui suit `x`.

## Cartes qui glissent

Entrée `{ x: 900, rotation: 6 } → { x: 0, rotation: 0 }` 0,5 s `expo.out` ; sortie `x: -1100, rotation: -6` 0,4 s `expo.in`. Pour enchaîner deux cartes, la sortie de l'une chevauche l'entrée de l'autre de 0,1–0,2 s.

## Rideau de chapitre (vidéo Métiers)

Div plein écran `#0F5132` z-index 80 ; nom du chapitre blanc 100 px 900 au centre.
`yPercent 100 → 0` (0,3 s), nom qui monte (0,25 s), nom qui sort, `yPercent 0 → -100` (0,3 s), puis `tl.set(yPercent:100)`. La nouvelle vidéo démarre à mi-rideau, cachée derrière.

## Configurateur (vidéo Configurateur)

Pastilles 270×76, bord `#D1D5DB`, case 30 px. Au clic : pastille → `#0F5132`/blanc, case → menthe avec petit rebond, onde (cercle 80 px bord vert, scale 0,3 → 1,8, opacité → 0). Curseur SVG flèche blanche bord `#111827`, déplacements 0,45 s `power3.inOut`, garé hors champ (`x:1200,y:1500`) au repos. Réglages **cumulatifs** : chaque clic ajoute un effet au précédent.

## Battle

Deux cartes 460×900 côte à côte (x 60 et 560), pastilles « Revit » (gris) / « Revit + BIMaestro » (vert), rond « VS » noir au centre. Côté Revit = image fixe (Revit de base ne bouge pas, c'est voulu) ; côté BIMaestro = looks animés en balayage toutes les 1,5 s. Finale : le côté Revit bascule aussi.

## Accroche et fin

- Accroche : deux lignes 90–120 px 900, la 2ᵉ en vert avec un surlignage menthe qui se dessine (`scaleX 0→1`, `transform-origin: 0 50%`, `z-index:-1`). Sortie en glissé.
- « Ou encore… » : pastille verte qui remplace les commandes avant un défilé de looks, nom du look au-dessus de la carte.
- Fin : `outro.html` des projets existants (logo, nom, pastille slogan).

## Générateur

Chaque projet a un `gen.mjs` : constantes de rythme (`W`, `STEP`, `FIRST`), liste des plans `[nom, durée, étiquette, rush, n° de variante]`, et il écrit `index.html` + `compositions/reel.html` + les `<audio>` d'effets. Changer l'ordre ou les durées = modifier la liste et relancer `node gen.mjs`.
