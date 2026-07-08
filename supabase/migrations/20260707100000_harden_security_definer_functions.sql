-- Restrict privileged helper functions in the public schema.
-- These functions are executed by Edge Functions with the service role and
-- should not be callable by public API roles.

revoke execute on function public.refresh_all_analytics() from public, anon, authenticated;
grant execute on function public.refresh_all_analytics() to service_role;

revoke execute on function public.set_updated_at() from public, anon, authenticated;
grant execute on function public.set_updated_at() to service_role;

