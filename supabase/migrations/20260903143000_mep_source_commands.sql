create or replace function public.apply_mep_scenario_edit(
  p_publication_id uuid,
  p_operation_id uuid,
  p_command_kind text,
  p_target_id text,
  p_expected_revision bigint,
  p_next_value jsonb,
  p_participant_name text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_publication public.mep_publications%rowtype;
  v_previous_value jsonb;
  v_next_revision bigint;
  v_updated_at timestamptz := now();
  v_events jsonb;
  v_state jsonb;
  v_bucket text;
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

  if v_publication.scenario_revision <> p_expected_revision then
    raise exception using errcode = 'P0001', message = 'REVISION_CONFLICT';
  end if;
  if p_command_kind = 'valve' then
    if jsonb_typeof(p_next_value) <> 'boolean' then
      raise exception using errcode = 'P0001', message = 'INVALID_VALVE_VALUE';
    end if;
    if not exists (
      select 1 from public.mep_exports e
      where e.publication_id = v_publication.id
        and e.revision = v_publication.active_revision
        and (
          p_target_id = any(e.valve_ids)
          or (
            p_target_id ~ '\\|[0-9]{1,18}$'
            and exists (
              select 1 from unnest(e.valve_ids) valve_id
              where right(lower(valve_id), 8) = lpad(
                lower(to_hex(substring(p_target_id from '([0-9]+)$')::bigint)),
                8,
                '0'
              )
            )
          )
        )
    ) then
      raise exception using errcode = 'P0001', message = 'VALVE_NOT_FOUND';
    end if;
    v_bucket := 'valves';
  elsif p_command_kind = 'source' then
    if jsonb_typeof(p_next_value) <> 'string' or
       p_next_value #>> '{}' not in ('inlet', 'outlet', 'none') then
      raise exception using errcode = 'P0001', message = 'INVALID_SOURCE_VALUE';
    end if;
    v_bucket := 'sources';
  else
    raise exception using errcode = 'P0001', message = 'UNKNOWN_COMMAND_KIND';
  end if;

  v_previous_value := v_publication.scenario_state #> array[v_bucket, p_target_id];
  v_state := v_publication.scenario_state;
  if not (v_state ? v_bucket) then
    v_state := jsonb_set(v_state, array[v_bucket], '{}'::jsonb, true);
  end if;
  if p_command_kind = 'source' and p_next_value #>> '{}' = 'none' then
    v_state := v_state #- array[v_bucket, p_target_id];
  else
    v_state := jsonb_set(v_state, array[v_bucket, p_target_id], p_next_value, true);
  end if;

  v_next_revision := p_expected_revision + 1;
  v_events := v_publication.scenario_events || jsonb_build_array(jsonb_build_object(
    'scenario_revision', v_next_revision,
    'operation_id', p_operation_id::text,
    'command_kind', p_command_kind,
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
      scenario_state = v_state,
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

revoke all on function public.apply_mep_scenario_edit(uuid, uuid, text, text, bigint, jsonb, text)
  from public, anon, authenticated;
grant execute on function public.apply_mep_scenario_edit(uuid, uuid, text, text, bigint, jsonb, text)
  to service_role;

comment on function public.apply_mep_scenario_edit(uuid, uuid, text, text, bigint, jsonb, text) is
  'Applique atomiquement une commande collaborative de vanne ou de source MEP.';
