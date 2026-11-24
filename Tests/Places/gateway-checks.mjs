import assert from 'node:assert/strict';
import { createHandler } from '../../supabase/functions/trip-places/handler.mjs';

let checks = 0;
const check = (value, message) => { assert.ok(value, message); console.log('PASS ' + message); checks++; };
const config = { supabaseUrl: 'https://project.supabase.co', anonKey: 'public-key', serviceKey: 'service-key', googleKey: 'google-secret' };
const userId = '11111111-1111-4111-8111-111111111111';
const requests = [];
let authorized = true, quotaReady = true, googleStatus = 200;
let reservation = { allowed: true };
const fetcher = async (url, options = {}) => {
  requests.push({ url, ...options });
  if (url.endsWith('/auth/v1/user')) return Response.json({ id: userId }, { status: authorized ? 200 : 401 });
  if (url.endsWith('/rpc/places_reserve_request')) return Response.json(reservation, { status: quotaReady ? 200 : 404 });
  if (url.startsWith('https://places.googleapis.com/') && googleStatus !== 200) return Response.json({}, { status: googleStatus });
  if (url.startsWith('https://cdn.googleusercontent.com/')) return new Response(new Uint8Array([1,2,3]), { headers: { 'content-type': 'image/jpeg' } });
  if (url.includes('/media?')) return Response.json({ photoUri: 'https://cdn.googleusercontent.com/photo' });
  if (options.headers?.['X-Goog-FieldMask'] === 'websiteUri') return Response.json({ websiteUri: 'https://museum.example' });
  const place = { id: 'ChIJMuseum', displayName: { text: 'Museum' }, formattedAddress: 'Address', location: { latitude: 52, longitude: 21 },
    attributions: [{ provider: 'Provider', providerUri: 'https://provider.example' }],
    photos: [{ name: 'places/ChIJMuseum/photos/reference', authorAttributions: [{ displayName: 'Author', uri: 'https://author.example' }] }] };
  return Response.json(url.endsWith('places:searchText') ? { places: [place, place] } : place);
};
const handler = createHandler(config, fetcher);
const post = (body, auth = true) => handler(new Request('https://project.supabase.co/functions/v1/trip-places', {
  method: 'POST', headers: { 'content-type': 'application/json', ...(auth ? { authorization: 'Bearer test-user-access-token' } : {}) }, body: JSON.stringify(body),
}));

check((await post({ action: 'search', destination: 'Warszawa' }, false)).status === 401 && requests.length === 0,
  'An unauthenticated caller cannot reach Auth, quota or the paid API');
authorized = false;
check((await post({ action: 'search', destination: 'Warszawa' })).status === 401 && requests.length === 1,
  'An invalid bearer token cannot reach quota or Google');
authorized = true; requests.length = 0;
check((await post({ action: 'website', placeId: 'google:../../secret' })).status === 400 && requests.length === 1,
  'Untrusted IDs cannot modify the upstream URL or trigger paid requests');
requests.length = 0; quotaReady = false;
check((await post({ action: 'search', destination: 'Warszawa' })).status === 503 && requests.length === 2,
  'A missing quota migration fails closed before paid requests');
quotaReady = true;
reservation = { allowed: false, reason: 'daily_limit', scope: 'account', limit: 10, used: 10, retryAfter: 1200 };
requests.length = 0;
const searchLimit = await post({ action: 'search', destination: 'Warszawa' });
const searchLimitBody = await searchLimit.json();
check(searchLimit.status === 429 && searchLimitBody.code === 'search_daily_limit'
  && searchLimitBody.scope === 'account' && searchLimitBody.used === 10
  && searchLimit.headers.get('retry-after') === '1200' && requests.length === 2,
  'A daily account search cap returns its specific reason without paid requests');
reservation = { allowed: false, reason: 'daily_limit', scope: 'project', limit: 20, used: 20, retryAfter: 1200 };
requests.length = 0;
const photoLimit = await post({ action: 'photo', photoReference: 'places/ChIJMuseum/photos/reference' });
const photoLimitBody = await photoLimit.json();
check(photoLimit.status === 429 && photoLimitBody.code === 'photo_daily_limit'
  && photoLimitBody.scope === 'project' && photoLimitBody.limit === 20 && requests.length === 2,
  'A shared photo cap is distinguishable from exhausted search quota');
reservation = { allowed: false, reason: 'cooldown', retryAfter: 2 };
requests.length = 0;
const cooldown = await post({ action: 'search', destination: 'Warszawa' });
check(cooldown.status === 429 && (await cooldown.json()).code === 'request_too_soon'
  && cooldown.headers.get('retry-after') === '2' && requests.length === 2,
  'A quick duplicate click reports a short cooldown instead of a daily cap');
reservation = { allowed: false, reason: 'invalid_user' };
requests.length = 0;
check((await post({ action: 'search', destination: 'Warszawa' })).status === 503 && requests.length === 2,
  'An unexpected reservation rejection still fails closed');
reservation = { allowed: true }; googleStatus = 429;
const upstreamLimit = await post({ action: 'search', destination: 'Warszawa' });
check(upstreamLimit.status === 429 && (await upstreamLimit.json()).code === 'google_rate_limited',
  'Google upstream throttling is distinct from application limits');
googleStatus = 200; requests.length = 0;
const response = await post({ action: 'search', destination: 'Warszawa', language: 'pl' });
const result = await response.json();
check(response.ok && result.places.length === 1 && result.places[0].id === 'google:ChIJMuseum',
  'Search normalizes and deduplicates real Google results');
check(response.headers.get('cache-control') === 'no-store' && !JSON.stringify(result).includes('google-secret'),
  'Responses disable storage caching and never expose the Google API key');
const search = requests.at(-1);
check(!search.headers['X-Goog-FieldMask'].includes('websiteUri') && !search.headers['X-Goog-FieldMask'].includes('rating')
  && !search.headers['X-Goog-FieldMask'].includes('photos') && JSON.parse(search.body).pageSize === 20,
  'Initial discovery requests only Pro fields and does not fetch photos or Enterprise fields');
const quota = requests.find(x => x.url.includes('/rpc/'));
check(JSON.parse(quota.body).p_user_id === userId, 'Quota uses the verified Auth identity, never a body-supplied user ID');
requests.length = 0;
const details = await (await post({ action: 'details', placeId: 'google:ChIJMuseum', language: 'pl' })).json();
check(details.photoReference === 'places/ChIJMuseum/photos/reference' && details.photoAttributions[0].name === 'Author'
  && requests.length === 3, 'Opening details returns a transient photo reference and required attribution');
requests.length = 0;
const photo = await post({ action: 'photo', photoReference: details.photoReference });
check(photo.headers.get('content-type') === 'image/jpeg' && (await photo.arrayBuffer()).byteLength === 3
  && !requests.at(-1).headers, 'Photo bytes are proxied without sending the API key to the CDN');
requests.length = 0;
check((await (await post({ action: 'website', placeId: 'google:ChIJMuseum' })).json()).websiteUrl === 'https://museum.example/'
  && requests.at(-1).headers['X-Goog-FieldMask'] === 'websiteUri', 'Only clicking website requests the Enterprise website field');
requests.length = 0;
check((await (await post({ action: 'search', destination: 'Warszawa', excludedIds: ['google:ChIJMuseum'] })).json()).places.length === 0,
  'Saved place identifiers are excluded from suggestions');
requests.length = 0;
check((await post({ action: 'photo', photoReference: 'https://attacker.example/private' })).status === 400,
  'Photo references cannot become arbitrary server-side URLs');
console.log(`${checks} gateway checks passed.`);
