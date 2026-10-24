-- Run this migration in the Supabase SQL editor before deploying the app update.
-- Existing trips keep the safe defaults: one adult, no children, no transport selected.
ALTER TABLE "Trip"
    ADD COLUMN IF NOT EXISTS "AdultsCount" integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS "ChildrenAges" jsonb NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN IF NOT EXISTS "SelectedTransportTypes" jsonb NOT NULL DEFAULT '[]'::jsonb;

-- Repair rows written by an earlier app build which stored ages as a JSON string
-- in PostgreSQL-array notation, e.g. "{2}", instead of a JSON array, e.g. [2].
UPDATE "Trip"
SET "ChildrenAges" = to_jsonb(
    ARRAY(
        SELECT value::integer
        FROM unnest(string_to_array(trim(both '{}' from "ChildrenAges" #>> '{}'), ',')) AS value
        WHERE value <> ''
    )
)
WHERE jsonb_typeof("ChildrenAges") = 'string';
