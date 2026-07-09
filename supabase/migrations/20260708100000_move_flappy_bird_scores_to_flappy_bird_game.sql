-- Flappy Bird is stored as its own game while keeping mode = 'flappy_bird'.
-- Existing plugin calls stay compatible because the Edge Function response key
-- remains flappy_bird.

update public.game_leaderboards
set game = 'flappy_bird',
    updated_at = now()
where game = 'snake'
  and mode = 'flappy_bird';

