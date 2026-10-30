---
name: tutoriel-video
description: Monte un tutoriel vidéo BIMaestro à partir d'une capture d'écran (.mp4) d'un bouton ou d'une fonction Revit — zooms progressifs, encadrés numérotés sur les boutons cliqués, ondes de clic, coupe et accélération des temps morts, titre, indicateur d'étape, barre de progression en bas et chapitres. Utiliser dès que l'utilisateur fournit une vidéo et demande un tutoriel, une démo, une vidéo pour un bouton, des zooms ou des encadrés.
---

# Tutoriel vidéo BIMaestro

Transforme une capture brute en tutoriel monté. Tout le rendu passe par `scripts/tutoriel.py`, piloté par un fichier de réglages JSON. Ton travail consiste à **remplir ce JSON avec des temps et des coordonnées exacts**. Le modèle complet et validé est `exemple-proprietes.json`.

Prérequis : `ffmpeg`, `ffprobe` et `python` dans le PATH (installés sur ce poste).

## Déroulé

1. **Sonder la vidéo** : durée, résolution, images par seconde.
   ```bash
   ffprobe -v error -show_entries format=duration:stream=width,height,r_frame_rate -of compact "<video>"
   ```
2. **Planche d'ensemble** : une image toutes les 3 s avec l'horodatage, pour comprendre le scénario.
   ```bash
   ffmpeg -v error -y -i "<video>" -vf "fps=1/3,scale=640:-1,drawtext=text='%{pts\:hms}':x=5:y=5:fontsize=24:fontcolor=yellow:box=1:boxcolor=black,tile=4x7" -frames:v 1 planche.jpg
   ```
3. **Repérer les instants d'action** grâce à la détection de changements d'écran. Chaque clic produit en général un pic.
   ```bash
   ffmpeg -hide_banner -i "<video>" -vf "fps=10,scale=640:-1,select='gt(scene,0.004)',showinfo" -an -f null - 2>&1 | grep -o "pts_time:[0-9.]*"
   ```
4. **Extraire des images clés en 1280 px** juste avant chaque pic. Lis-les pour trouver le curseur, le bouton visé et son rectangle `[x, y, l, h]` **en coordonnées 1280**. Pour une zone précise, recadre avec `crop` et ajoute le décalage du recadrage aux coordonnées.
   ```bash
   ffmpeg -v error -y -ss <t> -i "<video>" -frames:v 1 -vf scale=1280:-1 f_<t>.jpg
   ```
5. **Écrire le JSON** dans le dossier de sortie, par exemple `output/tutoriel-<sujet>/reglages.json`. Les champs sont décrits plus bas.
6. **Rendre avec contrôle**, puis lire `verification.jpg`.
   ```bash
   python .claude/skills/tutoriel-video/scripts/tutoriel.py output/tutoriel-<sujet>/reglages.json --verif
   ```
7. **Vérifier le calage clic par clic.** Le script affiche le début de chaque étape dans la vidéo montée. Extrais une image environ 0,15 s après quelques clics et vérifie que l'onde tombe sur le bon bouton, avec la bonne étiquette « n/N » en bas à gauche. Corrige le JSON et relance : le rendu prend environ 30 s.
8. **Envoyer la vidéo** à l'utilisateur et signaler tout contenu à vérifier avant diffusion (voir Pièges).

## Champs du JSON

Tous les temps sont ceux de la **vidéo d'origine**. Le script recalcule lui-même les temps après coupes et accélérations.

| Champ | Rôle |
|---|---|
| `source`, `output` | Vidéo brute, nom du fichier produit sans extension |
| `title`, `subtitle`, `end_text` | Titre d'ouverture, sous-titre, message de l'écran de fin |
| `ref_width` | Largeur des images sur lesquelles tu as relevé les coordonnées (1280) |
| `steps[]` | Une entrée par étape, dans l'ordre |
| `steps[].start` | Début de l'étape : indicateur, barre de progression et chapitre |
| `steps[].title` | Libellé de l'étape en bas et dans les chapitres |
| `steps[].label` | Texte de l'étiquette de l'encadré si différent du `title` (facultatif) |
| `steps[].box`, `box_time` | Rectangle du bouton `[x,y,l,h]` et fenêtre d'affichage `[début, fin]` (facultatifs) |
| `steps[].click` | Onde de clic `[x, y, t]` (facultatif) |
| `zooms[]` | `[début, fin, centre x, centre y, facteur]`, sans chevauchement, facteur 1,6 à 2,4 |
| `segments[]` | `[début, fin, vitesse]` à garder. **Ce qui n'est pas listé est coupé.** Vitesse > 1 = accéléré avec un badge « ▶▶ x… » |
| `intro`, `outro` | Durée du titre sur image figée et de l'écran de fin (2,5 s et 2,2 s) |

## Règles de montage qui ont plu

- **Couper** les attentes sans intérêt : chargement de fenêtre, curseur immobile, fin de capture après le résultat.
- **Accélérer de x2,5 à x3** les passages où le curseur cherche ou défile, plutôt que de les couper.
- **Garder à vitesse 1** chaque clic important, avec environ 1 s avant et après.
- **Un zoom par zone d'action.** Il démarre environ 0,4 s avant l'encadré et finit après le clic. Facteur 2 à 2,4 pour un bouton isolé, 1,6 quand il faut voir deux zones proches.
- **Encadré pendant 1,5 à 3 s** avant le clic. L'onde de clic tombe au pic de changement d'écran.
- **Terminer sur le résultat visible**, avant toute action étrangère au tutoriel.

## Pièges connus

- **Défilement de panneau** : si un panneau défile pendant l'encadré, celui-ci se retrouve sur le mauvais élément. Relève la position à plusieurs instants et limite `box_time` à la période où l'élément est immobile.
- **Image figée du titre** : ne pas utiliser `tpad` dans ce graphe, ffmpeg l'ignore. Le script utilise `loop` puis `setpts=N/FPS/TB`. Ne change pas cette partie.
- **Polices** : les avertissements « Fontconfig error » de ffmpeg sous Windows sont sans conséquence.
- **Confidentialité** : la capture peut montrer des noms de projets, de clients ou d'utilisateurs, ou des libellés personnels. Signale-les à l'utilisateur avant toute diffusion, et propose de refaire la prise ou de recadrer.
- **Variante simple** : `--basique` produit seulement les zooms, encadrés et ondes de clic, sans coupes ni habillage, dans `<output>-basique.mp4`.
