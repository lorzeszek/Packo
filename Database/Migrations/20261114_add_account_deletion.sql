BEGIN;

-- Older installations may have NO ACTION or SET NULL on these two keys.
DO $$
DECLARE v_table regclass; v_constraint name;
BEGIN
    FOREACH v_table IN ARRAY ARRAY['public."Trip"'::regclass, 'public."PackingItem"'::regclass]
    LOOP
        FOR v_constraint IN
            SELECT c.conname FROM pg_catalog.pg_constraint c
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.conrelid AND a.attname = 'user_id'
            JOIN pg_catalog.pg_attribute u ON u.attrelid = c.confrelid AND u.attname = 'id'
            WHERE c.contype = 'f' AND c.conrelid = v_table AND c.confrelid = 'auth.users'::regclass
                AND c.conkey = ARRAY[a.attnum] AND c.confkey = ARRAY[u.attnum]
        LOOP
            EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', v_table, v_constraint);
        END LOOP;
        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT account_user_fk FOREIGN KEY (user_id) REFERENCES auth.users(id) ON DELETE CASCADE', v_table);
    END LOOP;
END $$;

-- This also covers accounts deleted by an administrator in Supabase Auth.
CREATE FUNCTION packo_private.prepare_account_deletion() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE v_trip bigint;
BEGIN
    -- Sharing, packing and reminder RPCs use the same locks. Drain changes before cleanup.
    FOR v_trip IN
        SELECT DISTINCT t."Id" FROM public."Trip" t
        WHERE t.user_id = OLD.id
            OR EXISTS (SELECT 1 FROM packo_private.trip_member m WHERE m.trip_id = t."Id" AND m.user_id = OLD.id)
            OR EXISTS (SELECT 1 FROM public."PackingItem" i WHERE i."TripId" = t."Id" AND i.user_id = OLD.id)
            OR EXISTS (SELECT 1 FROM packo_private.trip_reminder r WHERE r.trip_id = t."Id"
                AND (r.author_id = OLD.id OR r.definition->'RecipientIds' ? OLD.id::text))
        ORDER BY t."Id"
    LOOP
        PERFORM pg_catalog.pg_advisory_xact_lock(v_trip);
    END LOOP;

    -- Contributions to someone else's packing lists stay with that trip.
    UPDATE public."PackingItem" i SET user_id = t.user_id
    FROM public."Trip" t
    WHERE i."TripId" = t."Id" AND i.user_id = OLD.id AND t.user_id <> OLD.id;

    -- Do not delete another author's notification just because this account received it.
    -- Advance both versions so an offline editor cannot restore the deleted recipient.
    UPDATE packo_private.trip_reminder
    SET definition = jsonb_set(jsonb_set(definition, '{RecipientIds}',
            (definition->'RecipientIds') - OLD.id::text), '{Version}', to_jsonb(version + 1)),
        version = version + 1
    WHERE author_id <> OLD.id AND definition->'RecipientIds' ? OLD.id::text;

    -- Idempotency results contain old definition snapshots, including personal details.
    DELETE FROM packo_private.reminder_operation
    WHERE result->'Definition'->>'AuthorId' = OLD.id::text
        OR result->'Definition'->'RecipientIds' ? OLD.id::text;

    -- Foreign keys remove owned trips (also trash), authored reminders, memberships,
    -- accepted invitations, preferences and operation receipts, plus Auth sessions/identities.
    RETURN OLD;
END;
$$;
REVOKE ALL ON FUNCTION packo_private.prepare_account_deletion() FROM PUBLIC, anon, authenticated;
CREATE TRIGGER prepare_account_deletion BEFORE DELETE ON auth.users
    FOR EACH ROW EXECUTE FUNCTION packo_private.prepare_account_deletion();

CREATE FUNCTION public.delete_my_account() RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE v_user uuid := auth.uid();
BEGIN
    IF v_user IS NULL THEN
        RAISE EXCEPTION 'Authentication required' USING ERRCODE = '42501';
    END IF;
    -- No caller-supplied ID. A retry after a lost response is intentionally harmless.
    DELETE FROM auth.users WHERE id = v_user;
    RETURN true;
END;
$$;
REVOKE ALL ON FUNCTION public.delete_my_account() FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.delete_my_account() TO authenticated;

NOTIFY pgrst, 'reload schema';
COMMIT;
