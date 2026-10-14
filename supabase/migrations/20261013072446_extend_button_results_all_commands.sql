-- Stable identifiers for all tracked commands; names follow the latest UI label.
create table public.button_metrics_catalog (
  tool_id text primary key
);
alter table public.button_metrics_catalog enable row level security;
revoke all on public.button_metrics_catalog from public,anon,authenticated;
grant select on public.button_metrics_catalog to service_role;
insert into public.button_metrics_catalog values
('AddFlangesAtEnds'),
('CodexChat'),
('CollaborativeModelTracker'),
('CombinedCleanupCommand'),
('CommandAnalysePoids'),
('CommunityFamilies'),
('ConfigureDynamoButtonCommand'),
('ConnectPipesCommand'),
('ContactCommand'),
('ConvertSharedToFamilyParameters'),
('CreateDemoProject'),
('CustomizeRibbonColorsCommand'),
('DemoTours'),
('ElementHistoryHoverToggle'),
('ExportProjectUnitsCommand'),
('ExportScheduleCommand'),
('ExportSheetsCommand'),
('FamilyBrowserCommand'),
('FlappyBirdCommand'),
('GetPaintedMaterialsCommand'),
('GPTBotWindowCommand'),
('GuideCommand'),
('HighlightElementsByCategoriesCommand'),
('ImportProjectUnitsCommand'),
('MainCommand'),
('MepAssistantAi'),
('MepBooster'),
('MiseAJourCommand'),
('OpenSheetFromView'),
('OverrideColorCommand'),
('PapanoelCommand'),
('PhaseQuickEditCommand'),
('PickDefaultFlange'),
('PipeLengthByDiameterCommandV2'),
('PipeSystemColors'),
('PurgeFamilyParametersCommand'),
('RadialMenuButtonsCommand'),
('RadialMenuCommand'),
('RealisticViewImageCommand'),
('RemoveFlangesReconnect'),
('RenameElementsCommand'),
('ReorientViewCommand'),
('ReservationAutoV3Command'),
('ResetTabItemRandomColorsCommand'),
('RestartAppearanceGuide'),
('RevitGameCommand'),
('RibbonLayoutCommand'),
('RunDynamo1Command'),
('RunDynamo2Command'),
('RunDynamo3Command'),
('RunDynamo4Command'),
('RunDynamo5Command'),
('ScheduleExcelIOCommand'),
('SelectSimilarCommand'),
('SelectViewsCommand'),
('ShowTimeDashboard'),
('SmartClashCommand'),
('SnakeCommand'),
('SupportCommand'),
('TabCompanionToggle'),
('TextCorrectionCommand'),
('ToggleCombinedColoringCommand'),
('TraduireParametresFamilleOpenAI'),
('TraduireVuesFamilleOpenAI'),
('ViewDeckToggle'),
('ViewTemplateTransfer'),
('ViewThumbnailBatch'),
('WindowCompanionToggle');

create view public.v_button_usage_events with (security_invoker = true) as
select distinct on (license_key,machine_id_hash,context->>'usage_id')
  md5(jsonb_build_array(license_key,machine_id_hash)::text) as user_id,
  context->>'command_id' as tool_id,button_id as title,
  created_at,context->>'command_status' as status
from public.plugin_usage_events
where context->>'event_kind'='command_usage'
  and context->>'usage_id' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  and nullif(context->>'command_id','') is not null
order by license_key,machine_id_hash,context->>'usage_id',created_at,id;

create view public.v_button_result_events with (security_invoker = true) as
select distinct on (license_key,machine_id_hash,context->'operation_result'->>'operation_id')
  md5(jsonb_build_array(license_key,machine_id_hash)::text) as user_id,
  coalesce(nullif(context->>'command_id',''),context->'operation_result'->>'tool_id') as tool_id,
  button_id as title,created_at,plugin_version,success as command_success,
  context->'operation_result'->>'operation_id' as operation_id,
  context->'operation_result'->>'mode' as mode,
  coalesce(context->'operation_result'->>'is_tutorial'='true',false) as is_tutorial,
  case when jsonb_typeof(context->'operation_result'->'counts')='object'
    then context->'operation_result'->'counts' else '{}'::jsonb end as counts
from public.plugin_usage_events
where context->'operation_result'->>'schema_version'='1'
  and context->'operation_result'->>'committed'='true'
  and context->'operation_result'->>'operation_id' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  and nullif(context->'operation_result'->>'tool_id','') is not null
order by license_key,machine_id_hash,context->'operation_result'->>'operation_id',created_at,id;

create view public.v_button_result_metrics with (security_invoker = true) as
select e.user_id,e.tool_id,e.operation_id,e.created_at,e.is_tutorial,
  m.key as metric,m.value::bigint as quantity
from public.v_button_result_events e
cross join lateral jsonb_each_text(e.counts) m
where m.key ~ '^[a-z][a-z0-9_]{0,63}$' and m.value ~ '^[0-9]{1,10}$';

-- Extend the original helper without breaking its dependent views.
create or replace view public.v_button_operation_results with (security_invoker = true) as
select e.user_id,e.created_at,e.plugin_version,e.title as button_id,e.command_success,
  e.operation_id,
  case e.tool_id when 'ReservationAutoV3Command' then 'auto_reservation'
    when 'RenameElementsCommand' then 'organizer' else e.tool_id end as tool_id,
  e.mode,e.is_tutorial,
  coalesce(sum(m.quantity) filter(where m.metric='reservations_created'),0)::bigint as reservations_created,
  coalesce(sum(m.quantity) filter(where m.metric='elements_renamed'),0)::bigint as elements_renamed,
  coalesce(sum(m.quantity) filter(where m.metric='elements_reset'),0)::bigint as elements_reset,
  coalesce(sum(m.quantity) filter(where m.metric='elements_unchanged'),0)::bigint as elements_unchanged,
  coalesce(sum(m.quantity) filter(where m.metric='elements_skipped'),0)::bigint as elements_skipped
