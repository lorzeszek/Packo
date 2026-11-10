BEGIN;

CREATE SCHEMA IF NOT EXISTS packo_private;
REVOKE ALL ON SCHEMA packo_private FROM PUBLIC;
GRANT USAGE ON SCHEMA packo_private TO authenticated;

CREATE TABLE packo_private.trip_member (
    trip_id bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    user_id uuid NOT NULL REFERENCES auth.users (id) ON DELETE CASCADE,
    PRIMARY KEY (trip_id, user_id)
);
CREATE INDEX trip_member_user_idx ON packo_private.trip_member (user_id, trip_id);

CREATE TABLE packo_private.trip_invite (
    token_hash bytea PRIMARY KEY,
    trip_id bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    accepted_by uuid REFERENCES auth.users (id) ON DELETE CASCADE
);
CREATE INDEX trip_invite_trip_idx ON packo_private.trip_invite (trip_id);

CREATE TABLE packo_private.packing_operation (
    user_id uuid NOT NULL REFERENCES auth.users (id) ON DELETE CASCADE,
    operation_id uuid NOT NULL,
    trip_id bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    result jsonb NOT NULL,
    PRIMARY KEY (user_id, operation_id)
);
CREATE INDEX packing_operation_trip_idx ON packo_private.packing_operation (trip_id);
REVOKE ALL ON ALL TABLES IN SCHEMA packo_private FROM PUBLIC, anon, authenticated;

CREATE FUNCTION packo_private.is_trip_owner(p_trip_id bigint) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = ''
AS $$
    SELECT EXISTS (SELECT 1 FROM public."Trip" WHERE "Id" = p_trip_id AND user_id = auth.uid());
$$;

CREATE FUNCTION packo_private.can_access_trip(p_trip_id bigint) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = ''
AS $$
    SELECT EXISTS (
        SELECT 1 FROM public."Trip" t WHERE t."Id" = p_trip_id
        AND (t.user_id = auth.uid() OR (NOT t."IsInTrash" AND EXISTS (
            SELECT 1 FROM packo_private.trip_member m WHERE m.trip_id = p_trip_id AND m.user_id = auth.uid()
        )))
    );
$$;

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA packo_private FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION packo_private.is_trip_owner(bigint), packo_private.can_access_trip(bigint) TO authenticated;

ALTER TABLE public."Trip" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."PackingItem" ENABLE ROW LEVEL SECURITY;
GRANT SELECT, INSERT, UPDATE, DELETE ON public."Trip", public."PackingItem" TO authenticated;
REVOKE ALL ON public."Trip", public."PackingItem" FROM anon, PUBLIC;

-- Restrictive policies also constrain older permissive policies in deployed projects.
CREATE POLICY trip_access_guard ON public."Trip" AS RESTRICTIVE FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("Id"));
CREATE POLICY trip_access_read ON public."Trip" FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("Id"));
CREATE POLICY trip_owner_insert_guard ON public."Trip" AS RESTRICTIVE FOR INSERT TO authenticated
    WITH CHECK (user_id = (SELECT auth.uid()));
CREATE POLICY trip_owner_update_guard ON public."Trip" AS RESTRICTIVE FOR UPDATE TO authenticated
    USING (user_id = (SELECT auth.uid())) WITH CHECK (user_id = (SELECT auth.uid()));
CREATE POLICY trip_owner_delete_guard ON public."Trip" AS RESTRICTIVE FOR DELETE TO authenticated
    USING (user_id = (SELECT auth.uid()));
CREATE POLICY trip_owner_write ON public."Trip" FOR ALL TO authenticated
    USING (user_id = (SELECT auth.uid())) WITH CHECK (user_id = (SELECT auth.uid()));

DROP POLICY trip_owner_packing_lists ON public."PackingList";
CREATE POLICY packing_lists_access_guard ON public."PackingList" AS RESTRICTIVE FOR ALL TO authenticated
    USING (packo_private.can_access_trip("TripId")) WITH CHECK (packo_private.can_access_trip("TripId"));
CREATE POLICY packing_lists_access ON public."PackingList" FOR ALL TO authenticated
    USING (packo_private.can_access_trip("TripId")) WITH CHECK (packo_private.can_access_trip("TripId"));

