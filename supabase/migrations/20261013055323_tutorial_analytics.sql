-- Administrator-only tutorial analytics. The collector already persists context.
-- Catalog revisions must be immutable; add a new revision when availability changes.
create table public.tutorial_catalog (
  catalog_version text not null,
  tutorial_id text not null,
  title text not null,
  primary key (catalog_version, tutorial_id)
);
alter table public.tutorial_catalog enable row level security;
revoke all on public.tutorial_catalog from public, anon, authenticated;
grant select on public.tutorial_catalog to service_role;
insert into public.tutorial_catalog values
('1','reservation','Auto réservation'), ('1','history','Qui a fait ça ?'),
('1','colors','Couleurs et vues'), ('1','pipe-calculation','Calcul des canalisations'),
('1','organizer','Organisateur'), ('1','view-template','Gabarit de vue'),
('1','excel','Gestion Excel'), ('1','family-browser','Navigateur de familles'),
('1','mep-booster','MEP Booster'), ('1','clash-3d','Clash 3D');

create index plugin_usage_tutorial_events_idx on public.plugin_usage_events (created_at)
where button_id in ('Tutorial.Event', 'Tutorial.Catalog');

create view public.v_tutorial_events with (security_invoker = true) as
select distinct on (context->>'event_id')
  md5(jsonb_build_array(license_key, machine_id_hash)::text) as user_id,
  plugin_version, created_at, context->>'catalog_version' as catalog_version,
  context->>'tutorial_id' as tutorial_id, context->>'attempt_id' as attempt_id,
  context->>'action' as action,
  case when context->>'step_index' ~ '^[0-9]{1,5}$' then (context->>'step_index')::integer else 0 end as step_index,
  context->>'step_title' as step_title
from public.plugin_usage_events
where button_id = 'Tutorial.Event' and context->>'schema_version' = '1'
  and context->>'event_id' ~ '^[0-9a-f-]{36}$'
  and context->>'attempt_id' ~ '^[0-9a-f-]{36}$'
  and context->>'action' in ('started','step_viewed','completed','quit','interrupted')
order by context->>'event_id', created_at, id;

create view public.v_tutorial_attempts with (security_invoker = true) as
select e.user_id, e.catalog_version, e.tutorial_id, e.attempt_id,
  min(e.created_at) filter (where action = 'started') as started_at,
  max(e.created_at) as last_activity_at,
  case when bool_or(action = 'completed') then 'completed'
       when bool_or(action = 'quit') then 'quit'
       when bool_or(action = 'interrupted') then 'interrupted'
       when max(e.created_at) < now() - interval '24 hours' then 'inactive'
       else 'in_progress' end as status,
  (array_agg(step_index order by created_at desc) filter (where step_index > 0))[1] as last_step,
  (array_agg(step_title order by created_at desc) filter (where step_index > 0))[1] as last_step_title
from public.v_tutorial_events e
join public.tutorial_catalog c using (catalog_version, tutorial_id)
group by e.user_id, e.catalog_version, e.tutorial_id, e.attempt_id
having bool_or(action = 'started');

create view public.v_tutorial_user_progress with (security_invoker = true) as
with users as (
  select md5(jsonb_build_array(license_key, machine_id_hash)::text) as user_id,
    context->>'catalog_version' as catalog_version, max(created_at) as last_seen_at
  from public.plugin_usage_events where button_id = 'Tutorial.Catalog'
  group by 1,2
  union all
  select user_id, catalog_version, max(last_activity_at) from public.v_tutorial_attempts group by 1,2
), available as (
  select catalog_version, count(*) as available_tutorials from public.tutorial_catalog group by 1
), progress as (
  select user_id, catalog_version, count(distinct tutorial_id) as started_tutorials,
    count(distinct tutorial_id) filter (where status = 'completed') as completed_tutorials
  from public.v_tutorial_attempts group by 1,2
)
select u.user_id, u.catalog_version,
  coalesce(p.started_tutorials,0) as started_tutorials,
  coalesce(p.completed_tutorials,0) as completed_tutorials, a.available_tutorials,
  round(100.0 * coalesce(p.completed_tutorials,0) / a.available_tutorials,1) as progress_percent,
  max(u.last_seen_at) as last_seen_at
from users u join available a using (catalog_version)
left join progress p using (user_id,catalog_version)
group by u.user_id,u.catalog_version,p.started_tutorials,p.completed_tutorials,a.available_tutorials;

create view public.v_tutorial_stats with (security_invoker = true) as
with exits as (
  select catalog_version,tutorial_id,last_step,last_step_title,count(*) as exits,
    row_number() over (partition by catalog_version,tutorial_id order by count(*) desc,last_step) as rank
  from public.v_tutorial_attempts where status = 'quit' and last_step > 0
  group by 1,2,3,4
)
select c.catalog_version,c.tutorial_id,c.title,
  count(distinct a.user_id) as distinct_users, count(a.attempt_id) as starts,
  count(*) filter (where a.status = 'completed') as completed_attempts,
  case when count(a.attempt_id) > 0 then
    round(100.0 * count(*) filter (where a.status = 'completed') / count(a.attempt_id),1)
    else null end as completion_percent,
  count(*) filter (where a.status = 'quit') as explicit_exits,
  count(*) filter (where a.status in ('interrupted','inactive')) as interrupted_or_inactive,
  count(*) filter (where a.status = 'in_progress') as in_progress,
  x.last_step as frequent_exit_step,x.last_step_title as frequent_exit_title
from public.tutorial_catalog c
left join public.v_tutorial_attempts a using (catalog_version,tutorial_id)
left join exits x on x.catalog_version=c.catalog_version and x.tutorial_id=c.tutorial_id and x.rank=1
group by c.catalog_version,c.tutorial_id,c.title,x.last_step,x.last_step_title;

revoke all on public.v_tutorial_events, public.v_tutorial_attempts,
  public.v_tutorial_user_progress, public.v_tutorial_stats from public, anon, authenticated;
grant select on public.v_tutorial_events, public.v_tutorial_attempts,
  public.v_tutorial_user_progress, public.v_tutorial_stats to service_role;
