-- Run in Supabase SQL Editor as the project owner. Read-only diagnostics.
-- Definitions are intentionally in packo_private, not the default public schema.
SELECT to_regclass('packo_private.trip_reminder') AS reminder_table,
       to_regclass('packo_private.reminder_preference') AS preference_table;

SELECT r.id, r.trip_id, t."Destination", r.author_id, r.version,
       r.definition->>'Title' AS title,
       r.definition->>'Date' AS calendar_date,
       (r.definition->>'MinuteOfDay')::int AS minute_of_day,
       r.definition->>'Repeat' AS repeat_mode,
       r.definition->>'TimeZoneId' AS time_zone,
       r.definition->>'Audience' AS audience,
       r.definition->>'RecipientIds' AS selected_recipients,
       r.definition->>'Enabled' AS enabled
FROM packo_private.trip_reminder r
JOIN public."Trip" t ON t."Id" = r.trip_id
ORDER BY r.trip_id, r.id;
