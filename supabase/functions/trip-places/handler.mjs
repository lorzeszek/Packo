const headers = {
  "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store",
  "Pragma": "no-cache", "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};
const json = (body, status = 200, extraHeaders = {}) => new Response(JSON.stringify(body), { status, headers: { ...headers, ...extraHeaders } });
const failure = (code, status) => json({ code }, status);
const validId = value => typeof value === "string" && /^google:[A-Za-z0-9_-]{1,512}$/.test(value);
const webUrl = value => {
  try { const uri = new URL(value); return ["https:", "http:"].includes(uri.protocol) && !uri.username && !uri.password ? uri.href : null; }
  catch { return null; }
};
const attributions = values => (Array.isArray(values) ? values : []).map(x => ({
  name: String(x.displayName ?? x.provider ?? "").slice(0, 300), url: webUrl(x.uri ?? x.providerUri),
})).filter(x => x.name);

function place(value) {
  if (!validId("google:" + value?.id) || !value.displayName?.text) return null;
  const latitude = value.location?.latitude, longitude = value.location?.longitude;
  return {
    id: "google:" + value.id, provider: "google", name: String(value.displayName.text).slice(0, 300),
    locationText: String(value.formattedAddress ?? "").slice(0, 600),
    latitude: Number.isFinite(latitude) && Math.abs(latitude) <= 90 ? latitude : null,
    longitude: Number.isFinite(longitude) && Math.abs(longitude) <= 180 ? longitude : null,
    attributions: attributions(value.attributions),
  };
}

async function readBody(request) {
  const reader = request.body?.getReader();
  if (!reader) throw new Error("invalid_request");
  const chunks = []; let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      length += value.length;
      if (length > 16_384) { await reader.cancel(); throw new Error("invalid_request"); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  try { return JSON.parse(new TextDecoder().decode(bytes)); } catch { throw new Error("invalid_request"); }
}

function validate(body) {
  if (!body || !["search", "details", "website", "photo"].includes(body.action)) return false;
  if (body.action === "search") return typeof body.destination === "string" && body.destination.trim().length >= 2
    && body.destination.length <= 160 && (!body.excludedIds || (Array.isArray(body.excludedIds)
      && body.excludedIds.length <= 200 && body.excludedIds.every(validId)));
  if (body.action === "photo") return typeof body.photoReference === "string"
    && /^places\/[A-Za-z0-9_-]{1,512}\/photos\/[A-Za-z0-9_-]{1,3000}$/.test(body.photoReference);
  return validId(body.placeId);
}

export function createHandler(config, fetcher = fetch) {
  return async request => {
    if (request.method === "OPTIONS") return new Response(null, { status: 204, headers });
    if (request.method !== "POST") return failure("method_not_allowed", 405);
    const authorization = request.headers.get("authorization") ?? "";
    if (!/^Bearer \S{16,8192}$/.test(authorization)) return failure("sign_in_required", 401);
    if (!config.supabaseUrl || !config.anonKey || !config.serviceKey || !config.googleKey)
      return failure("not_configured", 503);
    const signal = AbortSignal.any([request.signal, AbortSignal.timeout(20_000)]);
    try {
      // Never trust a caller-provided user ID or merely decode an unverified JWT.
      const auth = await fetcher(config.supabaseUrl + "/auth/v1/user", {
        headers: { authorization, apikey: config.anonKey }, signal,
      });
      if (!auth.ok) return failure("sign_in_required", 401);
      const user = await auth.json();
      if (!/^[0-9a-f-]{36}$/i.test(user.id ?? "") || user.is_anonymous) return failure("sign_in_required", 401);
      const body = await readBody(request);
      if (!validate(body)) return failure("invalid_request", 400);
      const language = body.language === "pl" ? "pl" : "en";
      // Fail closed if the quota migration is absent. No paid upstream call precedes this RPC.
      const quota = await fetcher(config.supabaseUrl + "/rest/v1/rpc/places_reserve_request", {
        method: "POST", headers: { authorization: "Bearer " + config.serviceKey, apikey: config.serviceKey,
          "Content-Type": "application/json" },
        body: JSON.stringify({ p_user_id: user.id, p_action: body.action }), signal,
      });
      if (!quota.ok) return failure("not_configured", 503);
      const reservation = await quota.json();
      if (reservation?.allowed !== true) {
        if (reservation?.reason === "cooldown") return json({ code: "request_too_soon", action: body.action }, 429, { "Retry-After": "2" });
        if (reservation?.reason !== "daily_limit") return failure("not_configured", 503);
        return json({ code: body.action + "_daily_limit", scope: reservation.scope, action: body.action,
          limit: reservation.limit, used: reservation.used }, 429,
          { "Retry-After": String(Math.max(1, Math.min(86400, Number(reservation.retryAfter) || 86400))) });
      }

      const google = async (path, fieldMask, payload) => {
        const result = await fetcher("https://places.googleapis.com/v1/" + path, {
          method: payload ? "POST" : "GET", headers: { "X-Goog-Api-Key": config.googleKey,
            ...(fieldMask ? { "X-Goog-FieldMask": fieldMask } : {}), "Content-Type": "application/json" },
          ...(payload ? { body: JSON.stringify(payload) } : {}), signal,
        });
        if (!result.ok) {
          const code = result.status === 404 ? "place_not_found" : result.status === 403 ? "google_forbidden"
            : result.status === 429 ? "google_rate_limited" : "unavailable";
          throw Object.assign(new Error(code), { status: result.status === 404 ? 404 : result.status === 429 ? 429 : 502 });
        }
        return result;
      };
      const basicFields = "id,displayName,formattedAddress,location,attributions";
      if (body.action === "search") {
        const result = await (await google("places:searchText", basicFields.split(",").map(x => "places." + x).join(","), {
          textQuery: (language === "pl" ? "atrakcje turystyczne w " : "tourist attractions in ") + body.destination.trim(),
          languageCode: language, pageSize: 20,
        })).json();
        const excluded = new Set(body.excludedIds ?? []);
        const unique = new Map();
        for (const candidate of result.places ?? []) { const item = place(candidate); if (item && !excluded.has(item.id)) unique.set(item.id, item); }
        return json({ places: [...unique.values()].slice(0, 8) });
      }
      if (body.action === "website") {
        const result = await (await google("places/" + encodeURIComponent(body.placeId.slice(7)), "websiteUri")).json();
        return json({ websiteUrl: webUrl(result.websiteUri) });
      }
      if (body.action === "details") {
        const id = body.placeId.slice(7);
        const result = await (await google("places/" + encodeURIComponent(id) + "?languageCode=" + language, basicFields + ",photos")).json();
        const item = place(result);
        if (!item) return failure("place_not_found", 404);
        const photo = result.photos?.[0];
        const photoReference = typeof photo?.name === "string" && photo.name.startsWith("places/" + id + "/photos/") ? photo.name : null;
        return json({ place: item, photoReference, photoAttributions: attributions(photo?.authorAttributions), photoSourceUrl: webUrl(photo?.googleMapsUri) });
      }
      const metadata = await (await google(body.photoReference + "/media?maxWidthPx=640&maxHeightPx=400&skipHttpRedirect=true", null)).json();
      const photoUri = webUrl(metadata.photoUri);
      const photoHost = photoUri ? new URL(photoUri).hostname : "";
      if (!photoUri || !photoUri.startsWith("https:") || !["googleusercontent.com", "ggpht.com"].some(host => photoHost === host || photoHost.endsWith("." + host)))
        return failure("unavailable", 502);
      // The Google API key never follows the image redirect to a CDN.
      const photo = await fetcher(photoUri, { signal });
      if (!photo.ok) return failure("unavailable", 502);
      const type = photo.headers.get("content-type")?.split(";")[0];
      if (!["image/jpeg", "image/png", "image/webp"].includes(type)) return failure("unavailable", 502);
      const bytes = await photo.arrayBuffer();
      if (bytes.byteLength > 4 * 1024 * 1024) return failure("unavailable", 502);
      return new Response(bytes, { headers: { ...headers, "Content-Type": type } });
    } catch (error) {
      if (error?.message === "invalid_request") return failure("invalid_request", 400);
      if (["place_not_found", "google_forbidden", "google_rate_limited", "unavailable"].includes(error?.message))
        return failure(error.message, error.status ?? 502);
      // Never return upstream messages, request bodies, authorization headers or API keys.
      return failure("unavailable", 502);
    }
  };
}
