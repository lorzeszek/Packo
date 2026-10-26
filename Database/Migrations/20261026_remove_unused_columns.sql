-- Run in the Supabase SQL editor after updating all app clients.
-- Permanently removes PackingItem.Category, PackingItem.ModifiedDate and Trip.ModifiedDate.
-- RESTRICT prevents cascading removal of dependent database objects.
BEGIN;

ALTER TABLE public."PackingItem"
    DROP COLUMN IF EXISTS "Category" RESTRICT,
    DROP COLUMN IF EXISTS "ModifiedDate" RESTRICT;

ALTER TABLE public."Trip"
    DROP COLUMN IF EXISTS "ModifiedDate" RESTRICT;

NOTIFY pgrst, 'reload schema';

COMMIT;
