-- Move quota administration to one row per license.
-- Existing machine quota limits are aggregated so no paid capacity is lost.

with aggregated as (
  select
    license_key,
    least(sum(coalesce(token_limit, 0))::bigint, 2147483647)::integer as token_limit
  from public.api_quotas
  where license_key is not null
    and nullif(trim(license_key), '') is not null
    and machine_id is not null
  group by license_key
),
updated as (
  update public.api_quotas q
  set token_limit = a.token_limit,
      updated_at = now()
  from aggregated a
  where q.license_key = a.license_key
    and q.machine_id is null
  returning q.license_key
)
insert into public.api_quotas (license_key, machine_id, token_limit, created_at, updated_at)
select a.license_key, null, a.token_limit, now(), now()
from aggregated a
where not exists (
  select 1 from updated u where u.license_key = a.license_key
)
and not exists (
  select 1
  from public.api_quotas q
  where q.license_key = a.license_key
    and q.machine_id is null
);

delete from public.api_quotas
where machine_id is not null;

