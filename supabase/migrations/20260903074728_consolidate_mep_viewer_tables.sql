-- Consolidate the MEP viewer into two application tables:
--   mep_publications: current share, access and collaborative state
--   mep_exports:      one centralized administrative row per exported revision

alter table public.mep_publications
  add column license_key text,
  add column viewer_token_hash text,
  add column editor_token_hash text,
  add column editor_link text,
  add column scenario_revision bigint not null default 0 check (scenario_revision >= 0),
  add column scenario_state jsonb not null default '{"valves":{}}'::jsonb,
  add column scenario_updated_by text not null default 'Publication Revit',
  add column scenario_updated_at timestamptz not null default now(),
  add column scenario_events jsonb not null default '[]'::jsonb
    check (jsonb_typeof(scenario_events) = 'array'),
  add column usage_month_start date not null default date_trunc('month', now())::date,
  add column reserved_download_bytes bigint not null default 0
    check (reserved_download_bytes >= 0),
  add column scenario_operations bigint not null default 0
    check (scenario_operations >= 0);

update public.mep_publications p
set license_key = coalesce((
      select l.license_key
      from public.licenses l
      where encode(digest(l.license_key, 'sha256'), 'hex') = p.owner_license_hash
      limit 1
    ), 'licence-inconnue'),
    viewer_token_hash = (
      select t.token_hash from public.mep_publication_tokens t
      where t.publication_id = p.id and t.access_role = 'viewer'
    ),
    editor_token_hash = (
      select t.token_hash from public.mep_publication_tokens t
      where t.publication_id = p.id and t.access_role = 'editor'
    ),
    scenario_revision = coalesce((
      select s.revision from public.mep_scenarios s where s.publication_id = p.id
    ), 0),
    scenario_state = coalesce((
      select s.state from public.mep_scenarios s where s.publication_id = p.id
    ), '{"valves":{}}'::jsonb),
    scenario_updated_by = coalesce((
      select s.updated_by from public.mep_scenarios s where s.publication_id = p.id
    ), 'Publication Revit'),
    scenario_updated_at = coalesce((
      select s.updated_at from public.mep_scenarios s where s.publication_id = p.id
    ), p.updated_at),
    scenario_events = coalesce((
      select jsonb_agg(jsonb_build_object(
        'scenario_revision', e.scenario_revision,
        'operation_id', e.operation_id::text,
        'participant_name', e.participant_name,
        'target_id', e.target_id,
        'previous_value', e.previous_value,
        'next_value', e.next_value,
        'created_at', e.created_at
      ) order by e.scenario_revision)
      from public.mep_scenario_events e where e.publication_id = p.id
    ), '[]'::jsonb);

with current_usage as (
  select * from public.mep_viewer_usage_monthly
  where month_start = date_trunc('month', now())::date
), target_publication as (
  select id from public.mep_publications order by created_at limit 1
)
update public.mep_publications p
set reserved_download_bytes = u.reserved_download_bytes,
    scenario_operations = u.scenario_operations,
    usage_month_start = u.month_start
from current_usage u, target_publication t
where p.id = t.id;

alter table public.mep_publications
  alter column license_key set not null,
  alter column viewer_token_hash set not null,
  alter column editor_token_hash set not null;

create unique index mep_publications_viewer_token_hash_idx
  on public.mep_publications (viewer_token_hash);
create unique index mep_publications_editor_token_hash_idx
  on public.mep_publications (editor_token_hash);
create index mep_publications_license_key_idx
  on public.mep_publications (license_key);

create table public.mep_exports (
  id bigint generated always as identity primary key,
  publication_id uuid not null references public.mep_publications(id) on delete cascade,
  revision integer not null check (revision > 0),
  user_name text not null,
  license_key text not null,
  model_name text not null,
  package_bytes bigint not null check (package_bytes > 0),
  package_megabytes numeric(12,2) generated always as
    (round(package_bytes::numeric / 1048576, 2)) stored,
  editor_link text,
  export_date timestamptz not null default now(),
  storage_path text not null unique,
  package_sha256 text not null check (package_sha256 ~ '^[0-9a-f]{64}$'),
  manifest jsonb not null default '{}'::jsonb,
  valve_ids text[] not null default '{}',
  unique (publication_id, revision)
);

