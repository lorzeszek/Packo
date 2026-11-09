-- Apply before rolling out the app version that uses named packing lists.
BEGIN;

CREATE TABLE public."PackingList" (
    "TripId" bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    "Id" uuid NOT NULL,
    "Name" text,
    PRIMARY KEY ("TripId", "Id"),
    CONSTRAINT packing_list_name_check CHECK (
        ("Name" IS NULL AND "Id" = '00000000-0000-0000-0000-000000000000')
        OR (char_length(btrim("Name")) BETWEEN 1 AND 100 AND "Name" IS NOT NULL)
    )
);

-- The zero UUID is scoped to a trip. A null name is localized by the app.
INSERT INTO public."PackingList" ("TripId", "Id")
SELECT "Id", '00000000-0000-0000-0000-000000000000' FROM public."Trip";

ALTER TABLE public."PackingItem"
    ADD COLUMN "PackingListId" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    ADD CONSTRAINT packing_item_list_fk FOREIGN KEY ("TripId", "PackingListId")
        REFERENCES public."PackingList" ("TripId", "Id") ON DELETE CASCADE;

-- The composite foreign key now enforces trip membership through PackingList.
-- A direct NO ACTION/RESTRICT key could reject a trip deletion before that cascade runs.
DO $$
DECLARE direct_fk name;
BEGIN
    FOR direct_fk IN
        SELECT c.conname
        FROM pg_constraint c
        JOIN pg_attribute item_trip ON item_trip.attrelid = c.conrelid AND item_trip.attname = 'TripId'
        JOIN pg_attribute trip_id ON trip_id.attrelid = c.confrelid AND trip_id.attname = 'Id'
        WHERE c.contype = 'f'
          AND c.conrelid = 'public."PackingItem"'::regclass
          AND c.confrelid = 'public."Trip"'::regclass
          AND c.conkey = ARRAY[item_trip.attnum]
          AND c.confkey = ARRAY[trip_id.attnum]
    LOOP
        EXECUTE format('ALTER TABLE public."PackingItem" DROP CONSTRAINT %I', direct_fk);
    END LOOP;
END $$;

CREATE INDEX packing_item_list_idx ON public."PackingItem" ("TripId", "PackingListId");

ALTER TABLE public."PackingList" ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public."PackingList" FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, DELETE ON public."PackingList" TO authenticated;
GRANT UPDATE ("Name") ON public."PackingList" TO authenticated;

CREATE POLICY trip_owner_packing_lists ON public."PackingList"
    FOR ALL TO authenticated
    USING (EXISTS (
        SELECT 1 FROM public."Trip" t
        WHERE t."Id" = "PackingList"."TripId" AND t.user_id = (SELECT auth.uid())
    ))
    WITH CHECK (EXISTS (
        SELECT 1 FROM public."Trip" t
        WHERE t."Id" = "PackingList"."TripId" AND t.user_id = (SELECT auth.uid())
    ));

NOTIFY pgrst, 'reload schema';
COMMIT;
