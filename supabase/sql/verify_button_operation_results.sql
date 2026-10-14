-- All fixtures are rolled back. Execute as administrator.
begin;
insert into public.plugin_usage_events(license_key,machine_id_hash,button_id,success,created_at,context)
select '__button_results_test__','test',tool_id,status='succeeded',now()+n*interval '1 second',
  jsonb_build_object('command_id',tool_id,'event_kind',kind,'command_status',status,
    'usage_id',case when kind='command_usage' then '10000000-0000-0000-0000-'||lpad(n::text,12,'0') else null end,
    'operation_result',case when counts is null then null else jsonb_build_object(
      'schema_version',1,'operation_id','20000000-0000-0000-0000-'||lpad(op::text,12,'0'),
      'tool_id',tool_id,'mode','test','committed',committed,'is_tutorial',tutorial,'counts',counts) end)
from (values
  (1,1,'ReservationAutoV3Command','command_usage','succeeded',true,false,'{"reservations_created":10}'::jsonb),
  (1,1,'ReservationAutoV3Command','command_usage','succeeded',true,false,'{"reservations_created":10}'::jsonb),
  (2,2,'ReservationAutoV3Command','command_usage','succeeded',true,false,'{"reservations_created":1}'::jsonb),
  (3,3,'ReservationAutoV3Command','command_usage','succeeded',true,false,'{"reservations_created":1}'::jsonb),
  (4,4,'ReservationAutoV3Command','command_usage','cancelled',false,false,null::jsonb),
  (5,5,'ReservationAutoV3Command','operation_result','succeeded',true,true,'{"reservations_created":100}'::jsonb),
  (6,6,'ReservationAutoV3Command','operation_result','failed',false,false,'{"reservations_created":40}'::jsonb),
  (7,7,'RenameElementsCommand','command_usage','succeeded',true,false,'{"elements_renamed":2,"elements_unchanged":1}'::jsonb),
  (8,8,'FamilyBrowserCommand','command_usage','succeeded',false,false,null::jsonb),
  (9,9,'FamilyBrowserCommand','operation_result','succeeded',true,false,'{"families_loaded":3,"families_unchanged":1}'::jsonb),
  (10,10,'FamilyBrowserCommand','operation_result','succeeded',true,false,'{"families_loaded":-1,"items_failed":"invalid"}'::jsonb)
) fixtures(n,op,tool_id,kind,status,committed,tutorial,counts);
do $$
declare u record; user_key text := md5(jsonb_build_array('__button_results_test__'::text,'test'::text)::text);
begin
  select * into strict u from public.v_button_user_summary where user_id=user_key and tool_id='ReservationAutoV3Command';
  if u.uses<>4 or u.successful_launches<>3 or u.cancelled_launches<>1
    or u.result_operations<>3 or u.tutorial_result_operations<>1
    or (u.quantities->>'reservations_created')::integer<>12 then
    raise exception 'Batch/single operation counts or deduplication failed';
  end if;
  select * into strict u from public.v_button_user_summary where user_id=user_key and tool_id='FamilyBrowserCommand';
  if u.uses<>1 or u.result_operations<>2 or (u.quantities->>'families_loaded')::integer<>3 then
    raise exception 'Deferred actions counted as launches or invalid metrics accepted';
  end if;
  select * into strict u from public.v_button_user_summary where user_id=user_key and tool_id='RenameElementsCommand';
  if (u.quantities->>'elements_renamed')::integer<>2 or (u.quantities->>'elements_unchanged')::integer<>1 then
    raise exception 'Organizer metrics failed';
  end if;
  if not exists(select 1 from public.v_button_results where tool_id='PickDefaultFlange') then
    raise exception 'Unused catalog command missing';
  end if;
  if has_table_privilege('anon','public.v_button_results','select')
    or has_table_privilege('authenticated','public.v_button_user_summary','select') then
    raise exception 'Analytics accessible to public clients';
  end if;
end $$;
rollback;