insert into public.mep_exports (
  publication_id, revision, user_name, license_key, model_name,
  package_bytes, editor_link, export_date, storage_path,
  package_sha256, manifest, valve_ids
)
select
  r.publication_id,
  r.revision,
  coalesce(nullif(trim(concat_ws(' ', lp.first_name, lp.last_name)), ''),
           nullif(lp.email, ''), 'Utilisateur BIMaestro'),
  p.license_key,
  p.name,
  r.package_bytes,
  p.editor_link,
  r.created_at,
  r.storage_path,
  r.package_sha256,
  r.manifest,
  r.valve_ids
from public.mep_publication_revisions r
join public.mep_publications p on p.id = r.publication_id
left join lateral (
  select profile.first_name, profile.last_name, profile.email
  from public.license_profiles profile
  where profile.license_key = p.license_key
  order by profile.last_seen_at desc
  limit 1
) lp on true;

create index mep_exports_publication_revision_idx
  on public.mep_exports (publication_id, revision desc);
create index mep_exports_license_date_idx
  on public.mep_exports (license_key, export_date desc);

alter table public.mep_exports enable row level security;
revoke all on table public.mep_exports from anon, authenticated;
revoke all on sequence public.mep_exports_id_seq from anon, authenticated;

drop function if exists public.apply_mep_scenario_command(uuid, uuid, text, bigint, boolean, text);
drop function if exists public.reserve_mep_viewer_usage(text, bigint);
drop function if exists public.mep_viewer_storage_bytes();

drop table public.mep_scenario_events;
drop table public.mep_scenarios;
drop table public.mep_publication_tokens;
drop table public.mep_publication_revisions;
drop table public.mep_viewer_usage_monthly;

create or replace function public.mep_viewer_storage_bytes()
returns bigint
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(sum(package_bytes), 0)::bigint from public.mep_exports;
$$;

