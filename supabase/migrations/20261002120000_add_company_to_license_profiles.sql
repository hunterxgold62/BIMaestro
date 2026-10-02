alter table public.license_profiles
  add column if not exists company text;
