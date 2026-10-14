# Réutilisation des boutons

La vue administrative `public.v_button_reuse_stats` fournit un tableau par version et libellé de bouton. Aucun changement du plugin n'est nécessaire.

```sql
select button_id as "Bouton",
  users_tried as "Utilisateurs ayant essayé",
  users_returned as "Revenus un autre jour",
  reuse_percent as "Taux de réutilisation (%)",
  regular_users_last_30 as "Réguliers sur 30 jours",
  new_users_today as "Premiers essais aujourd’hui"
from public.v_button_reuse_stats
where plugin_version = '1.0.6.4' and users_tried > 0
order by users_tried desc, button_id;
```

- Essayé : au moins un lancement enregistré, annulations et erreurs incluses.
- Revenu : lancement sur au moins deux dates différentes, dans la même version.
- Taux : revenus / ayant essayé, sur toute la période disponible de cette version. Ce n'est pas une rétention à sept jours ; les utilisateurs récents ont moins de recul.
- Régulier : utilisation sur au moins trois dates dans les 30 derniers jours calendaires, aujourd'hui inclus, dans cette version.
- Les dates utilisent Europe/Paris.
- Les démarrages, événements IA techniques, événements de tutoriel et résultats d'opération sont exclus. Les lancements de boutons de tutoriel restent inclus.
- Les événements portant le même usage_id sur une installation sont dédupliqués.
- Les installations sont regroupées selon leur dernier identifiant Revit non vide parmi les lancements. Sans identifiant, le couple licence/machine est utilisé. Un compte partagé ou un changement de compte limite cette estimation des personnes.
- Les usages de Paul (`plemert6DT5B`) sont séparés dans developer_launches, exclus des indicateurs publics. Ce filtre ne détecte pas automatiquement d'autres comptes de test.
- Les boutons sont regroupés par libellé historique. Une traduction ou un renommage peut donc diviser les statistiques ; une table de correspondance sera nécessaire pour unifier ces cas.
- Les vues sont security_invoker et accessibles au service_role et aux administrateurs, sans accès anon/authenticated. Ne pas exposer une clé service_role dans un client.

`public.v_button_reuse_user_activity` contient la synthèse par utilisateur et bouton utilisée pour calculer le tableau. Les vues sont recalculées à la lecture ; le tableau évolue avec les données reçues.
