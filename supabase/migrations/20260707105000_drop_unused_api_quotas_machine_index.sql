-- api_quotas is now license-scoped. The machine_id lookup index is no longer
-- useful because quota enforcement reads machine_id is null.

drop index if exists public.ix_api_quotas_machine;

