import { PGlite } from '@electric-sql/pglite';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const db = new PGlite(); let checks = 0;
const check = (value, message) => { assert.ok(value, message); console.log('PASS ' + message); checks++; };
const user = '11111111-1111-4111-8111-111111111111';
await db.exec(`CREATE ROLE anon; CREATE ROLE authenticated; CREATE ROLE service_role;
  CREATE SCHEMA auth; CREATE TABLE auth.users(id uuid PRIMARY KEY); INSERT INTO auth.users VALUES('${user}');`);
await db.exec(await readFile(new URL('../../../Database/Migrations/20261122_add_places_quota.sql', import.meta.url), 'utf8'));
check(true, 'The production quota migration executes on PostgreSQL');
const consume = async action => (await db.query('SELECT public.places_consume_quota($1,$2) AS allowed', [user, action])).rows[0].allowed;
await db.exec('SET ROLE service_role');
check(await consume('search'), 'The server role can reserve the first search');
await db.exec('RESET ROLE');
const diagnosticsMigration = await readFile(new URL('../../../Database/Migrations/20261122_describe_places_limits.sql', import.meta.url), 'utf8');
await db.exec(diagnosticsMigration);
const reserve = async action => (await db.query('SELECT public.places_reserve_request($1,$2) AS result', [user, action])).rows[0].result;
const usage = async () => (await db.query(`SELECT
  (SELECT sum(used)::int FROM packo_private.places_user_usage) AS account,
  (SELECT sum(used)::int FROM packo_private.places_global_usage) AS project`)).rows[0];
check((await usage()).account === 1 && (await usage()).project === 1,
  'Deploying richer limit reasons preserves existing daily counters');
await db.exec('SET ROLE service_role');
check(!await consume('search'), 'A rapid duplicate request is blocked by the cooldown');
const cooldown = await reserve('search');
check(cooldown.allowed === false && cooldown.reason === 'cooldown' && cooldown.retryAfter === 2,
  'Short cooldowns have a distinct reason and retry interval');
await db.exec('RESET ROLE');
check((await usage()).account === 1 && (await usage()).project === 1,
  'Rejected duplicate clicks debit neither account nor project budgets');
await db.exec(`UPDATE packo_private.places_user_usage SET used=10, last_used_at=now()-interval '3 seconds' WHERE action='search'`);
check(!await consume('search'), 'The per-user daily cap cannot be exceeded');
const accountLimit = await reserve('search');
check(accountLimit.reason === 'daily_limit' && accountLimit.scope === 'account'
  && accountLimit.limit === 10 && accountLimit.used === 10 && accountLimit.retryAfter >= 1 && accountLimit.retryAfter <= 86400,
  'An exhausted account budget reports its limit and time until UTC reset');
await db.exec(`UPDATE packo_private.places_global_usage SET used=100 WHERE action='details'`);
// No details row exists yet; insert a full global budget to exercise the shared cap.
await db.exec(`INSERT INTO packo_private.places_global_usage VALUES((now() AT TIME ZONE 'UTC')::date,'details',100)
  ON CONFLICT DO NOTHING`);
check(!await consume('details'), 'The project-wide daily cap applies across users');
const projectLimit = await reserve('details');
check(projectLimit.reason === 'daily_limit' && projectLimit.scope === 'project'
  && projectLimit.limit === 100 && projectLimit.used === 100,
  'A shared project cap is distinguishable from an account cap');
check(!await consume('arbitrary'), 'Callers cannot select an unmetered action');
check(await consume('website') && await consume('photo'), 'Website and photo budgets are independent of search');
await db.exec(`UPDATE packo_private.places_user_usage SET used=10 WHERE action='photo'`);
const beforePhotoRejection = await usage();
const photoLimit = await reserve('photo');
check(photoLimit.reason === 'daily_limit' && photoLimit.scope === 'account' && photoLimit.limit === 10,
  'The photo budget remains ten per account without consuming search quota');
assert.deepEqual(await usage(), beforePhotoRejection);
check(true, 'A rejected photo request leaves both daily counters unchanged');
await db.exec(diagnosticsMigration);
assert.deepEqual(await usage(), beforePhotoRejection);
check(true, 'Reapplying the diagnostics migration is safe without resetting usage');
for (const role of ['anon', 'authenticated']) {
  await db.exec(`SET ROLE ${role}`);
  await assert.rejects(() => consume('photo'));
  await assert.rejects(() => reserve('photo'));
  check(true, `${role} cannot debit or bypass server-side quota`);
  await db.exec('RESET ROLE');
}
await db.exec(`DELETE FROM auth.users WHERE id='${user}'`);
check((await db.query('SELECT count(*)::int AS count FROM packo_private.places_user_usage')).rows[0].count === 0,
  'Deleting an account removes its usage metadata through the foreign key');
await db.close();
console.log(`${checks} quota checks passed.`);
