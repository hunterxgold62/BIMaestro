-- Unified token/quota dashboard.
-- v_tokens_vs_quota is the reference view.
-- The older detail/global views are kept as compatibility wrappers.

create or replace view public.v_tokens_vs_quota
with (security_invoker = true)
as
with usage_machine as (
  select
    license_key,
    nullif(trim(machine_id), '') as machine_id,
    sum(tokens_used) as tokens_machine,
    max(created_at) as derniere_activite_machine
  from public.api_usage
  group by license_key, nullif(trim(machine_id), '')
),
usage_global as (
  select
    license_key,
    sum(tokens_used) as tokens_global,
    max(created_at) as derniere_activite_global
  from public.api_usage
  group by license_key
),
quota_machine as (
  select
    license_key,
    nullif(trim(machine_id), '') as machine_id,
    max(token_limit) as token_limit
  from public.api_quotas
  where nullif(trim(machine_id), '') is not null
  group by license_key, nullif(trim(machine_id), '')
),
quota_global_explicit as (
  select
    license_key,
    max(token_limit) as token_limit
  from public.api_quotas
  where nullif(trim(machine_id), '') is null
  group by license_key
),
quota_machine_sum as (
  select
    license_key,
    least(sum(token_limit), 2147483647)::integer as token_limit
  from public.api_quotas
  where nullif(trim(machine_id), '') is not null
  group by license_key
),
license_keys as (
  select license_key from usage_global
  union
  select license_key from quota_machine
  union
  select license_key from quota_global_explicit
),
global_limits as (
  select
    lk.license_key,
    coalesce(qg.token_limit, qms.token_limit, 200000) as global_token_limit,
    case
      when qg.token_limit is not null then 'api_quotas global'
      when qms.token_limit is not null then 'somme quotas machines'
      else 'defaut 200000'
    end as global_limit_source
  from license_keys lk
  left join quota_global_explicit qg on qg.license_key = lk.license_key
  left join quota_machine_sum qms on qms.license_key = lk.license_key
),
machine_keys as (
  select license_key, machine_id
  from usage_machine
  where machine_id is not null
  union
  select license_key, machine_id
  from quota_machine
  where machine_id is not null
),
combined as (
  select
    'GLOBAL'::text as "Ligne",
    lk.license_key as "Licence",
    '(global)'::text as "Machine",
    coalesce(ug.tokens_global, 0::bigint) as "Tokens utilises",
    gl.global_token_limit as "Limite tokens",
    greatest(gl.global_token_limit::bigint - coalesce(ug.tokens_global, 0::bigint), 0::bigint) as "Reste tokens",
    coalesce(ug.tokens_global, 0::bigint) >= gl.global_token_limit::bigint as "Au plafond ?",
    coalesce(ug.tokens_global, 0::bigint) as "Tokens utilises global licence",
    gl.global_token_limit as "Limite globale licence",
    greatest(gl.global_token_limit::bigint - coalesce(ug.tokens_global, 0::bigint), 0::bigint) as "Reste global licence",
    false as "Limite machine specifique ?",
    gl.global_limit_source as "Source limite globale",
    ug.derniere_activite_global as "Derniere activite"
  from license_keys lk
  left join usage_global ug on ug.license_key = lk.license_key
  join global_limits gl on gl.license_key = lk.license_key

  union all

  select
    'MACHINE'::text as "Ligne",
    mk.license_key as "Licence",
    mk.machine_id as "Machine",
    coalesce(um.tokens_machine, 0::bigint) as "Tokens utilises",
    coalesce(qm.token_limit, gl.global_token_limit, 200000) as "Limite tokens",
    greatest(coalesce(qm.token_limit, gl.global_token_limit, 200000)::bigint - coalesce(um.tokens_machine, 0::bigint), 0::bigint) as "Reste tokens",
    coalesce(um.tokens_machine, 0::bigint) >= coalesce(qm.token_limit, gl.global_token_limit, 200000)::bigint as "Au plafond ?",
    coalesce(ug.tokens_global, 0::bigint) as "Tokens utilises global licence",
    coalesce(gl.global_token_limit, 200000) as "Limite globale licence",
    greatest(coalesce(gl.global_token_limit, 200000)::bigint - coalesce(ug.tokens_global, 0::bigint), 0::bigint) as "Reste global licence",
    qm.token_limit is not null as "Limite machine specifique ?",
    coalesce(gl.global_limit_source, 'defaut 200000') as "Source limite globale",
    coalesce(um.derniere_activite_machine, ug.derniere_activite_global) as "Derniere activite"
  from machine_keys mk
  left join usage_machine um on um.license_key = mk.license_key and um.machine_id = mk.machine_id
  left join quota_machine qm on qm.license_key = mk.license_key and qm.machine_id = mk.machine_id
  left join usage_global ug on ug.license_key = mk.license_key
  left join global_limits gl on gl.license_key = mk.license_key
)
select *
from combined
order by "Licence", case "Ligne" when 'GLOBAL' then 0 else 1 end, "Tokens utilises" desc;

create or replace view public.v_tokens_vs_quota_detail
with (security_invoker = true)
as
select
  "Licence",
  "Machine",
  "Tokens utilises" as "Tokens utilises (utilisateur)",
  "Tokens utilises global licence" as "Tokens utilises (global licence)",
  "Limite tokens" as "Limite appliquee (utilisateur)",
  "Limite globale licence" as "Limite globale (licence)",
  "Reste tokens" as "Reste (utilisateur)",
  "Reste global licence" as "Reste (global licence)",
  "Au plafond ?" as "Utilisateur au plafond ?",
  "Tokens utilises global licence" >= "Limite globale licence" as "Licence au plafond ?",
  "Derniere activite" as "Derniere activite"
from public.v_tokens_vs_quota
where "Ligne" = 'MACHINE'
order by "Licence", "Au plafond ?" desc, "Reste tokens";

create or replace view public.v_tokens_vs_quota_global
with (security_invoker = true)
as
select
  "Licence",
  "Tokens utilises global licence" as "Tokens utilises (global licence)",
  "Limite globale licence" as "Limite globale (licence)",
  "Reste global licence" as "Reste (global licence)",
  "Derniere activite" as "Derniere activite"
from public.v_tokens_vs_quota
where "Ligne" = 'GLOBAL'
order by "Tokens utilises global licence" desc;

