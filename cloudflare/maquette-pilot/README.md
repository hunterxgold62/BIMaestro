# Stockage R2 des maquettes MEP

Le Worker `bimaestro-maquette-pilot` sert les fichiers depuis le bucket privé `bimaestro-maquettes`. Le nom historique du Worker est conservé pour ne pas changer son URL. Supabase garde les publications, les liens, les annotations, les réservations, les lots et les notes.

## État au 23 septembre 2026

- AntibesV2, révision 2 : 15 fichiers, 16 633 642 octets.
- SIGMA 20260729 · 3D Travail, révision 4 : 42 fichiers, 15 520 696 octets.
- Les 57 objets ont été relus depuis R2 et comparés au SHA-256 du manifeste Supabase.
- Le viewer public charge les liens habituels depuis R2, sans paramètre `r2pilot=1`. Test dans Chrome : 15/15 objets AntibesV2 et 42/42 objets SIGMA, aucun fichier de maquette lu depuis Supabase Storage, aucune erreur de page. SIGMA affichait 75 annotations et ses lots.
- Une lecture de l'index SIGMA depuis le Worker n'a pas changé `reserved_download_bytes` dans Supabase.
- Les originaux Supabase des deux révisions sont conservés. Les quatre autres publications de test et leurs fichiers Supabase ont été supprimés à la demande de l'utilisateur.

## Prochaines publications

`GameMepPublishClient` demande `storageBackend = "r2"`. La fonction Supabase crée le brouillon et garde le scénario collaboratif ; le Worker valide le JWT de licence auprès de Supabase, contrôle taille et SHA-256, puis écrit chaque fichier dans R2. Avant d'activer une révision, Supabase demande au Worker de vérifier tous les objets. Une révision incomplète ne devient pas active.

Le code serveur et le Worker sont déployés. La DLL Revit a été compilée en Release, mais son installation locale attend la fermeture de Revit par l'utilisateur. L'ancienne DLL continue de publier vers Supabase ; cela évite de casser une publication pendant la transition. Une publication réelle avec la nouvelle DLL reste à vérifier avant de considérer ce parcours entièrement validé.

`copy-publication.ps1` et `verify-publication.ps1` servent à copier puis à relire une révision existante. Ne pas supprimer les originaux Supabase des deux maquettes sans contrôle explicite de la mise à jour suivante et de ses annotations.
