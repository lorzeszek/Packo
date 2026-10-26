-- Run in the Supabase SQL editor before deploying the app update.
BEGIN;
ALTER TABLE public."PackingItem"
    ADD COLUMN IF NOT EXISTS "Quantity" integer NOT NULL DEFAULT 1;
UPDATE public."PackingItem" SET "Quantity" = 1 WHERE "Quantity" IS NULL OR "Quantity" < 1;
ALTER TABLE public."PackingItem"
    ALTER COLUMN "Quantity" SET DEFAULT 1,
    ALTER COLUMN "Quantity" SET NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public."PackingItem"'::regclass AND conname = 'packing_item_quantity_positive') THEN
        ALTER TABLE public."PackingItem"
            ADD CONSTRAINT packing_item_quantity_positive CHECK ("Quantity" >= 1);
    END IF;
END $$;
COMMIT;
