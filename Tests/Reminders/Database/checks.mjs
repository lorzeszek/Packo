import { PGlite } from '@electric-sql/pglite';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import assert from 'node:assert/strict';

const db = new PGlite();
let count = 0;
const check = (condition, description) => { assert.ok(condition, description); console.log('PASS ' + description); count++; };
const owner = '00000000-0000-0000-0000-000000000001';
const member = '00000000-0000-0000-0000-000000000002';
const outsider = '00000000-0000-0000-0000-000000000003';
const selected = '00000000-0000-0000-0000-000000000004';
await db.exec(`
  CREATE ROLE anon; CREATE ROLE authenticated;
  CREATE SCHEMA auth; CREATE SCHEMA packo_private;
  CREATE TABLE auth.users(id uuid PRIMARY KEY, raw_user_meta_data jsonb DEFAULT '{}');
  CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE AS
    $$ SELECT nullif(current_setting('request.jwt.claim.sub',true),'')::uuid $$;
  GRANT USAGE ON SCHEMA auth TO authenticated;
  CREATE TABLE public."Trip"("Id" bigint PRIMARY KEY, user_id uuid, "StartDate" timestamp,
    "EndDate" timestamp, "IsInTrash" boolean DEFAULT false, "Destination" text);
  CREATE TABLE packo_private.trip_member(trip_id bigint REFERENCES public."Trip"("Id") ON DELETE CASCADE,
    user_id uuid REFERENCES auth.users(id), PRIMARY KEY(trip_id,user_id));
  CREATE FUNCTION packo_private.is_trip_owner(p_trip_id bigint) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER AS
    $$ SELECT EXISTS(SELECT 1 FROM public."Trip" WHERE "Id"=p_trip_id AND user_id=auth.uid()) $$;
  CREATE FUNCTION packo_private.can_access_trip(p_trip_id bigint) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER AS
    $$ SELECT EXISTS(SELECT 1 FROM public."Trip" t WHERE "Id"=p_trip_id AND (user_id=auth.uid() OR
      (NOT "IsInTrash" AND EXISTS(SELECT 1 FROM packo_private.trip_member m WHERE m.trip_id=t."Id" AND m.user_id=auth.uid())))) $$;
  GRANT USAGE ON SCHEMA packo_private TO authenticated;
  CREATE PUBLICATION supabase_realtime;
  INSERT INTO auth.users(id) VALUES('${owner}'),('${member}'),('${outsider}'),('${selected}');
  INSERT INTO public."Trip" VALUES(1,'${owner}','2099-07-10 12:00','2099-07-20 20:00',false,'Trip'),
    (2,'${outsider}','2099-07-10 12:00','2099-07-20 20:00',false,'Other');
  INSERT INTO packo_private.trip_member VALUES(1,'${member}'),(1,'${selected}');
`);
await db.exec(await readFile(new URL('../../../Database/Migrations/20261113_add_trip_reminders.sql', import.meta.url), 'utf8'));
check(true, 'Production migration executes on an isolated PostgreSQL database');
async function as(user) {
  await db.exec('RESET ROLE; SET ROLE authenticated;');
  await db.query("SELECT set_config('request.jwt.claim.sub',$1,false)", [user]);
}
async function save(definition, version = 0, trip = 1, op = randomUUID()) {
  const result = await db.query('SELECT public.save_trip_reminder($1,$2,$3,$4::jsonb,$5) AS result',
    [trip, op, version, JSON.stringify(definition), 'Europe/Warsaw']);
  return result.rows[0].result;
}
async function snapshot() { return (await db.query('SELECT public.trip_reminders_snapshot() AS result')).rows[0].result; }
function definition(audience = 1) { return { Id: randomUUID(), Version: 999, AuthorId: outsider, AuthorName: 'Forged',
  Title: 'Reminder', Body: 'Remember', Timing: 0, Repeat: 1, Clock: 0, Audience: audience,
  RecipientIds: [], Date: '2099-07-11T00:00:00', MinuteOfDay: 1080, Offset: 1,
  RepeatUntil: null, TimeZoneId: 'Europe/Warsaw', Enabled: true, Deleted: false }; }

await as(member);
const group = definition();
group.Date = '2099-07-14T00:00:00';
const op = randomUUID();
const created = await save(group, 0, 1, op);
check(created.Status === 'ok' && created.Definition.AuthorId === member && created.Definition.Version === 1,
  'A participant creates a group reminder and the server assigns its author and version');
