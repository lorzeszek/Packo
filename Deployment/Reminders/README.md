# Trip reminders

Apply `Database/Migrations/20261113_add_trip_reminders.sql` after the existing packing-list
and trip-sharing migrations, before deploying the updated app. Run it using the project's
normal Supabase SQL migration workflow. It adds the trip time zone, private reminder and
preference tables, version-checked/idempotent RPCs and a small Realtime revision table.
No service-role key belongs in the app. Client access uses the authenticated session.

## Delivery and synchronization

- Supabase holds shared definitions; SQLite stores the current account's copy and pending changes.
- Android schedules one upcoming occurrence per reminder using AlarmManager. It recomputes
  calendar recurrences after delivery, time-zone/clock changes, reboot and app updates.
- Daily device-time reminders follow the current device zone. Fixed/relative reminders use
  the trip zone. Hours-before is an elapsed duration; days-before uses a calendar date/time.
- A DST gap advances to the first valid minute; an overlap uses its first occurrence.
  Delivery history avoids repeating the same daily occurrence when travelling west.
- The trip end is an instant in the trip zone, independent of each recipient's device zone.
  Every occurrence is capped by that end. One-time reminders outside the valid window remain
  visible as expired/needing a new time. Trashed/deleted trips stop local scheduling.
- Synchronization runs at startup/resume, restored connectivity while running, trip changes,
  opening reminders, and Realtime revision events. A complete authorized snapshot reconciles
  deletions, recipients and revoked access. Realtime is a refresh signal, not a durable queue.
- Existing downloaded alarms work offline. Remote additions, cancellations and date changes
  take effect after the next successful sync. Closed-app background synchronization on restored
  connectivity is not guaranteed. This version does not require Firebase or duplicate delivery
  from a server push scheduler.
- Past occurrences are not replayed during sync. Native delivery drops alarms delayed by more
  than 15 minutes or past the trip end. Timing remains subject to Android and device restrictions.
- Desktop builds can edit/sync definitions; native notification delivery currently targets Android.

## Permissions and sharing

Android 13+ asks for notification access while saving the first reminder. The list shows
permission status and offers exact-alarm settings. Without exact-alarm access, scheduling
uses Android's inexact fallback and the UI explicitly indicates possible delays.

Any current participant may create a group reminder. Its author and the trip owner can edit
it. Personal reminders are visible/editable only to their author. Every recipient can mute
one reminder or all group reminders for that trip, without changing another person's settings.
"All participants" includes future joiners; selected recipients are explicit current members.

## Checks

```powershell
npm --prefix Tests/Reminders/Database ci --ignore-scripts
dotnet run --project Tests/Reminders/Reminders.csproj
dotnet run --project Tests/TripDetails/TripDetails.csproj
npm --prefix Tests/Reminders/Database test
```

The database suite executes the production migration against isolated PostgreSQL (PGlite),
including participant/owner permissions, private reminders, conflict detection, idempotency,
validation, individual mutes, membership revocation and cascade deletion. It never connects
to the live Supabase project.

The reminder suite also sends requests with the actual Postgrest client over loopback HTTP
into isolated PostgreSQL, and synchronizes separate SQLite databases representing two
participants and a second phone on the owner's account. Calendar fields are sent as
`yyyy-MM-dd` strings so the RPC client's internal JSON round trip cannot convert them to UTC.
