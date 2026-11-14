import { PGlite } from '@electric-sql/pglite';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import assert from 'node:assert/strict';

const db = new PGlite();
let count = 0;
const check = (condition, message) => { assert.ok(condition, message); console.log('PASS ' + message); count++; };
const deleted = randomUUID(), owner = randomUUID(), member = randomUUID();
const migration = async name => db.exec(await readFile(new URL('../../../Database/Migrations/' + name, import.meta.url), 'utf8'));
async function as(user, role = 'authenticated') {
  await db.exec(`RESET ROLE; SET ROLE ${role};`);
  await db.query("SELECT set_config('request.jwt.claim.sub', $1, false)", [user ?? '']);
}
const scalar = async sql => (await db.query(sql)).rows[0].value;
await db.exec(`
  CREATE ROLE anon; CREATE ROLE authenticated;
  CREATE SCHEMA auth; CREATE SCHEMA extensions;
  CREATE PUBLICATION supabase_realtime;
  CREATE TABLE auth.users(id uuid PRIMARY KEY, raw_user_meta_data jsonb DEFAULT '{}');
  CREATE TABLE auth.sessions(id uuid PRIMARY KEY, user_id uuid REFERENCES auth.users(id) ON DELETE CASCADE);
  CREATE TABLE auth.identities(id uuid PRIMARY KEY, user_id uuid REFERENCES auth.users(id) ON DELETE CASCADE);
  CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE AS
    $$ SELECT nullif(current_setting('request.jwt.claim.sub',true),'')::uuid $$;
  GRANT USAGE ON SCHEMA auth TO authenticated, anon;
  CREATE TABLE public."Trip"("Id" bigint PRIMARY KEY, user_id uuid NOT NULL REFERENCES auth.users(id),
    "StartDate" timestamp, "EndDate" timestamp, "IsInTrash" boolean DEFAULT false, "Destination" text);
  ALTER TABLE public."Trip" ENABLE ROW LEVEL SECURITY;
  CREATE TABLE public."PackingItem"("Id" bigint PRIMARY KEY, "TripId" bigint REFERENCES public."Trip"("Id"),
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE, "Name" text, "Quantity" int,
    "IsPacked" boolean DEFAULT false, "CreatedDate" timestamp);
  ALTER TABLE public."PackingItem" ENABLE ROW LEVEL SECURITY;
  INSERT INTO auth.users(id) VALUES('${deleted}'),('${owner}'),('${member}');
  INSERT INTO auth.sessions VALUES('${randomUUID()}','${deleted}'),('${randomUUID()}','${owner}');
  INSERT INTO auth.identities VALUES('${randomUUID()}','${deleted}'),('${randomUUID()}','${owner}');
  INSERT INTO public."Trip"("Id",user_id,"IsInTrash","Destination") VALUES
    (1,'${deleted}',false,'Owned'), (2,'${deleted}',true,'Trash'),
    (3,'${owner}',false,'Joined'), (4,'${owner}',false,'Unrelated');
`);
await migration('20261109_add_packing_lists.sql');
await migration('20261109_add_trip_sharing.sql');
await migration('20261113_add_trip_reminders.sql');
await migration('20261114_add_account_deletion.sql');
check(true, 'Production packing, sharing, reminder and account-deletion migrations execute together');

const group = randomUUID(), selected = randomUUID(), soleRecipient = randomUUID(), authored = randomUUID(), ownReminder = randomUUID();
const definition = (author, recipients, audience = 2) => JSON.stringify({
  AuthorId: author, AuthorName: 'Name', RecipientIds: recipients, Audience: audience, Version: 7
});
await db.exec(`
  INSERT INTO packo_private.trip_member VALUES(1,'${member}'),(3,'${deleted}'),(3,'${member}');
  INSERT INTO public."PackingItem"("Id","TripId",user_id,"Name","Quantity") VALUES
    (1,1,'${member}','Owned trip item',1), (2,2,'${deleted}','Trash item',1),
    (3,3,'${deleted}','Shared contribution',2), (4,4,'${owner}','Unrelated item',1);
  INSERT INTO packo_private.trip_invite VALUES
    ('\\x01',3,now() + interval '1 day','${deleted}'), ('\\x02',3,now() + interval '1 day',NULL);
  INSERT INTO packo_private.packing_operation VALUES('${deleted}','${randomUUID()}',3,'{}'),('${owner}','${randomUUID()}',3,'{}');
  INSERT INTO packo_private.reminder_preference VALUES('${deleted}',3,'*',true),('${owner}',3,'*',true);
`);
for (const [id, trip, author, recipients, audience] of [
  [group, 3, owner, [], 1], [selected, 3, owner, [deleted, member], 2],
  [soleRecipient, 3, owner, [deleted], 2], [authored, 3, deleted, [], 1], [ownReminder, 1, member, [], 1]
]) {
  await db.query('INSERT INTO packo_private.trip_reminder VALUES($1,$2,$3,7,$4::jsonb)',
    [id, trip, author, definition(author, recipients, audience)]);
}
await db.query('INSERT INTO packo_private.reminder_operation VALUES($1,$2,$3::jsonb)',
  [owner, randomUUID(), JSON.stringify({ Definition: { AuthorId: deleted, RecipientIds: [] } })]);
