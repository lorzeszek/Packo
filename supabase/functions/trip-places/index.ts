import { createHandler } from "./handler.mjs";

Deno.serve(createHandler({
  supabaseUrl: Deno.env.get("SUPABASE_URL"),
  anonKey: Deno.env.get("SUPABASE_ANON_KEY"),
  serviceKey: Deno.env.get("SUPABASE_SERVICE_ROLE_KEY"),
  googleKey: Deno.env.get("GOOGLE_PLACES_API_KEY"),
}));
