# Essai R2 : Projet_AntibesV2 · {3D}

Ce pilote copie uniquement la révision 2 de la publication `5b1d1220-2b0c-4194-8b4e-ea18db63f73d` dans le bucket privé `bimaestro-maquettes-test`. Il ne modifie ni les fichiers Supabase d'origine, ni `mep_publications.scenario_state` (réservations, notes et lots). Le site utilise R2 seulement avec `?r2pilot=1` sur cette publication. Sans ce paramètre, tous les liens utilisent Supabase.

Le Worker vérifie le jeton du lien auprès de la fonction Supabase existante, limite la lecture aux fichiers déclarés dans le manifeste actif et refuse les objets absents ou de taille incorrecte. Le navigateur vérifie également le SHA-256 des zones téléchargées.

## Contrôles effectués le 23 septembre 2026

- Copie des 15 fichiers (16 633 642 octets), vérifiés par taille et SHA-256 avant chaque envoi.
- Lecture réelle de l'index et d'une zone via le Worker R2 : taille et SHA-256 identiques au manifeste.
- Chargement des 14 zones dans le viewer local puis sur `viewer.bimaestro.fr` avec `?r2pilot=1`.
- Trois étiquettes de réservation visibles ; la fiche d'une réservation s'ouvre.
- État Supabase avant/après : révision maquette 2, révision scénario 6, trois annotations, empreinte `336f97fb4a5a2609b1f816b3df3f8da3` inchangée.
- 57 tests du viewer et deux tests du Worker réussis ; build du viewer réussi.

## Limite du pilote

Le Worker utilise encore l'action Supabase `resolve` pour chaque zone afin de vérifier l'accès. Cette action réserve du budget de téléchargement dans le compteur interne du viewer même lorsque les octets sont servis par R2. Le pilote permet de valider la conservation des données et le chargement R2 ; une version destinée à tous les liens devra remplacer cette vérification répétée, gérer les nouvelles publications et prévoir le retour au parcours Supabase si R2 est indisponible.

Le script `copy-pilot.ps1` attend un jeton de partage fourni à l'exécution ; aucun jeton de partage n'est conservé dans le dépôt.
