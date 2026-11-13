// A loopback-only PostgreSQL endpoint for the real C# Postgrest client. No live Supabase access.
import { PGlite } from '@electric-sql/pglite';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';

const db = new PGlite();
const owner = '00000000-0000-0000-0000-000000000001';
const member = '00000000-0000-0000-0000-000000000002';
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
  INSERT INTO auth.users(id) VALUES('${owner}'),('${member}');
  INSERT INTO public."Trip" VALUES(1,'${owner}',
    (now() AT TIME ZONE 'Europe/Warsaw')::date + interval '1 day',
    (now() AT TIME ZONE 'Europe/Warsaw')::date + interval '7 days',false,'Integration trip');
  INSERT INTO packo_private.trip_member VALUES(1,'${member}');
`);
await db.exec(await readFile(new URL('../../../Database/Migrations/20261113_add_trip_reminders.sql', import.meta.url), 'utf8'));

let queue = Promise.resolve();
const server = createServer(async (req, res) => {
  let body = '';
  for await (const chunk of req) body += chunk;
  const work = async () => {
    try {
      const user = req.headers['x-test-user'];
      if (![owner, member].includes(user)) throw new Error('Unknown fixture user');
      const args = JSON.parse(body || '{}');
      await db.exec('RESET ROLE; SET ROLE authenticated;');
      await db.query("SELECT set_config('request.jwt.claim.sub',$1,false)", [user]);
      let result;
      if (req.url === '/rpc/save_trip_reminder')
        result = await db.query('SELECT public.save_trip_reminder($1,$2,$3,$4::jsonb,$5) AS result',
          [args.p_trip_id, args.p_operation_id, args.p_expected_version, JSON.stringify(args.p_definition), args.p_trip_zone]);
      else if (req.url === '/rpc/trip_reminders_snapshot')
        result = await db.query('SELECT public.trip_reminders_snapshot() AS result');
      else if (req.url === '/rpc/mute_trip_reminder')
        result = await db.query('SELECT public.mute_trip_reminder($1,$2,$3) AS result', [args.p_trip_id,args.p_reminder_id,args.p_muted]);
      else throw new Error('Unknown fixture RPC');
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(result.rows[0].result));
    } catch (error) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ message: error.message }));
    }
  };
  queue = queue.then(work, work);
});
server.listen(0, '127.0.0.1', () => console.log(`READY http://127.0.0.1:${server.address().port}`));
