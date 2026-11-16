import { PGlite } from '@electric-sql/pglite';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

const db = new PGlite();
const owner = '00000000-0000-0000-0000-000000000001';
const outsider = '00000000-0000-0000-0000-000000000002';
const check = message => console.log('PASS ' + message);
try {
  await db.exec(`
    CREATE ROLE anon; CREATE ROLE authenticated;
    CREATE SCHEMA auth; CREATE SCHEMA extensions;
    CREATE PUBLICATION supabase_realtime;
    CREATE TABLE auth.users(id uuid PRIMARY KEY, raw_user_meta_data jsonb DEFAULT '{}');
    CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE AS
      $$ SELECT nullif(current_setting('request.jwt.claim.sub',true),'')::uuid $$;
    GRANT USAGE ON SCHEMA auth TO authenticated, anon;
    CREATE TABLE public."Trip"("Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      user_id uuid NOT NULL REFERENCES auth.users(id), "ClientId" text, "LocalTripId" int,
      "StartDate" timestamp, "EndDate" timestamp, "IsInTrash" boolean DEFAULT false, "Destination" text);
    CREATE TABLE public."PackingItem"("Id" bigint PRIMARY KEY, "TripId" bigint REFERENCES public."Trip"("Id"),
      user_id uuid NOT NULL REFERENCES auth.users(id), "Name" text, "Quantity" int,
      "IsPacked" boolean DEFAULT false, "CreatedDate" timestamp);
    INSERT INTO auth.users(id) VALUES('${owner}'),('${outsider}');
    GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO authenticated;
  `);
  for (const name of ['20261109_add_packing_lists.sql', '20261109_add_trip_sharing.sql', '20261113_add_trip_reminders.sql'])
    await db.exec(await readFile(new URL('../../../Database/Migrations/' + name, import.meta.url), 'utf8'));
  await db.exec('SET ROLE authenticated');
  await db.query("SELECT set_config('request.jwt.claim.sub',$1,false)", [owner]);

  const insert = `INSERT INTO public."Trip"(user_id,"ClientId","LocalTripId","Destination","TimeZoneId")
    VALUES($1,'local-owner',$2,$3,'Europe/Warsaw')`;
  await assert.rejects(() => db.query(insert + ' RETURNING *', [owner, 1, 'Rejected returning']),
    error => error.code === '42501' && error.message.includes('trip_access_guard'));
  check('The deployed sharing policies reproduce the original INSERT RETURNING failure');

  await db.query(insert, [owner, 2, 'Warszawa']);
  await db.query(insert, [owner, 3, 'Gdynia']);
  const saved = await db.query(`SELECT "Id", "Destination" FROM public."Trip"
    WHERE "ClientId"='local-owner' AND "LocalTripId" IN (2,3) ORDER BY "LocalTripId"`);
  assert.deepEqual(saved.rows.map(row => row.Destination), ['Warszawa', 'Gdynia']);
  assert.ok(saved.rows.every(row => row.Id > 0));
  check('Two minimal inserts commit and separate owner reads return both generated identities');

  await assert.rejects(() => db.query(insert, [outsider, 4, 'Forged owner']), error => error.code === '42501');
  await db.query("SELECT set_config('request.jwt.claim.sub',$1,false)", [outsider]);
  assert.equal((await db.query('SELECT * FROM public."Trip"')).rows.length, 0);
  check('Minimal inserts retain owner enforcement and do not expose trips to other accounts');
} catch (error) {
  console.error(error.code ?? error.name, error.message);
  process.exitCode = 1;
} finally {
  await db.close();
}
