---
name: tutoriel-video
description: Prépare le tournage et monte des tutoriels vidéo BIMaestro pour présenter une commande ou une fonctionnalité Revit sur le site, à partir de captures d’écran vidéo, avec zooms et cadres animés.
---

# Tutoriels vidéo BIMaestro

Vise un MP4 de 60 secondes maximum pour le site, lisible sans son. La référence approuvée est la démonstration Accueil Revit avec cadres animés (`output/video-accueil-v5/BIMaestro-Accueil-Revit-cadres-animes.mp4` si elle est disponible dans le projet).

## Préparer et filmer

- Si la fonction est nouvelle, lis le code et vérifie le chemin réel dans le ruban. Prépare une courte liste des gestes et résultats à filmer avec les vrais libellés des commandes.
- Privilégie la capture filmée par Paul, dont le mouvement du curseur est plus naturel. En l’absence de prise, donne un guide de tournage. Pilote Revit si Paul demande un tournage autonome et que le poste permet la capture.
- Demande de filmer la vue entière de Revit avant le premier clic, des gestes nets avec une brève pause autour des clics, et les résultats réels. Pour une fonction avec plusieurs présentations, vise quatre ou cinq résultats distincts quand les prises le permettent.

## Monter dans le style validé

- Ouvre immédiatement sur un exemple concret du résultat. Reviens ensuite à l’interface générale, puis zoome progressivement vers la commande. Garde le même cadrage pendant le choix et la validation quand ils se font dans le même panneau.
- Coupe les attentes et les hésitations. Conserve les gestes décisifs à vitesse réelle. Garde la cadence source quand elle est adaptée, notamment 60 i/s pour les prises à 60 i/s.
- Utilise le vert BIMaestro `#103e32`, l’accent menthe `#9bdec4` et le doré `#efab32` pour guider l’œil. Fais apparaître le cadre autour du contrôle avant le geste, ajoute une onde courte au clic et un libellé d’étape si utile. Le texte du contrôle reste lisible. Retire le cadre avant un changement de plan, de cadrage ou de position de sa cible.
- Termine par les résultats disponibles, nommés et enchaînés avec des fondus courts. Pour un essai comparatif sur la prise de Paul, signale si tu complètes avec une autre source. Utilise un résultat unique quand la démonstration n’en montre qu’un.

## Rendre et vérifier

Sonde la source avec FFprobe. Examine une planche d’ensemble et des images autour des gestes pour relever les temps et les positions. FFmpeg permet de monter, recadrer et superposer les animations. Pour réutiliser le moteur existant, pars de [l’exemple de réglages](exemple-proprietes.json) et lance `python <dossier-du-skill>/scripts/tutoriel.py <reglages.json> --verif`. Renseigne les temps dans la chronologie source et adapte `source`, `output`, `fps` et `ref_width` au projet. Les `segments` sont les plages conservées et leur vitesse. `steps[].box` définit `[x,y,largeur,hauteur]` au `ref_width`, `box_time` définit sa fenêtre et `click` contient `[x,y,t]`. Adapte l’intro et la fin à la prise avec FFmpeg si le moteur ne produit pas la séquence voulue.

Exporte en 1920 × 1080, H.264, pixels carrés et `faststart`. Vérifie le décodage et la durée, puis inspecte les plans avant et après les transitions ainsi que les moments où un cadre ou une onde est visible. Masque les éléments hors démonstration qui ne doivent pas apparaître sur le site, notamment les noms de compte ou de projet lorsque nécessaire. Livre le fichier et indique les limites de la prise si elles influencent le résultat.

Après un retour explicite de Paul, consigne la préférence validée dans ce skill si elle s’applique aux prochaines vidéos. Garde les temps et coordonnées propres à une prise dans ses réglages.
