-- Reserve a unique revision under a row lock. Expired/aborted uploads never
-- reuse a revision, so a delayed client cannot overwrite a newer transfer.
alter table public.mep_publications
  add column revision_counter integer not null default 0,
  add column upload_until timestamptz;
update public.mep_publications p set revision_counter = greatest(active_revision,
  coalesce((select max(e.revision) from public.mep_exports e where e.publication_id=p.id),0));
create function public.mep_reserve_upload(p_id uuid, p_owner text) returns integer
language plpgsql security invoker set search_path = '' as $$
declare reserved integer;
begin
  update public.mep_publications set
    revision_counter=greatest(revision_counter,active_revision)+1,
    upload_until=now()+interval '30 minutes'
  where id=p_id and owner_license_hash=p_owner
    and (upload_until is null or upload_until <= now())
  returning revision_counter into reserved;
  return reserved;
end;
$$;
revoke all on function public.mep_reserve_upload(uuid,text) from public, anon, authenticated;
grant execute on function public.mep_reserve_upload(uuid,text) to service_role;
