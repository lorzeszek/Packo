# Account deletion

Apply `Database/Migrations/20261114_add_account_deletion.sql` in the Supabase SQL
editor **before releasing the app**. It requires the packing lists, sharing and
reminder migrations, and must run as the database owner (as with those migrations).
No service-role key is shipped in the app. There is no additional Edge Function.

The authenticated `delete_my_account()` RPC accepts no user ID and deletes only
`auth.uid()`. The entire database operation rolls back on error. Retrying after a
lost response is safe. Anonymous requests cannot execute it. Existing Auth foreign
keys cascade sessions, refresh tokens and identities when the user is deleted.

- Owned trips, including trash, and their lists/items/reminders are deleted.
- Memberships in others' trips, accepted invites, reminder preferences and the
  deleted account's operation receipts are deleted.
- Reminders authored by the account are deleted, including ones on joined trips.
- Other authors' reminders remain. Explicit recipient lists lose this user and
  advance their versions. An empty selected list remains empty, never becomes
  "all participants". Group recipients are derived from current trip membership.
- Packing items contributed to someone else's trip stay, assigned to its owner.
- Old reminder operation results containing this user are removed to prevent
  replaying a stale recipient/author snapshot.

The same cleanup trigger applies to administrative deletes from Supabase Auth.
An already issued JWT can remain valid until expiry; the remaining trip access
checks and user foreign keys prevent it from recreating the deleted account's data.
See [Supabase user management](https://supabase.com/docs/guides/auth/managing-user-data).

The app keeps credentials until the RPC succeeds, then destroys the SDK session,
removes persisted credentials, switches to a guest, cancels device reminders,
removes account-scoped SQLite data and pending uploads, invalidates caches and
opens the initial Trips screen. Other cached accounts and guest drafts are retained.
The current Documents feature is device-wide, without account ownership, and is
not part of the account-scoped data.

Checks (isolated databases only; no live user is deleted):

```powershell
node Tests/Reminders/Database/account-deletion-checks.mjs
dotnet run --project Tests/AccountDeletion/AccountDeletion.csproj
```
