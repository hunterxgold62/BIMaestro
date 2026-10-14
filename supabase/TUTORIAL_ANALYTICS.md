# Statistiques des tutoriels

Dans Supabase > Database > Tables / Views, consulter :

- `v_tutorial_user_progress` : tutos distincts commencés et terminés, nombre disponible, pourcentage et dernière activité.
- `v_tutorial_stats` : utilisateurs distincts, tentatives commencées et terminées, taux de complétion, sorties explicites et étape de sortie fréquente.

Les vues sont réservées aux administrateurs et au rôle serveur. Le client conserve le collecteur existant : les événements `Tutorial.Event` sont enregistrés dans `plugin_usage_events.context`. Aucun nouveau service à déployer.

`user_id` est un pseudonyme calculé à partir de la licence et du poste. Il représente une installation sous licence, pas une personne identifiable sur un poste partagé. Les utilisateurs ayant lancé la nouvelle version apparaissent même avec zéro tutoriel effectué grâce à `Tutorial.Catalog`. Les anciennes versions ne fournissent pas cette information.

Chaque tentative porte un UUID, chaque événement un UUID distinct. Les doublons d'envoi sont éliminés dans `v_tutorial_events`. Un retour à une étape antérieure met à jour la dernière étape réellement affichée. Les passages de fenêtre au ruban et les exercices avec plusieurs fenêtres conservent la tentative. Une sortie via Quitter est explicite ; une fermeture de Revit est interrompue ; sans événement terminal après 24 heures la tentative est inactive, sans présumer un abandon volontaire. La fin est enregistrée à la confirmation de réussite du guide. Couleurs exige la dernière étape avant l'enregistrement.

La progression compte chaque tuto terminé une seule fois. Le taux de complétion compte toutes les tentatives, y compris celles encore en cours ; il peut donc évoluer. L'étape de sortie fréquente utilise uniquement les sorties explicites. Aucune durée de lecture ou validation d'étape individuelle n'est revendiquée par ces vues.

Le catalogue `1` contient dix tutoriels. Les nouvelles disponibilités doivent recevoir une nouvelle version de catalogue côté client et des lignes correspondantes en base, sans modifier les anciennes révisions. Les résultats sont séparés par version de catalogue.

Vérification SQL : `supabase/sql/verify_tutorial_analytics.sql` utilise des événements fictifs dans une transaction annulée. Il vérifie les reprises, les doublons, les étapes de sortie, le catalogue et les droits.

La collecte nécessite la diffusion du plugin recompilé. Aucun historique précis de tutoriels ne peut être reconstitué pour les versions antérieures.