CREATE POLICY packing_items_access_guard ON public."PackingItem" AS RESTRICTIVE FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("TripId"));
CREATE POLICY packing_items_access_read ON public."PackingItem" FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("TripId"));
-- Legacy direct writers retain access to private trips. Shared writes use the operation RPC.
CREATE FUNCTION packo_private.can_write_packing_directly(p_trip_id bigint) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = ''
AS $$
    SELECT packo_private.is_trip_owner(p_trip_id) AND NOT EXISTS (
        SELECT 1 FROM packo_private.trip_member WHERE trip_id = p_trip_id
    );
$$;
REVOKE EXECUTE ON FUNCTION packo_private.can_write_packing_directly(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION packo_private.can_write_packing_directly(bigint) TO authenticated;
CREATE POLICY packing_items_direct_insert_guard ON public."PackingItem" AS RESTRICTIVE FOR INSERT TO authenticated
    WITH CHECK (packo_private.can_write_packing_directly("TripId") AND user_id = (SELECT auth.uid()));
CREATE POLICY packing_items_direct_update_guard ON public."PackingItem" AS RESTRICTIVE FOR UPDATE TO authenticated
    USING (packo_private.can_write_packing_directly("TripId"))
    WITH CHECK (packo_private.can_write_packing_directly("TripId") AND user_id = (SELECT auth.uid()));
CREATE POLICY packing_items_direct_delete_guard ON public."PackingItem" AS RESTRICTIVE FOR DELETE TO authenticated
    USING (packo_private.can_write_packing_directly("TripId"));

ALTER TABLE public."PackingItem" ADD COLUMN "NameVersion" bigint NOT NULL DEFAULT 0,
    ADD COLUMN "QuantityVersion" bigint NOT NULL DEFAULT 0,
    ADD COLUMN "IsPackedVersion" bigint NOT NULL DEFAULT 0;

CREATE FUNCTION packo_private.version_packing_item() RETURNS trigger
LANGUAGE plpgsql SET search_path = ''
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW."NameVersion" := 0;
        NEW."QuantityVersion" := 0;
        NEW."IsPackedVersion" := 0;
    ELSE
        NEW."NameVersion" := OLD."NameVersion" + (NEW."Name" IS DISTINCT FROM OLD."Name")::int;
        NEW."QuantityVersion" := OLD."QuantityVersion" + (NEW."Quantity" IS DISTINCT FROM OLD."Quantity")::int;
        NEW."IsPackedVersion" := OLD."IsPackedVersion" + (NEW."IsPacked" IS DISTINCT FROM OLD."IsPacked")::int;
    END IF;
    RETURN NEW;
END;
$$;
REVOKE EXECUTE ON FUNCTION packo_private.version_packing_item() FROM PUBLIC;
CREATE TRIGGER version_packing_item BEFORE INSERT OR UPDATE ON public."PackingItem"
    FOR EACH ROW EXECUTE FUNCTION packo_private.version_packing_item();

CREATE TABLE public."TripPackingRevision" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TripId" bigint NOT NULL UNIQUE REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    "Revision" bigint NOT NULL DEFAULT 1
);
ALTER TABLE public."TripPackingRevision" ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public."TripPackingRevision" FROM PUBLIC, anon, authenticated;
GRANT SELECT ON public."TripPackingRevision" TO authenticated;
CREATE POLICY trip_revision_read ON public."TripPackingRevision" FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("TripId"));
ALTER PUBLICATION supabase_realtime ADD TABLE public."TripPackingRevision";

CREATE FUNCTION packo_private.signal_packing_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = ''
AS $$
DECLARE
    v_row jsonb := to_jsonb(CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END);
    v_trip_id bigint := coalesce(v_row->>'TripId', v_row->>'trip_id')::bigint;
BEGIN
    INSERT INTO public."TripPackingRevision" AS r ("TripId")
    SELECT v_trip_id WHERE EXISTS (SELECT 1 FROM public."Trip" WHERE "Id" = v_trip_id)
    ON CONFLICT ("TripId") DO UPDATE SET "Revision" = r."Revision" + 1;
    RETURN NULL;
END;
$$;
REVOKE EXECUTE ON FUNCTION packo_private.signal_packing_change() FROM PUBLIC;
CREATE TRIGGER signal_item_change AFTER INSERT OR UPDATE OR DELETE ON public."PackingItem"
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_packing_change();
CREATE TRIGGER signal_list_change AFTER INSERT OR UPDATE OR DELETE ON public."PackingList"
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_packing_change();
CREATE TRIGGER signal_member_change AFTER INSERT OR DELETE ON packo_private.trip_member
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_packing_change();

