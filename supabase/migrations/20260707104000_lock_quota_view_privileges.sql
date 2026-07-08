-- The quota views expose license-level usage. Keep them for service/admin use,
-- but do not expose them to public API roles.

revoke all on table public.v_tokens_vs_quota from public, anon, authenticated;
revoke all on table public.v_tokens_vs_quota_detail from public, anon, authenticated;
revoke all on table public.v_tokens_vs_quota_global from public, anon, authenticated;

grant select on table public.v_tokens_vs_quota to service_role;
grant select on table public.v_tokens_vs_quota_detail to service_role;
grant select on table public.v_tokens_vs_quota_global to service_role;

