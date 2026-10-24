-- Run this migration in the Supabase SQL editor after adding the packing-context columns.
ALTER TABLE "Trip"
    ADD COLUMN IF NOT EXISTS "TripType" text NOT NULL DEFAULT 'Leisure';
