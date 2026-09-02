update storage.buckets
set file_size_limit = 26214400
where id = 'mep-publications';

create table public.mep_viewer_usage_monthly (
  month_start date primary key,
  reserved_download_bytes bigint not null default 0 check (reserved_download_bytes >= 0),
  scenario_operations bigint not null default 0 check (scenario_operations >= 0),
  updated_at timestamptz not null default now()
);

alter table public.mep_viewer_usage_monthly enable row level security;
revoke all on table public.mep_viewer_usage_monthly from anon, authenticated;

create or replace function public.reserve_mep_viewer_usage(
  p_kind text,
  p_amount bigint default 1
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_month date := date_trunc('month', now())::date;
  v_usage public.mep_viewer_usage_monthly%rowtype;
  v_download_limit constant bigint := 3221225472; -- 3 GiB, garde 2 GiB d'egress au reste de BIMaestro.
  v_operation_limit constant bigint := 20000;
begin
  if p_amount <= 0 then
    raise exception using errcode = 'P0001', message = 'INVALID_USAGE_AMOUNT';
  end if;
  insert into public.mep_viewer_usage_monthly(month_start) values (v_month)
  on conflict (month_start) do nothing;
  select * into v_usage from public.mep_viewer_usage_monthly
  where month_start = v_month for update;

  if p_kind = 'download' then
    if v_usage.reserved_download_bytes + p_amount > v_download_limit then
      raise exception using errcode = 'P0001', message = 'VIEWER_EGRESS_LIMIT';
    end if;
    update public.mep_viewer_usage_monthly
      set reserved_download_bytes = reserved_download_bytes + p_amount, updated_at = now()
      where month_start = v_month returning * into v_usage;
  elsif p_kind = 'scenario' then
    if v_usage.scenario_operations + p_amount > v_operation_limit then
      raise exception using errcode = 'P0001', message = 'VIEWER_REALTIME_LIMIT';
    end if;
    update public.mep_viewer_usage_monthly
      set scenario_operations = scenario_operations + p_amount, updated_at = now()
      where month_start = v_month returning * into v_usage;
  else
    raise exception using errcode = 'P0001', message = 'UNKNOWN_USAGE_KIND';
  end if;
  return to_jsonb(v_usage);
end;
$$;

create or replace function public.mep_viewer_storage_bytes()
returns bigint
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(sum(package_bytes), 0)::bigint
  from public.mep_publication_revisions;
$$;

revoke all on function public.reserve_mep_viewer_usage(text, bigint) from public, anon, authenticated;
revoke all on function public.mep_viewer_storage_bytes() from public, anon, authenticated;
grant execute on function public.reserve_mep_viewer_usage(text, bigint) to service_role;
grant execute on function public.mep_viewer_storage_bytes() to service_role;