const retried = await save(group, 0, 1, op);
check(retried.Definition.Version === 1, 'Retrying the same operation is idempotent');
check((await snapshot()).Entries.length === 1, 'The author can read the reminder');
await as(owner);
check((await snapshot()).Entries.length === 1, 'The owner sees a participant-created group reminder');
let changed = await save({ ...created.Definition, Title: 'Owner edit' }, 1);
check(changed.Status === 'ok' && changed.Definition.Version === 2, 'The trip owner can edit a group reminder');
await as(member);
changed = await save({ ...changed.Definition, Date: '2099-07-13T00:00:00' }, 2);
check(changed.Status === 'ok' && changed.Definition.Date.startsWith('2099-07-13')
  && (await snapshot()).Entries.find(x => x.Definition.Id === group.Id).Definition.Date.startsWith('2099-07-13'),
  'Editing the 14th to the 13th persists the earlier calendar date in PostgreSQL');
const conflict = await save({ ...created.Definition, Title: 'Stale edit' }, 1);
check(conflict.Status === 'conflict' && conflict.Definition.Title === 'Owner edit', 'Stale edits return the newer definition');
check((await save(changed.Definition, null)).Status === 'denied', 'A null expected version cannot bypass conflict detection');
const personal = await save(definition(0));
check(personal.Status === 'ok', 'A participant can create a private reminder');
await as(owner);
check(!(await snapshot()).Entries.some(x => x.Definition.Id === personal.Definition.Id), 'A participant personal reminder is private from the owner');
check((await save({ ...personal.Definition, Title: 'Intrusion' }, 1)).Status === 'denied', 'The owner cannot edit a participant personal reminder');
await as(selected);
check((await save(changed.Definition, 2)).Status === 'denied', 'Another participant cannot edit someone else group reminder');
await as(member);
const specific = definition(2); specific.RecipientIds = [selected];
check((await save(specific)).Status === 'ok', 'An author can select a current participant as the recipient');
const invalidRecipient = definition(2); invalidRecipient.RecipientIds = [outsider];
check((await save(invalidRecipient)).Status === 'denied', 'A user outside the trip cannot be selected');
const invalidText = definition(); invalidText.Title = '';
check((await save(invalidText)).Status === 'invalid', 'The server independently validates title length');
const invalidZone = definition(); invalidZone.TimeZoneId = 'Not/AZone';
check((await save(invalidZone)).Status === 'invalid', 'Unknown time zones are rejected');
const afterEnd = definition(); afterEnd.Date = '2099-07-21T00:00:00';
check((await save(afterEnd)).Status === 'invalid', 'The server rejects reminders after trip end');
const badOffset = definition(); badOffset.Offset = -1;
check((await save(badOffset)).Status === 'invalid', 'Negative departure offsets are rejected');
const badDate = definition(); badDate.Date = 'not-a-date';
check((await save(badDate)).Status === 'invalid', 'Malformed dates are rejected without saving');
const badRecipients = definition(); badRecipients.RecipientIds = [123];
check((await save(badRecipients)).Status === 'invalid', 'Malformed recipient element types cannot poison a shared snapshot');
const badEnum = definition(); badEnum.Audience = '1';
check((await save(badEnum)).Status === 'invalid', 'Enum values must be JSON numbers so all clients can deserialize them');
const badTitle = definition(); badTitle.Title = { text: 'Title' };
check((await save(badTitle)).Status === 'invalid', 'Non-string titles cannot poison the shared snapshot');
const muted = (await db.query("SELECT public.mute_trip_reminder(1,$1,true) AS result",[group.Id])).rows[0].result;
check(muted && (await snapshot()).Preferences.some(p => p.ReminderId === group.Id && p.Muted), 'An individual mute is stored for the caller');
await as(owner);
check((await snapshot()).Preferences.length === 0, 'One participant mute does not change the owner preferences');
let directDenied = false;
try { await db.query('SELECT * FROM packo_private.trip_reminder'); } catch { directDenied = true; }
check(directDenied, 'Authenticated clients cannot bypass RPC permissions through direct table access');
await as(outsider);
check((await snapshot()).Entries.length === 0 && (await save(definition())).Status === 'denied', 'An outsider cannot read or create trip reminders');
await db.exec(`RESET ROLE; DELETE FROM packo_private.trip_member WHERE trip_id=1 AND user_id='${member}';`);
await as(member);
check((await snapshot()).Entries.length === 0 && (await save(created.Definition, 2)).Status === 'denied', 'Revoking trip membership removes read and write access');
await db.exec(`RESET ROLE; DELETE FROM public."Trip" WHERE "Id"=1;`);
check((await db.query('SELECT count(*)::int AS count FROM packo_private.trip_reminder')).rows[0].count === 0,
  'Deleting a trip cascades its reminder definitions');
console.log(`${count} database checks passed.`);
await db.close();
