-- Run as administrator. Fixtures are rolled back, including on successful checks.
begin;
insert into public.plugin_usage_events (license_key,machine_id_hash,button_id,success,created_at,context)
values ('__tutorial_analytics_test__','test','Tutorial.Catalog',true,now(),'{"catalog_version":"1"}');
insert into public.plugin_usage_events (license_key,machine_id_hash,button_id,success,created_at,context)
select '__tutorial_analytics_test__','test','Tutorial.Event',true,
  now() + n * interval '1 second',
  jsonb_build_object('schema_version',1,'catalog_version','1','tutorial_id','reservation',
    'event_id',event_id,'attempt_id',attempt_id,'action',action,'step_index',step,'step_title','Étape test')
from (values
 (0,'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','started',0),
 (1,'00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000011','step_viewed',2),
 (2,'00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000011','completed',2),
 (2,'00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000011','completed',2),
 (3,'00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000012','started',0),
 (4,'00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000012','completed',2),
 (5,'00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000013','started',0),
 (6,'00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000013','step_viewed',4),
 (7,'00000000-0000-0000-0000-000000000008','00000000-0000-0000-0000-000000000013','quit',4)
) fixture(n,event_id,attempt_id,action,step);
do $$
declare progress record; stats record;
begin
  select * into strict progress from public.v_tutorial_user_progress
  where user_id=md5(jsonb_build_array('__tutorial_analytics_test__'::text,'test'::text)::text);
  if progress.started_tutorials <> 1 or progress.completed_tutorials <> 1
     or progress.available_tutorials <> 10 or progress.progress_percent <> 10 then
    raise exception 'Distinct tutorial progression failed';
  end if;
  if (select count(*) from public.v_tutorial_attempts where user_id=progress.user_id) <> 3 then
    raise exception 'Attempt deduplication failed';
  end if;
  if not exists (select 1 from public.v_tutorial_attempts where user_id=progress.user_id
    and status='quit' and last_step=4) then raise exception 'Exit step failed'; end if;
  if (select count(*) from public.v_tutorial_stats where catalog_version='1') <> 10 then
    raise exception 'Unused tutorials missing';
  end if;
  if has_table_privilege('anon','public.v_tutorial_user_progress','select')
     or has_table_privilege('authenticated','public.v_tutorial_stats','select') then
    raise exception 'Analytics accessible to public clients';
  end if;
end $$;
rollback;
