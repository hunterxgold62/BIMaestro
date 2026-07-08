# Supabase - BIMaestro

Ce dossier versionne la partie Supabase du projet BIMaestro : fonctions Edge, migrations SQL et notes d'exploitation.

## Depot public

Le code Supabase peut etre public tant qu'aucun secret n'est commite.

A ne jamais mettre dans Git :

- `SERVICE_ROLE_KEY` / `SUPABASE_SERVICE_ROLE_KEY`
- `JWT_SECRET`
- `OPENAI_KEY`
- `DEEPSEEK_KEY`
- `CRON_SECRET`
- fichiers `.env` reels

Le code TypeScript et les migrations SQL ne donnent pas, a eux seuls, le droit de modifier le projet Supabase. Les modifications en production necessitent un acces Supabase authentifie ou une cle secrete.

## Secrets attendus par les Edge Functions

- `validate` : `SUPABASE_URL`, `SERVICE_ROLE_KEY` ou `SUPABASE_SERVICE_ROLE_KEY`, `JWT_SECRET`, optionnellement `MAX_DEVICES`
- `collect-usage` : `SUPABASE_URL`, `SERVICE_ROLE_KEY`, `JWT_SECRET`
- `upsert-profile` : `SUPABASE_URL`, `SERVICE_ROLE_KEY`, `JWT_SECRET`
- `ai-proxy` : `SUPABASE_URL`, `SERVICE_ROLE_KEY` ou `SUPABASE_SERVICE_ROLE_KEY`, `JWT_SECRET`, `OPENAI_KEY`, `DEEPSEEK_KEY`
- `snake-leaderboard` : `SUPABASE_URL`, `SERVICE_ROLE_KEY` ou `SUPABASE_SERVICE_ROLE_KEY`, `JWT_SECRET`
- `refres_les_donn-es_d-analyse` : `SUPABASE_URL`, `SERVICE_ROLE_KEY`, `CRON_SECRET`

## Notes importantes

- `api_quotas` est maintenant administre par licence : une ligne par `license_key` avec `machine_id is null`.
- `api_usage` conserve toujours `machine_id` pour l'analyse detaillee et le support.
- `v_tokens_vs_quota` est la vue de reference. Les anciennes vues `v_tokens_vs_quota_detail` et `v_tokens_vs_quota_global` restent comme wrappers pour compatibilite.
- Les vues de quotas sont reservees aux roles admin/service. Elles ne doivent pas etre accessibles par `anon` ou `authenticated`.