CREATE FUNCTION public.create_trip_invite(p_trip_id bigint, p_token uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = ''
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(p_trip_id);
    IF p_token IS NULL OR NOT EXISTS (
        SELECT 1 FROM public."Trip" WHERE "Id" = p_trip_id AND user_id = auth.uid() AND NOT "IsInTrash"
    ) THEN
        RETURN false;
    END IF;
    INSERT INTO packo_private.trip_invite (token_hash, trip_id, expires_at)
    VALUES (sha256(uuid_send(p_token)), p_trip_id, now() + interval '7 days');
    RETURN true;
END;
$$;

CREATE FUNCTION public.accept_trip_invite(p_token uuid) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = ''
AS $$
DECLARE
    v_user uuid := auth.uid();
    v_invite packo_private.trip_invite%ROWTYPE;
    v_trip_id bigint;
BEGIN
    IF v_user IS NULL THEN RETURN 'denied'; END IF;
    SELECT trip_id INTO v_trip_id FROM packo_private.trip_invite WHERE token_hash = sha256(uuid_send(p_token));
    IF NOT FOUND THEN RETURN 'invalid'; END IF;
    PERFORM pg_advisory_xact_lock(v_trip_id);
    SELECT * INTO v_invite FROM packo_private.trip_invite
        WHERE token_hash = sha256(uuid_send(p_token)) FOR UPDATE;
    IF NOT FOUND OR NOT EXISTS (
        SELECT 1 FROM public."Trip" WHERE "Id" = v_trip_id AND NOT "IsInTrash"
    ) THEN RETURN 'invalid'; END IF;
    IF v_invite.accepted_by = v_user AND packo_private.can_access_trip(v_trip_id) THEN RETURN 'joined'; END IF;
    IF v_invite.accepted_by IS NOT NULL OR v_invite.expires_at <= now() THEN RETURN 'invalid'; END IF;
    IF NOT packo_private.is_trip_owner(v_trip_id) THEN
        INSERT INTO packo_private.trip_member (trip_id, user_id) VALUES (v_trip_id, v_user) ON CONFLICT DO NOTHING;
        UPDATE packo_private.trip_invite SET accepted_by = v_user WHERE token_hash = v_invite.token_hash;
    END IF;
    RETURN 'joined';
END;
$$;

CREATE FUNCTION public.revoke_trip_access(p_trip_id bigint, p_user_id uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = ''
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(p_trip_id);
    IF NOT packo_private.is_trip_owner(p_trip_id) THEN RETURN false; END IF;
    IF p_user_id IS NULL THEN
        DELETE FROM packo_private.trip_invite WHERE trip_id = p_trip_id AND accepted_by IS NULL;
    ELSE
        DELETE FROM packo_private.trip_member WHERE trip_id = p_trip_id AND user_id = p_user_id;
    END IF;
    RETURN true;
END;
$$;

CREATE FUNCTION public.trip_participants() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = ''
AS $$
    SELECT coalesce(jsonb_agg(jsonb_build_object('TripId', people.trip_id, 'UserId', u.id,
        'IsOwner', people.is_owner, 'Name', left(coalesce(u.raw_user_meta_data->>'full_name',
            u.raw_user_meta_data->>'name', ''), 100),
        'AvatarUrl', coalesce(u.raw_user_meta_data->>'avatar_url', u.raw_user_meta_data->>'picture', ''))), '[]'::jsonb)
    FROM (
        SELECT m.trip_id, m.user_id, false AS is_owner FROM packo_private.trip_member m
        WHERE packo_private.can_access_trip(m.trip_id)
        UNION
        SELECT t."Id", t.user_id, true FROM public."Trip" t
        WHERE packo_private.can_access_trip(t."Id") AND EXISTS (
            SELECT 1 FROM packo_private.trip_member m WHERE m.trip_id = t."Id"
        )
    ) people JOIN auth.users u ON u.id = people.user_id;
$$;

CREATE FUNCTION public.packing_trip_stats() RETURNS jsonb
LANGUAGE sql STABLE SECURITY INVOKER SET search_path = ''
AS $$
    SELECT coalesce(jsonb_agg(jsonb_build_object('TripId', s.trip_id, 'Packed', s.packed, 'Total', s.total)), '[]'::jsonb)
    FROM (
        SELECT "TripId" AS trip_id, count(*) FILTER (WHERE "IsPacked") AS packed, count(*) AS total
        FROM public."PackingItem" GROUP BY "TripId"
    ) s;
$$;

CREATE FUNCTION public.apply_packing_operation(
    p_trip_id bigint, p_operation_id uuid, p_operation text, p_item_id bigint, p_list_id uuid,
    p_changes jsonb, p_before jsonb, p_created_at timestamp
) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = ''
AS $$
DECLARE
    v_user uuid := auth.uid();
    v_item public."PackingItem"%ROWTYPE;
    v_result jsonb;
    v_status text := 'ok';
    v_fields text[];
    v_field text;
    v_conflicts text[] := ARRAY[]::text[];
    v_patch jsonb := '{}'::jsonb;
BEGIN
    IF v_user IS NULL OR p_operation_id IS NULL THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
    PERFORM pg_advisory_xact_lock(p_trip_id);
    IF NOT packo_private.can_access_trip(p_trip_id) THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
    SELECT result INTO v_result FROM packo_private.packing_operation
        WHERE user_id = v_user AND operation_id = p_operation_id AND trip_id = p_trip_id;
    IF FOUND THEN RETURN v_result; END IF;

    IF p_operation = 'Add' THEN
        PERFORM 1 FROM public."PackingList" WHERE "TripId" = p_trip_id AND "Id" = p_list_id FOR KEY SHARE;
        IF NOT FOUND THEN
            v_status := 'missing';
        ELSE
            INSERT INTO public."PackingItem" ("TripId", "PackingListId", "Name", "Quantity", "IsPacked", "CreatedDate", user_id)
            VALUES (p_trip_id, p_list_id, p_changes->>'Name', (p_changes->>'Quantity')::int,
                (p_changes->>'IsPacked')::boolean, p_created_at, v_user)
            RETURNING * INTO v_item;
        END IF;
    ELSIF p_operation = 'Delete' THEN
        DELETE FROM public."PackingItem" WHERE "TripId" = p_trip_id AND "Id" = p_item_id;
    ELSIF p_operation = 'Update' THEN
        SELECT * INTO v_item FROM public."PackingItem" WHERE "TripId" = p_trip_id AND "Id" = p_item_id FOR UPDATE;
        IF NOT FOUND THEN
            v_status := 'missing';
        ELSE
            v_fields := ARRAY['Name', 'Quantity', 'IsPacked'];
            FOREACH v_field IN ARRAY v_fields LOOP
                IF p_changes ? v_field AND to_jsonb(v_item)->v_field IS DISTINCT FROM p_changes->v_field THEN
                    IF (p_before IS NULL AND packo_private.can_write_packing_directly(p_trip_id))
                        OR (to_jsonb(v_item)->v_field IS NOT DISTINCT FROM p_before->v_field
                            AND to_jsonb(v_item)->(v_field || 'Version') = p_before->(v_field || 'Version')) THEN
                        v_patch := v_patch || jsonb_build_object(v_field, p_changes->v_field);
                    ELSE
                        v_conflicts := array_append(v_conflicts, v_field);
                    END IF;
                END IF;
            END LOOP;
            IF v_patch <> '{}'::jsonb THEN
                UPDATE public."PackingItem" SET "Name" = coalesce(v_patch->>'Name', "Name"),
                    "Quantity" = coalesce((v_patch->>'Quantity')::int, "Quantity"),
                    "IsPacked" = coalesce((v_patch->>'IsPacked')::boolean, "IsPacked")
                WHERE "TripId" = p_trip_id AND "Id" = p_item_id RETURNING * INTO v_item;
            END IF;
            IF cardinality(v_conflicts) > 0 THEN v_status := 'conflict'; END IF;
        END IF;
    ELSE
        RAISE EXCEPTION 'Invalid packing operation';
    END IF;

    v_result := jsonb_build_object('Status', v_status, 'ItemId', v_item."Id");
    INSERT INTO packo_private.packing_operation VALUES (v_user, p_operation_id, p_trip_id, v_result);
    RETURN v_result;
END;
$$;

REVOKE EXECUTE ON FUNCTION public.create_trip_invite(bigint, uuid), public.accept_trip_invite(uuid),
    public.revoke_trip_access(bigint, uuid), public.trip_participants(), public.packing_trip_stats(),
    public.apply_packing_operation(bigint, uuid, text, bigint, uuid, jsonb, jsonb, timestamp)
    FROM PUBLIC, anon;
GRANT EXECUTE ON FUNCTION public.create_trip_invite(bigint, uuid), public.accept_trip_invite(uuid),
    public.revoke_trip_access(bigint, uuid), public.trip_participants(), public.packing_trip_stats(),
    public.apply_packing_operation(bigint, uuid, text, bigint, uuid, jsonb, jsonb, timestamp)
    TO authenticated;

NOTIFY pgrst, 'reload schema';
COMMIT;