from public.v_button_result_events e
left join public.v_button_result_metrics m on m.user_id=e.user_id and m.operation_id=e.operation_id
group by e.user_id,e.created_at,e.plugin_version,e.title,e.command_success,e.operation_id,e.tool_id,e.mode,e.is_tutorial;

create view public.v_button_user_summary with (security_invoker = true) as
with users as (
  select user_id,tool_id from public.v_button_usage_events
  union select user_id,tool_id from public.v_button_result_events
), usage as (
  select user_id,tool_id,count(*) as uses,
    count(*) filter(where status='succeeded') as successful_launches,
    count(*) filter(where status='cancelled') as cancelled_launches,
    count(*) filter(where status='failed') as failed_launches
  from public.v_button_usage_events group by 1,2
), results as (
  select user_id,tool_id,count(*) filter(where not is_tutorial) as result_operations,
    count(*) filter(where is_tutorial) as tutorial_result_operations,
    max(created_at) as last_result_at
  from public.v_button_result_events group by 1,2
), quantities as (
  select user_id,tool_id,metric,sum(quantity) as quantity
  from public.v_button_result_metrics where not is_tutorial group by 1,2,3
), metrics as (
  select user_id,tool_id,jsonb_object_agg(metric,quantity) as quantities from quantities group by 1,2
)
select s.user_id,s.tool_id,coalesce(u.uses,0) as uses,
  coalesce(u.successful_launches,0) as successful_launches,
  coalesce(u.cancelled_launches,0) as cancelled_launches,coalesce(u.failed_launches,0) as failed_launches,
  coalesce(r.result_operations,0) as result_operations,
  coalesce(r.tutorial_result_operations,0) as tutorial_result_operations,
  coalesce(m.quantities,'{}'::jsonb) as quantities,r.last_result_at
from users s left join usage u using(user_id,tool_id)
left join results r using(user_id,tool_id) left join metrics m using(user_id,tool_id);

create view public.v_button_results with (security_invoker = true) as
with tools as (
  select tool_id from public.button_metrics_catalog
  union select tool_id from public.v_button_user_summary
), names as (
  select distinct on(tool_id) tool_id,title from (
    select tool_id,title,created_at from public.v_button_usage_events
    union all select tool_id,title,created_at from public.v_button_result_events
  ) n order by tool_id,created_at desc
), totals as (
  select tool_id,count(*) as distinct_users,sum(uses) as uses,
    sum(successful_launches) as successful_launches,sum(cancelled_launches) as cancelled_launches,
    sum(failed_launches) as failed_launches,sum(result_operations) as result_operations,
    sum(tutorial_result_operations) as tutorial_result_operations,max(last_result_at) as last_result_at
  from public.v_button_user_summary group by 1
), quantities as (
  select tool_id,metric,sum(quantity) as quantity
  from public.v_button_result_metrics where not is_tutorial group by 1,2
), metrics as (
  select tool_id,jsonb_object_agg(metric,quantity) as quantities from quantities group by 1
)
select t.tool_id,coalesce(n.title,t.tool_id) as title,
  coalesce(s.distinct_users,0) as distinct_users,coalesce(s.uses,0) as uses,
  coalesce(s.successful_launches,0) as successful_launches,
  coalesce(s.cancelled_launches,0) as cancelled_launches,coalesce(s.failed_launches,0) as failed_launches,
  coalesce(s.result_operations,0) as result_operations,
  coalesce(s.tutorial_result_operations,0) as tutorial_result_operations,
  coalesce((m.quantities->>'reservations_created')::numeric,0) as reservations_created,
  coalesce((m.quantities->>'elements_renamed')::numeric,0) as elements_renamed,
  coalesce((m.quantities->>'elements_reset')::numeric,0) as elements_reset,
  coalesce((m.quantities->>'revit_elements_created')::numeric,0) as revit_elements_created,
  coalesce((m.quantities->>'revit_elements_modified')::numeric,0) as revit_elements_modified,
  coalesce((m.quantities->>'revit_elements_deleted')::numeric,0) as revit_elements_deleted,
  coalesce((m.quantities->>'families_loaded')::numeric,0) as families_loaded,
  coalesce((m.quantities->>'families_reloaded')::numeric,0) as families_reloaded,
  coalesce((m.quantities->>'files_exported')::numeric,0) as files_exported,
  coalesce((m.quantities->>'analyses_completed')::numeric,0) as analyses_completed,
  coalesce((m.quantities->>'objects_analyzed')::numeric,0) as objects_analyzed,
  coalesce((m.quantities->>'issues_found')::numeric,0) as issues_found,
  coalesce(m.quantities,'{}'::jsonb) as all_quantities,s.last_result_at
from tools t left join names n using(tool_id) left join totals s using(tool_id) left join metrics m using(tool_id);

revoke all on public.v_button_usage_events,public.v_button_result_events,public.v_button_result_metrics,
  public.v_button_user_summary,public.v_button_results from public,anon,authenticated;
grant select on public.v_button_usage_events,public.v_button_result_events,public.v_button_result_metrics,
  public.v_button_user_summary,public.v_button_results to service_role;

