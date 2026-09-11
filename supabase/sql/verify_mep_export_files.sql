create or replace function public.verify_mep_export_files(p_publication_id uuid, p_revision integer)
returns jsonb language sql stable security invoker set search_path = '' as $$
with revision as (
 select storage_path, manifest, package_bytes from public.mep_exports
 where publication_id=p_publication_id and revision=p_revision
), expected as (
 select regexp_replace(r.storage_path, 'index[.]zip$', '') || (a->>'name') as path,
 (a->>'bytes')::bigint as bytes from revision r
 cross join lateral jsonb_array_elements(r.manifest->'assets') a
 union all
 select storage_path, package_bytes::bigint from revision
 where manifest->'assets' is null
), checked as (
 select e.path, e.bytes, exists(select 1 from storage.objects o
 where o.bucket_id='mep-publications' and o.name=e.path
 and o.archived_at is null and (o.metadata->>'size')::bigint=e.bytes) as valid
 from expected e
)
select jsonb_build_object('expected',count(*),'valid',count(*) filter(where valid),
 'bytes',coalesce(sum(bytes),0)) from checked;
$$;
revoke all on function public.verify_mep_export_files(uuid,integer) from public, anon, authenticated;
grant execute on function public.verify_mep_export_files(uuid,integer) to service_role;
