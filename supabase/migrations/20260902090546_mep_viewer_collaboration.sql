create extension if not exists pgcrypto;

create table public.mep_publications (
  id uuid primary key default gen_random_uuid(),
  owner_license_hash text not null,
  model_key_hash text not null,
  slug text not null unique,
  name text not null check (char_length(name) between 1 and 120),
  active_revision integer not null default 0 check (active_revision >= 0),
  expires_at timestamptz not null,
  revoked_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index mep_publications_owner_model_idx
  on public.mep_publications (owner_license_hash, model_key_hash);
create index mep_publications_expiry_idx
  on public.mep_publications (expires_at)
  where revoked_at is null;

create table public.mep_publication_tokens (
  id uuid primary key default gen_random_uuid(),
  publication_id uuid not null references public.mep_publications(id) on delete cascade,
  token_hash text not null unique,
  access_role text not null check (access_role in ('viewer', 'editor')),
  created_at timestamptz not null default now(),
  revoked_at timestamptz,
  unique (publication_id, access_role)
);

create table public.mep_publication_revisions (
  publication_id uuid not null references public.mep_publications(id) on delete cascade,
  revision integer not null check (revision > 0),
  storage_path text not null unique,
  package_sha256 text not null check (package_sha256 ~ '^[0-9a-f]{64}$'),
  package_bytes bigint not null check (package_bytes > 0),
  manifest jsonb not null default '{}'::jsonb,
  valve_ids text[] not null default '{}',
  created_at timestamptz not null default now(),
  primary key (publication_id, revision)
);

create table public.mep_scenarios (
  publication_id uuid primary key references public.mep_publications(id) on delete cascade,
  revision bigint not null default 0 check (revision >= 0),
  state jsonb not null default '{"valves":{}}'::jsonb,
  updated_by text not null default 'Publication Revit',
  updated_at timestamptz not null default now()
);

create table public.mep_scenario_events (
  id bigint generated always as identity primary key,
  publication_id uuid not null references public.mep_publications(id) on delete cascade,
  scenario_revision bigint not null,
  operation_id uuid not null,
  participant_name text not null,
  target_id text not null,
  previous_value boolean,
  next_value boolean not null,
  created_at timestamptz not null default now(),
  unique (publication_id, operation_id),
  unique (publication_id, scenario_revision)
);

create index mep_scenario_events_recent_idx
  on public.mep_scenario_events (publication_id, scenario_revision desc);

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
  v_scenario public.mep_scenarios%rowtype;
  v_previous_value boolean;
  v_next_revision bigint;
begin
  if exists (
    select 1 from public.mep_scenario_events
    where publication_id = p_publication_id and operation_id = p_operation_id
  ) then
    select * into v_scenario from public.mep_scenarios where publication_id = p_publication_id;
    return jsonb_build_object('scenario', to_jsonb(v_scenario), 'replayed', true);
  end if;

  if not exists (
    select 1
    from public.mep_publications p
    join public.mep_publication_revisions r
      on r.publication_id = p.id and r.revision = p.active_revision
    where p.id = p_publication_id and p_target_id = any(r.valve_ids)
  ) then
    raise exception using errcode = 'P0001', message = 'VALVE_NOT_FOUND';
  end if;

  select * into v_scenario from public.mep_scenarios
  where publication_id = p_publication_id for update;
  if not found or v_scenario.revision <> p_expected_revision then
    raise exception using errcode = 'P0001', message = 'REVISION_CONFLICT';
  end if;

  if jsonb_typeof(v_scenario.state #> array['valves', p_target_id]) = 'boolean' then
    v_previous_value := (v_scenario.state #>> array['valves', p_target_id])::boolean;
  end if;
  v_next_revision := p_expected_revision + 1;

  update public.mep_scenarios
  set revision = v_next_revision,
      state = jsonb_set(state, array['valves', p_target_id], to_jsonb(p_next_value), true),
      updated_by = p_participant_name,
      updated_at = now()
  where publication_id = p_publication_id
  returning * into v_scenario;

  insert into public.mep_scenario_events (
    publication_id, scenario_revision, operation_id, participant_name,
    target_id, previous_value, next_value
  ) values (
    p_publication_id, v_next_revision, p_operation_id, p_participant_name,
    p_target_id, v_previous_value, p_next_value
  );

  return jsonb_build_object('scenario', to_jsonb(v_scenario), 'replayed', false);
end;
$$;

revoke all on function public.apply_mep_scenario_command(uuid, uuid, text, bigint, boolean, text)
  from public, anon, authenticated;
grant execute on function public.apply_mep_scenario_command(uuid, uuid, text, bigint, boolean, text)
  to service_role;

alter table public.mep_publications enable row level security;
alter table public.mep_publication_tokens enable row level security;
alter table public.mep_publication_revisions enable row level security;
alter table public.mep_scenarios enable row level security;
alter table public.mep_scenario_events enable row level security;

revoke all on table public.mep_publications from anon, authenticated;
revoke all on table public.mep_publication_tokens from anon, authenticated;
revoke all on table public.mep_publication_revisions from anon, authenticated;
revoke all on table public.mep_scenarios from anon, authenticated;
revoke all on table public.mep_scenario_events from anon, authenticated;
revoke all on sequence public.mep_scenario_events_id_seq from anon, authenticated;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values (
  'mep-publications',
  'mep-publications',
  false,
  26214400,
  array['application/zip', 'application/octet-stream']
)
on conflict (id) do update set
  public = excluded.public,
  file_size_limit = excluded.file_size_limit,
  allowed_mime_types = excluded.allowed_mime_types;

create policy "MEP realtime read"
on realtime.messages for select
to authenticated
using (
  realtime.topic() = 'mep:' || coalesce((select auth.jwt()) ->> 'publication_id', '')
);

create policy "MEP realtime write"
on realtime.messages for insert
to authenticated
with check (
  realtime.topic() = 'mep:' || coalesce((select auth.jwt()) ->> 'publication_id', '')
  and coalesce((select auth.jwt()) ->> 'share_role', '') = 'editor'
);
