-- Read-only analytics, restricted to administrative access.
create view public.v_button_reuse_user_activity with (security_invoker = true) as
with eligible as (
  select *, coalesce(nullif(context->>'usage_id',''),id::text) as usage_key
  from public.plugin_usage_events
  where button_id <> 'Plugin.Startup'
    and button_id not like 'IA.%' and button_id not like 'Tutorial.%'
    and coalesce(context->>'event_kind','') <> 'operation_result'
), events as (
  select distinct on (license_key,machine_id_hash,usage_key) *
  from eligible order by license_key,machine_id_hash,usage_key,created_at,id
), identities as (
  select license_key,machine_id_hash,
    (array_agg(nullif(trim(revit_username),'') order by created_at desc,id desc)
      filter (where nullif(trim(revit_username),'') is not null))[1] as revit_name
  from events group by 1,2
), identified as (
  select e.*,
    coalesce(lower(i.revit_name),md5(jsonb_build_array(e.license_key,e.machine_id_hash)::text)) as user_id,
    coalesce(lower(i.revit_name) = 'plemert6dt5b',false) as is_developer,
    (e.created_at at time zone 'Europe/Paris')::date as usage_day
  from events e join identities i on i.license_key is not distinct from e.license_key
    and i.machine_id_hash is not distinct from e.machine_id_hash
)
select plugin_version,button_id,user_id,is_developer,
  min(created_at) as first_use_at,max(created_at) as last_use_at,
  min(usage_day) as first_use_day,count(*) as launches,
  count(distinct usage_day) as active_days,
  count(distinct usage_day) filter (
    where usage_day between (now() at time zone 'Europe/Paris')::date - 29
      and (now() at time zone 'Europe/Paris')::date
  ) as active_days_last_30
from identified group by plugin_version,button_id,user_id,is_developer;

create view public.v_button_reuse_stats with (security_invoker = true) as
select plugin_version,button_id,
  count(*) filter(where not is_developer) as users_tried,
  count(*) filter(where not is_developer and active_days >= 2) as users_returned,
  round(100.0 * count(*) filter(where not is_developer and active_days >= 2)
    / nullif(count(*) filter(where not is_developer),0),1) as reuse_percent,
  count(*) filter(where not is_developer and active_days_last_30 >= 3) as regular_users_last_30,
  count(*) filter(where not is_developer and first_use_day = (now() at time zone 'Europe/Paris')::date) as new_users_today,
  coalesce(sum(launches) filter(where not is_developer),0) as launches,
  coalesce(sum(launches) filter(where is_developer),0) as developer_launches
from public.v_button_reuse_user_activity
group by plugin_version,button_id;

revoke all on public.v_button_reuse_user_activity,public.v_button_reuse_stats from public,anon,authenticated;
grant select on public.v_button_reuse_user_activity,public.v_button_reuse_stats to service_role;
