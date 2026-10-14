-- Operation summaries reuse the existing batch collector. Admin-only views.
create index plugin_usage_operation_results_idx on public.plugin_usage_events (created_at)
where context->'operation_result'->>'schema_version' = '1';

create view public.v_button_operation_results with (security_invoker = true) as
with events as (
  select distinct on (license_key, machine_id_hash, context->'operation_result'->>'operation_id')
    md5(jsonb_build_array(license_key,machine_id_hash)::text) as user_id,
    created_at,plugin_version,button_id,success as command_success,
    context->'operation_result' as result
  from public.plugin_usage_events
  where context->'operation_result'->>'schema_version' = '1'
    and context->'operation_result'->>'committed' = 'true'
    and context->'operation_result'->>'operation_id' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    and context->'operation_result'->>'tool_id' in ('auto_reservation','organizer')
  order by license_key,machine_id_hash,context->'operation_result'->>'operation_id',created_at,id
), metrics as (
  select e.*,m.key as metric,
    case when m.value ~ '^[0-9]{1,10}$' then m.value::bigint else 0 end as quantity
  from events e
  left join lateral jsonb_each_text(case when jsonb_typeof(e.result->'counts')='object'
    then e.result->'counts' else '{}'::jsonb end) m on true
)
select user_id,created_at,plugin_version,button_id,command_success,
  result->>'operation_id' as operation_id,result->>'tool_id' as tool_id,
  result->>'mode' as mode,coalesce(result->>'is_tutorial' = 'true',false) as is_tutorial,
  coalesce(sum(quantity) filter (where metric='reservations_created'),0)::bigint as reservations_created,
  coalesce(sum(quantity) filter (where metric='elements_renamed'),0)::bigint as elements_renamed,
  coalesce(sum(quantity) filter (where metric='elements_reset'),0)::bigint as elements_reset,
  coalesce(sum(quantity) filter (where metric='elements_unchanged'),0)::bigint as elements_unchanged,
  coalesce(sum(quantity) filter (where metric='elements_skipped'),0)::bigint as elements_skipped
from metrics group by user_id,created_at,plugin_version,button_id,command_success,result;

create view public.v_button_result_stats with (security_invoker = true) as
with tools(tool_id,title) as (values
  ('auto_reservation','Auto réservation'),('organizer','Organisateur')),
categories(is_tutorial) as (values (false),(true))
select t.tool_id,t.title,c.is_tutorial,
  count(distinct r.user_id) as distinct_users,
  count(r.operation_id) as committed_operations,
  count(*) filter (where reservations_created+elements_renamed+elements_reset > 0) as operations_with_changes,
  coalesce(sum(reservations_created),0) as reservations_created,
  coalesce(sum(elements_renamed),0) as elements_renamed,
  coalesce(sum(elements_reset),0) as elements_reset,
  coalesce(sum(elements_unchanged),0) as elements_unchanged,
  coalesce(sum(elements_skipped),0) as elements_skipped,
  max(r.created_at) as last_operation_at
from tools t cross join categories c
left join public.v_button_operation_results r using (tool_id,is_tutorial)
group by t.tool_id,t.title,c.is_tutorial;

create view public.v_button_user_results with (security_invoker = true) as
select user_id,tool_id,is_tutorial,
  count(*) as committed_operations,
  count(*) filter (where reservations_created+elements_renamed+elements_reset > 0) as operations_with_changes,
  sum(reservations_created) as reservations_created,
  sum(elements_renamed) as elements_renamed,sum(elements_reset) as elements_reset,
  sum(elements_unchanged) as elements_unchanged,sum(elements_skipped) as elements_skipped,
  max(created_at) as last_operation_at
from public.v_button_operation_results group by user_id,tool_id,is_tutorial;

revoke all on public.v_button_operation_results,public.v_button_result_stats,
  public.v_button_user_results from public,anon,authenticated;
grant select on public.v_button_operation_results,public.v_button_result_stats,
  public.v_button_user_results to service_role;
