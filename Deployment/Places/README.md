# Google Places pilot

The app calls the `trip-places` Supabase Edge Function. The Google key stays in
the existing project's `GOOGLE_PLACES_API_KEY` Edge Function secret.

## Deploy

1. Enable **Places API (New)** and billing in the Google Cloud project.
2. Restrict a separate server key to **Places API (New)**. Store it as the Supabase
   secret `GOOGLE_PLACES_API_KEY`; do not add it to the app or `appsettings.json`.
3. Run `supabase login` locally. With Supabase CLI 3, from the repository root run:

   ```powershell
   ./Deployment/Places/deploy.ps1
   ```

   This applies `Database/Migrations/20261122_add_places_quota.sql` followed by
   `Database/Migrations/20261122_describe_places_limits.sql`, and deploys
   only `trip-places`, using API-based bundling without Docker. It does not push the
   repository's older migrations or delete any other functions. The script defaults
   to the same project as `SupabaseService.ProjectUrl`.

If using the Dashboard instead, execute both quota migrations in that order in the SQL Editor and
deploy `supabase/functions/trip-places/index.ts` with its `handler.mjs` module.
Gateway JWT verification is disabled in `supabase/config.toml` because the handler
validates each bearer token with Supabase Auth, rejecting anonymous users before
any Google request. Disabling that gateway check does not make the endpoint public.

## Behavior and costs

- Only explicitly requested discovery calls use Text Search Pro. The response has
  at most 8 unsaved results from a pool of 20; discovery is not run on startup.
  Every explicit search excludes only currently saved places, allowing previously
  shown or removed places to appear again. No seen-place history is maintained.
- Place Details Pro and photo metadata are fetched when a user opens details.
- A photo is fetched only from that details popup, once, into memory. Both the
  response and image transfer use `Cache-Control: no-store`; no URI image source
  or disk image cache is used. Author attribution and source links are displayed.
- `websiteUri` uses a separate Place Details Enterprise call, only on a website tap.
- Google Places data is not sent to the app's AI provider. The server queries the
  destination directly and displays Google's returned names and addresses.

Daily pilot caps, in UTC:

| Action | Per account | Whole endpoint |
| --- | ---: | ---: |
| Search | 10 | 100 |
| Details | 20 | 100 |
| Photo | 10 | 20 |
| Website | 10 | 20 |

The PostgreSQL quota RPC serializes each shared counter and enforces a 2-second
cooldown per account/action. Only `service_role` may call it. A missing migration,
missing secret, or failed quota check prevents Google calls. Failed upstream calls
still consume a reservation to prevent error loops from bypassing limits.
The response identifies the action and whether a daily cap belongs to the account
or project. A 2-second cooldown and Google upstream throttling have separate error
codes. Rejected reservations do not debit usage. The diagnostics migration preserves
all counters and budgets, and daily caps reset at midnight UTC. A photo limit leaves
already loaded place details and the Maps action available.
These caps cover this endpoint only, not other API keys or applications sharing
the Google billing account. Monthly free limits and pricing still follow Google.

## Storage and offline behavior

Google place IDs and the user's own labels, notes, and visited status are stored in
SQLite. Google business names, addresses, coordinates, website URLs, photos, photo
references and attributions are never persisted by the new integration. Full Google
cards are kept in memory while browsing; reopening a saved place loads details online.
The "My note" action lets users give a saved ID their own useful offline label.

Existing manually entered and Wikipedia-based saved places are preserved. The new
context-key prefix invalidates previous discovery sets without deleting saved data.
This pilot does not synchronize saved places across devices or create a shared cache
of Google results. Usage tables contain application counters only and expire after
35 days; deleting an Auth account cascades its personal counters.

Before public release, make the app's terms/privacy documents available and include
the Google-required notices. Google Maps text attribution is shown on compact mobile
cards and details; returned third-party and photo attributions have source links.

References: [Places storage and attribution policies](https://developers.google.com/maps/documentation/places/web-service/policies),
[EEA permitted uses](https://cloud.google.com/terms/maps-platform/eea-places-api-permitted-uses),
[photo requests](https://developers.google.com/maps/documentation/places/web-service/place-photos).
