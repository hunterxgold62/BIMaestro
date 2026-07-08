-- Add server-side identity fields used by the snake-leaderboard Edge Function.
-- The function stores a stable hash derived from the license JWT instead of
-- trusting the install_id sent by the client.

alter table public.game_leaderboards
  add column if not exists license_key text,
  add column if not exists machine_id_hash text;

