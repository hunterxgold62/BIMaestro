-- Geometry/link lifetime remains 30 days. Collaborative state survives for
-- five calendar months after the latest edit, successful export or extension.
alter table public.mep_publications add column data_expires_at timestamptz;
update public.mep_publications set data_expires_at =
  greatest(created_at, updated_at, scenario_updated_at) + interval '5 months';
alter table public.mep_publications alter column data_expires_at set not null;
create index mep_publications_data_expiry_idx on public.mep_publications(data_expires_at);

create function public.mep_refresh_data_retention() returns trigger
language plpgsql security invoker set search_path = '' as $$
begin
  if TG_OP = 'INSERT' then
    NEW.data_expires_at := now() + interval '5 months';
  elsif NEW.scenario_revision is distinct from OLD.scenario_revision
     or NEW.scenario_state is distinct from OLD.scenario_state
     or NEW.active_revision is distinct from OLD.active_revision
     or NEW.expires_at is distinct from OLD.expires_at then
    NEW.data_expires_at := now() + interval '5 months';
  end if;
  return NEW;
end;
$$;
revoke all on function public.mep_refresh_data_retention() from public, anon, authenticated;
create trigger mep_refresh_data_retention before insert or update
on public.mep_publications for each row execute function public.mep_refresh_data_retention();
