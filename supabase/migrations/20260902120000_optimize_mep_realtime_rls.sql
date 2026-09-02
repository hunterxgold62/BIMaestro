drop policy if exists "MEP realtime read" on realtime.messages;
drop policy if exists "MEP realtime write" on realtime.messages;

create policy "MEP realtime read"
on realtime.messages for select
to authenticated
using (
  realtime.topic() = 'mep:' || coalesce((select auth.jwt()) ->> 'publication_id', '')
);

create policy "MEP realtime write"
on realtime.messages for insert
to authenticated
with check (
  realtime.topic() = 'mep:' || coalesce((select auth.jwt()) ->> 'publication_id', '')
  and coalesce((select auth.jwt()) ->> 'share_role', '') = 'editor'
);