await db.query('INSERT INTO packo_private.reminder_operation VALUES($1,$2,$3::jsonb)',
  [owner, randomUUID(), JSON.stringify({ Definition: { AuthorId: owner, RecipientIds: [deleted] } })]);
await db.query('INSERT INTO packo_private.reminder_operation VALUES($1,$2,$3::jsonb)', [deleted, randomUUID(), '{}']);
const revision = await scalar('SELECT "Revision" AS value FROM public."TripReminderRevision" WHERE "TripId"=3');

await as(null, 'anon');
await assert.rejects(() => db.query('SELECT public.delete_my_account()'), /permission denied/);
await as(null);
await assert.rejects(() => db.query('SELECT public.delete_my_account()'), /Authentication required/);
await as(deleted);
await assert.rejects(() => db.query('DELETE FROM auth.users WHERE id=$1', [owner]), /permission denied/);
await assert.rejects(() => db.query('SELECT public.delete_my_account($1::uuid)', [owner]), /does not exist/);
check(true, 'Anonymous, missing-identity and arbitrary-target deletion are denied');

await db.exec('RESET ROLE; CREATE TABLE public.deletion_blocker(user_id uuid REFERENCES auth.users(id));');
await db.query('INSERT INTO public.deletion_blocker VALUES($1)', [deleted]);
await as(deleted);
await assert.rejects(() => db.query('SELECT public.delete_my_account()'), /foreign key constraint/);
await db.exec('RESET ROLE;');
check(await scalar('SELECT count(*)::int AS value FROM public."Trip"') === 4
  && await scalar(`SELECT user_id='${deleted}'::uuid AS value FROM public."PackingItem" WHERE "Id"=3`)
  && (await scalar(`SELECT definition->'RecipientIds' AS value FROM packo_private.trip_reminder WHERE id='${selected}'`)).includes(deleted),
  'A failure rolls back Auth, trips, shared items and recipient changes together');
await db.exec('DROP TABLE public.deletion_blocker;');

await as(deleted);
check(await scalar('SELECT public.delete_my_account() AS value') === true, 'An authenticated account deletes itself');
check(await scalar('SELECT public.delete_my_account() AS value') === true, 'Retrying a successful deletion is harmless');
check((await scalar('SELECT public.trip_participants() AS value')).length === 0,
  'An unexpired JWT loses access to shared participants immediately');
await assert.rejects(() => db.query(`INSERT INTO public."Trip"("Id",user_id) VALUES(99,'${deleted}')`), /foreign key constraint/);
check(true, 'An old JWT cannot recreate a trip after account deletion');
await db.exec('RESET ROLE;');

check(await scalar('SELECT count(*)::int AS value FROM auth.users') === 2
  && await scalar('SELECT count(*)::int AS value FROM auth.sessions') === 1
  && await scalar('SELECT count(*)::int AS value FROM auth.identities') === 1,
  'Only the chosen Auth user, its sessions and identities are removed');
check(JSON.stringify((await db.query('SELECT "Id" FROM public."Trip" ORDER BY "Id"')).rows.map(x => x.Id)) === '[3,4]',
  'Owned and trashed trips disappear; joined and unrelated trips remain');
check(await scalar('SELECT count(*)::int AS value FROM public."PackingItem"') === 2
  && await scalar(`SELECT user_id='${owner}'::uuid AND "Quantity"=2 AS value FROM public."PackingItem" WHERE "Id"=3`),
  'Owned-trip items cascade; shared contributions remain without the deleted author ID');
check(await scalar('SELECT count(*)::int AS value FROM packo_private.trip_reminder') === 3,
  'Authored reminders and owned-trip reminders disappear; other reminders survive');
const remaining = (await db.query('SELECT definition,version FROM packo_private.trip_reminder WHERE id=$1', [selected])).rows[0];
check(JSON.stringify(remaining.definition.RecipientIds) === JSON.stringify([member])
  && remaining.version === 8 && remaining.definition.Version === 8,
  'Recipient removal preserves everyone else and advances both conflict versions');
check((await scalar(`SELECT definition->'RecipientIds' AS value FROM packo_private.trip_reminder WHERE id='${soleRecipient}'`)).length === 0
  && await scalar(`SELECT (definition->>'Audience')::int AS value FROM packo_private.trip_reminder WHERE id='${soleRecipient}'`) === 2,
  'A reminder with no remaining selected recipients stays private to that selection');
for (const table of ['trip_member','trip_invite','packing_operation','reminder_preference']) {
  check(await scalar(`SELECT count(*)::int AS value FROM packo_private.${table}`) === 1,
    `${table} removes this account's rows and preserves others`);
}
check(await scalar('SELECT count(*)::int AS value FROM packo_private.reminder_operation') === 0,
  'Idempotency snapshots cannot replay removed personal data');
check(await scalar('SELECT "Revision" AS value FROM public."TripReminderRevision" WHERE "TripId"=3') > revision,
  'Remaining participants receive a reminder revision change');
await as(owner);
check((await scalar('SELECT public.trip_participants() AS value')).every(p => p.UserId !== deleted),
  'The deleted user disappears from participant lists');
await db.exec('RESET ROLE;');
await db.query('DELETE FROM auth.users WHERE id=$1', [member]);
check(await scalar('SELECT count(*)::int AS value FROM public."Trip"') === 2,
  'Administrative Auth deletion also preserves trips owned by others');
console.log(`${count} account deletion database checks passed.`);
await db.close();
