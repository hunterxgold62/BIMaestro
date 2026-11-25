---
name: bimaestro-motion
description: Crée des vidéos courtes de motion design pour les réseaux (TikTok, Reels, LinkedIn, 9:16) qui mettent en valeur une fonction du plugin BIMaestro, dans le style validé par Paul (fond blanc, vert en accent, cartes, balayage vert, rideau, configurateur). Couvre le tournage automatique dans Revit 2026 (mode démo), le montage HyperFrames et le son. Utiliser dès que l'utilisateur demande une vidéo dynamique, un reel, un teaser, une vidéo réseaux ou « motion design » pour BIMaestro, ou de nouvelles variantes de ces vidéos. Pour un tutoriel pas à pas destiné au site, utiliser plutôt tutoriel-video.
---

# Vidéos motion design BIMaestro

Chaque vidéo est **nouvelle** : on réutilise la charte, le vocabulaire de transitions et la chaîne de tournage, pas un gabarit. Choisis la structure qui sert la fonction filmée, puis pioche dans les briques.

Charge d'abord `/hyperframes` (contrat de rendu, work loop) ; ce skill ajoute le style BIMaestro par-dessus.

## Charte validée

- **Fond** : blanc `#FFFFFF` + trame de points `radial-gradient(#E5E7EB 2px, transparent 2px)` 40 px, opacité 0,7. Jamais de fond vert plein (rejeté).
- **Vert BIMaestro** `#0F5132` (pastilles, liserés, ligne de balayage, rideau), **menthe** `#7FD6A6` (surlignage, lueur, coche). Texte `#111827`, secondaire `#6B7280`, bordures `#E5E7EB`/`#D1D5DB`.
- **Police** : Inter 900 pour les titres (letter-spacing −0,03 à −0,05 em), 700–800 pour le reste.
- **Format selon la plateforme — toujours le dire à Paul avant publication** : TikTok / Reels / Shorts = 1080×1920 (9:16) ; **LinkedIn = 1080×1350 (4:5) ou 1080×1080, jamais 9:16** (LinkedIn ouvre le vertical en plein écran et masque le texte du post — post raté et supprimé le 2026-11-19). 30 i/s. En 9:16 : Texte et produit entre 168 et 1330 px en hauteur, marge droite ≥ 180 px (rail TikTok).
- **Fin** : logo (`motion-design/brand/BIMaestro invisible.png`) dans une carte blanche arrondie + « BIMaestro » + pastille verte « Le plugin Revit qui bosse pour toi ».

## Ce qui a plu / ce qui a été rejeté

- ✅ Une **même arborescence cadrée** dont seul le contenu change (« la transition parfaite »).
- ✅ **Balayage par ligne verte** : la nouvelle image se révèle de haut en bas derrière une ligne `#0F5132` 8 px avec lueur menthe.
- ✅ **Cartes qui glissent** (entrée x + légère rotation, `expo.out`).
- ✅ **Rideau vert** plein écran qui monte avec un nom de chapitre puis repart vers le haut.
- ✅ **Configurateur** : boutons-pastilles cochés par un curseur animé, l'arborescence réelle suit.
- ✅ **Battle** : Revit figé à gauche, Revit + BIMaestro à droite, « VS ».
- ✅ Montrer **plusieurs looks** en fin de vidéo (« Ou encore… »).
- ✅ **Garder l'en-tête natif de la fenêtre Revit** filmée (ex. palette Propriétés : « Propriétés », « Mur de base », « Murs (1) », « Modifier le type »), même s'il ne change pas : c'est ce qui fait reconnaître l'outil à tout utilisateur Revit au premier coup d'œil. Ne recadrer que le bas (nom du projet).
- ✅ Pour LinkedIn, **« Couche par couche »** (configurateur où chaque clic ajoute une couche) explique mieux qu'un défilé de looks ; le défilé convient mieux au 9:16.
- ❌ Fond vert, punch-in/zooms brusques, coupes sèches sans transition.
- ❌ **Trop rapide** : à 0,8 s par look on croit voir des photos. Viser **1,5 s par look**, et privilégier les fonds animés (constellation, rubans, lucioles, aurore, dégradé animé) pour qu'on voie bouger.
- ❌ Une arborescence entièrement en texte rouge (look « Verre dépoli » avec Texte #BE123C) : illisible et agressive. Garder un texte sombre ou clair neutre, la couleur va sur les dossiers.
- ❌ Inventer le fonctionnement d'un outil : toujours partir d'images réelles de Revit.

## Rythme

- Accroche lisible dès l'image 0 (pas de logo, pas de noir), 1,8–2,2 s.
- Un changement visible toutes les 1,5–2,5 s ; balayage 0,25–0,35 s.
- Durée totale 15–25 s. Texte d'un plan ≥ 1,2 s à l'écran.
- Son : clic sur chaque balayage/clic, swoosh sur les entrées/sorties de carte, pop sur titres et logo (effets dans `motion-design/*/assets/sfx`), volumes clic 0,8, swoosh 0,7, pop 0,45.
- **Pas de musique** (décision de Paul après plusieurs essais, électro puis douce : « pas dingue »). Uniquement les bruitages. Ne pas en proposer sauf demande explicite ; des pistes Kevin MacLeod CC BY 4.0 restent dans `motion-design/brand/musique/` si besoin un jour (créditer l'auteur).
- **Arborescence longue dès le début** : carte de 620×1250 (du haut jusqu'à ~1680 px), pour montrer un maximum de lignes, idéalement avec un dossier déplié.

## Briques et tournage

- Briques réutilisables (balayage, cartes, rideau, configurateur, battle, accroche, fin) : `references/briques.md`.
- Tournage automatique dans Revit 2026 (mode démo, défilé de variantes, cadrage) et pièges connus : `references/tournage.md`.
- Exemples validés, chacun avec un `gen.mjs` qui génère la composition depuis une liste de plans : `motion-design/defile-fonds`, `battle`, `configurateur`, `config-plus`, `metiers`, `apparence-v2`.

## Méthode

1. Lis le code de la fonction pour connaître ses vrais réglages (noms exacts, fichiers de préférences).
2. Propose 2–3 concepts différents (structure, transitions, message) avant de tourner si la demande est ouverte.
3. Tourne automatiquement si la fonction est pilotable par fichier (voir `tournage.md`), sinon donne un guide de tournage à Paul.
4. Écris un `gen.mjs` propre au projet (liste de plans → HTML), jamais de timings écrits à la main dans 40 tweens.
5. `check`, puis `snapshot` aux moments clés **et regarde chaque plan** : un look peut sortir vide si Revit a tardé à l'appliquer.
6. Rends, envoie le MP4, liste ce qui manque (musique, plans imparfaits).
7. Données : projet de démo uniquement ; ne jamais garder une capture d'écran qui montre autre chose que Revit (messagerie, projets clients) — la supprimer aussitôt.