create or replace function public.reserve_mep_viewer_usage(
  p_publication_id uuid,
  p_kind text,
  p_amount bigint default 1
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_month date := date_trunc('month', now())::date;
  v_download_total bigint;
  v_operation_total bigint;
  v_publication public.mep_publications%rowtype;
  v_download_limit constant bigint := 3221225472;
  v_operation_limit constant bigint := 20000;
begin
  if p_amount <= 0 then
    raise exception using errcode = 'P0001', message = 'INVALID_USAGE_AMOUNT';
  end if;

  perform pg_advisory_xact_lock(hashtext('bimaestro_mep_usage_' || v_month::text));
  select * into v_publication from public.mep_publications
  where id = p_publication_id for update;
  if not found then
    raise exception using errcode = 'P0001', message = 'PUBLICATION_NOT_FOUND';
  end if;

  if v_publication.usage_month_start <> v_month then
    update public.mep_publications
    set usage_month_start = v_month,
        reserved_download_bytes = 0,
        scenario_operations = 0
    where id = p_publication_id
    returning * into v_publication;
  end if;

  select coalesce(sum(reserved_download_bytes), 0),
         coalesce(sum(scenario_operations), 0)
  into v_download_total, v_operation_total
  from public.mep_publications
  where usage_month_start = v_month;

  if p_kind = 'download' then
    if v_download_total + p_amount > v_download_limit then
      raise exception using errcode = 'P0001', message = 'VIEWER_EGRESS_LIMIT';
    end if;
    update public.mep_publications
    set reserved_download_bytes = reserved_download_bytes + p_amount
    where id = p_publication_id returning * into v_publication;
  elsif p_kind = 'scenario' then
    if v_operation_total + p_amount > v_operation_limit then
      raise exception using errcode = 'P0001', message = 'VIEWER_REALTIME_LIMIT';
    end if;
    update public.mep_publications
    set scenario_operations = scenario_operations + p_amount
    where id = p_publication_id returning * into v_publication;
  else
    raise exception using errcode = 'P0001', message = 'UNKNOWN_USAGE_KIND';
  end if;

  return jsonb_build_object(
    'month_start', v_publication.usage_month_start,
    'reserved_download_bytes', v_publication.reserved_download_bytes,
    'scenario_operations', v_publication.scenario_operations
  );
end;
$$;

create or replace function public.apply_mep_scenario_command(
  p_publication_id uuid,
  p_operation_id uuid,
  p_target_id text,
  p_expected_revision bigint,
  p_next_value boolean,
  p_participant_name text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_publication public.mep_publications%rowtype;
  v_previous_value boolean;
  v_next_revision bigint;
  v_updated_at timestamptz := now();
  v_events jsonb;
  v_scenario jsonb;
begin
  select * into v_publication from public.mep_publications
  where id = p_publication_id for update;
  if not found then
    raise exception using errcode = 'P0001', message = 'PUBLICATION_NOT_FOUND';
  end if;

  if exists (
    select 1 from jsonb_array_elements(v_publication.scenario_events) event
    where event ->> 'operation_id' = p_operation_id::text
  ) then
    v_scenario := jsonb_build_object(
      'publication_id', v_publication.id,
      'revision', v_publication.scenario_revision,
      'state', v_publication.scenario_state,
      'updated_by', v_publication.scenario_updated_by,
      'updated_at', v_publication.scenario_updated_at
    );
    return jsonb_build_object('scenario', v_scenario, 'replayed', true);
  end if;

  if not exists (
    select 1 from public.mep_exports e
    where e.publication_id = v_publication.id
      and e.revision = v_publication.active_revision
      and p_target_id = any(e.valve_ids)
  ) then
    raise exception using errcode = 'P0001', message = 'VALVE_NOT_FOUND';
  end if;
  if v_publication.scenario_revision <> p_expected_revision then
    raise exception using errcode = 'P0001', message = 'REVISION_CONFLICT';
  end if;

  if jsonb_typeof(v_publication.scenario_state #> array['valves', p_target_id]) = 'boolean' then
    v_previous_value := (v_publication.scenario_state #>> array['valves', p_target_id])::boolean;
  end if;
  v_next_revision := p_expected_revision + 1;
  v_events := v_publication.scenario_events || jsonb_build_array(jsonb_build_object(
    'scenario_revision', v_next_revision,
    'operation_id', p_operation_id::text,
    'participant_name', p_participant_name,
    'target_id', p_target_id,
    'previous_value', v_previous_value,
    'next_value', p_next_value,
    'created_at', v_updated_at
  ));
  if jsonb_array_length(v_events) > 100 then
    select jsonb_agg(value order by ordinal) into v_events
    from jsonb_array_elements(v_events) with ordinality items(value, ordinal)
    where ordinal > jsonb_array_length(v_events) - 100;
  end if;

  update public.mep_publications
  set scenario_revision = v_next_revision,
      scenario_state = jsonb_set(scenario_state, array['valves', p_target_id], to_jsonb(p_next_value), true),
      scenario_updated_by = p_participant_name,
      scenario_updated_at = v_updated_at,
      scenario_events = v_events,
      updated_at = v_updated_at
  where id = p_publication_id
  returning * into v_publication;

  v_scenario := jsonb_build_object(
    'publication_id', v_publication.id,
    'revision', v_publication.scenario_revision,
    'state', v_publication.scenario_state,
    'updated_by', v_publication.scenario_updated_by,
    'updated_at', v_publication.scenario_updated_at
  );
  return jsonb_build_object('scenario', v_scenario, 'replayed', false);
end;
$$;

revoke all on function public.apply_mep_scenario_command(uuid, uuid, text, bigint, boolean, text)
  from public, anon, authenticated;
revoke all on function public.reserve_mep_viewer_usage(uuid, text, bigint)
  from public, anon, authenticated;
revoke all on function public.mep_viewer_storage_bytes()
  from public, anon, authenticated;
grant execute on function public.apply_mep_scenario_command(uuid, uuid, text, bigint, boolean, text)
  to service_role;
grant execute on function public.reserve_mep_viewer_usage(uuid, text, bigint)
  to service_role;
grant execute on function public.mep_viewer_storage_bytes()
  to service_role;

comment on table public.mep_exports is
  'Centralisation administrative des exports web MEP, une ligne par revision.';
comment on column public.mep_exports.editor_link is
  'Lien collaboratif en clair, visible uniquement par les administrateurs Supabase.';
