BEGIN;

-- Existing trips acquire a zone on edit or when their first reminder is saved.
ALTER TABLE public."Trip" ADD COLUMN "TimeZoneId" text;

CREATE FUNCTION packo_private.validate_trip_time_zone() RETURNS trigger
LANGUAGE plpgsql SET search_path = '' AS $$
BEGIN
    IF NEW."TimeZoneId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_timezone_names WHERE name = NEW."TimeZoneId"
    ) THEN RAISE EXCEPTION 'Invalid trip time zone' USING ERRCODE = '22023'; END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER validate_trip_time_zone BEFORE INSERT OR UPDATE OF "TimeZoneId" ON public."Trip"
    FOR EACH ROW EXECUTE FUNCTION packo_private.validate_trip_time_zone();

CREATE TABLE packo_private.trip_reminder (
    id uuid PRIMARY KEY,
    trip_id bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    author_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    version bigint NOT NULL DEFAULT 1,
    definition jsonb NOT NULL
);
CREATE INDEX trip_reminder_trip_idx ON packo_private.trip_reminder(trip_id);
CREATE TABLE packo_private.reminder_preference (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    trip_id bigint NOT NULL REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    reminder_id text NOT NULL,
    muted boolean NOT NULL,
    PRIMARY KEY(user_id, trip_id, reminder_id)
);
CREATE TABLE packo_private.reminder_operation (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    operation_id uuid NOT NULL,
    result jsonb NOT NULL,
    PRIMARY KEY(user_id, operation_id)
);
REVOKE ALL ON packo_private.trip_reminder, packo_private.reminder_preference,
    packo_private.reminder_operation FROM PUBLIC, anon, authenticated;

CREATE TABLE public."TripReminderRevision" (
    "TripId" bigint PRIMARY KEY REFERENCES public."Trip" ("Id") ON DELETE CASCADE,
    "Revision" bigint NOT NULL DEFAULT 1
);
ALTER TABLE public."TripReminderRevision" ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public."TripReminderRevision" FROM PUBLIC, anon, authenticated;
GRANT SELECT ON public."TripReminderRevision" TO authenticated;
CREATE POLICY reminder_revision_read ON public."TripReminderRevision" FOR SELECT TO authenticated
    USING (packo_private.can_access_trip("TripId"));
ALTER PUBLICATION supabase_realtime ADD TABLE public."TripReminderRevision";

CREATE FUNCTION packo_private.reminder_visible(p_author uuid, p_definition jsonb, p_trip bigint)
RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
    SELECT packo_private.can_access_trip(p_trip) AND (
        p_author = auth.uid()
        OR (p_definition->>'Audience')::int = 1
        OR ((p_definition->>'Audience')::int = 2 AND
            (packo_private.is_trip_owner(p_trip) OR p_definition->'RecipientIds' ? auth.uid()::text))
    );
$$;

CREATE FUNCTION packo_private.signal_reminder_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE v_trip bigint;
BEGIN
    IF TG_TABLE_NAME = 'Trip' THEN v_trip := NEW."Id";
    ELSE v_trip := coalesce(to_jsonb(NEW)->>'trip_id', to_jsonb(OLD)->>'trip_id')::bigint;
    END IF;
    INSERT INTO public."TripReminderRevision" AS r ("TripId")
        SELECT v_trip WHERE EXISTS (SELECT 1 FROM public."Trip" WHERE "Id" = v_trip)
        ON CONFLICT ("TripId") DO UPDATE SET "Revision" = r."Revision" + 1;
    RETURN NULL;
END;
$$;
CREATE TRIGGER reminder_definition_changed AFTER INSERT OR UPDATE OR DELETE ON packo_private.trip_reminder
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_reminder_change();
CREATE TRIGGER reminder_preference_changed AFTER INSERT OR UPDATE OR DELETE ON packo_private.reminder_preference
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_reminder_change();
CREATE TRIGGER reminder_member_changed AFTER INSERT OR DELETE ON packo_private.trip_member
    FOR EACH ROW EXECUTE FUNCTION packo_private.signal_reminder_change();
CREATE TRIGGER reminder_trip_changed AFTER UPDATE OF "StartDate", "EndDate", "TimeZoneId", "IsInTrash"
    ON public."Trip" FOR EACH ROW EXECUTE FUNCTION packo_private.signal_reminder_change();

CREATE FUNCTION public.save_trip_reminder(p_trip_id bigint, p_operation_id uuid,
    p_expected_version bigint, p_definition jsonb, p_trip_zone text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE
    v_user uuid := auth.uid();
    v_id uuid;
    v_existing packo_private.trip_reminder%ROWTYPE;
    v_trip public."Trip"%ROWTYPE;
    v_definition jsonb;
    v_result jsonb;
    v_version bigint;
    v_author uuid;
    v_audience int;
    v_timing int;
    v_repeat int;
    v_clock int;
    v_offset int;
    v_minute int;
    v_first timestamp;
    v_first_instant timestamptz;
    v_end timestamptz;
    v_zone text;
BEGIN
    IF v_user IS NULL OR p_operation_id IS NULL OR p_expected_version IS NULL OR p_expected_version < 0
        OR jsonb_typeof(p_definition) IS DISTINCT FROM 'object'
        THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
    PERFORM pg_advisory_xact_lock(p_trip_id);
    SELECT result INTO v_result FROM packo_private.reminder_operation
        WHERE user_id = v_user AND operation_id = p_operation_id;
    IF FOUND THEN RETURN v_result; END IF;
    SELECT * INTO v_trip FROM public."Trip" WHERE "Id" = p_trip_id AND NOT "IsInTrash";
    IF NOT FOUND OR NOT packo_private.can_access_trip(p_trip_id) THEN
        RETURN jsonb_build_object('Status', 'denied');
    END IF;
    v_id := (p_definition->>'Id')::uuid;
    IF v_id IS NULL THEN RETURN jsonb_build_object('Status', 'invalid'); END IF;
    SELECT * INTO v_existing FROM packo_private.trip_reminder WHERE id = v_id;
    IF FOUND THEN
        IF v_existing.trip_id <> p_trip_id OR (v_existing.author_id <> v_user AND
            (NOT packo_private.is_trip_owner(p_trip_id) OR (v_existing.definition->>'Audience')::int = 0)) THEN
            RETURN jsonb_build_object('Status', 'denied');
        END IF;
        IF v_existing.version <> p_expected_version THEN
            RETURN jsonb_build_object('Status', 'conflict', 'Definition', v_existing.definition);
        END IF;
        v_author := v_existing.author_id;
        v_version := v_existing.version + 1;
    ELSE
        IF p_expected_version <> 0 THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
        v_author := v_user;
        v_version := 1;
    END IF;
    v_audience := (p_definition->>'Audience')::int;
    v_timing := (p_definition->>'Timing')::int;
    v_repeat := (p_definition->>'Repeat')::int;
    v_clock := (p_definition->>'Clock')::int;
    v_offset := (p_definition->>'Offset')::int;
    v_minute := (p_definition->>'MinuteOfDay')::int;
    IF v_audience IS NULL OR v_audience NOT BETWEEN 0 AND 2 OR v_timing IS NULL OR v_timing NOT BETWEEN 0 AND 2
        OR v_repeat IS NULL OR v_repeat NOT BETWEEN 0 AND 2 OR v_clock IS NULL OR v_clock NOT BETWEEN 0 AND 1
        OR v_offset IS NULL OR v_offset NOT BETWEEN 0 AND 8760 OR v_minute IS NULL OR v_minute NOT BETWEEN 0 AND 1439
        OR length(trim(coalesce(p_definition->>'Title', ''))) NOT BETWEEN 1 AND 80
        OR length(trim(coalesce(p_definition->>'Body', ''))) NOT BETWEEN 1 AND 500
        OR jsonb_typeof(p_definition->'Title') IS DISTINCT FROM 'string'
        OR jsonb_typeof(p_definition->'Body') IS DISTINCT FROM 'string'
        OR jsonb_typeof(p_definition->'Audience') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'Timing') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'Repeat') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'Clock') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'Offset') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'MinuteOfDay') IS DISTINCT FROM 'number'
        OR jsonb_typeof(p_definition->'RecipientIds') IS DISTINCT FROM 'array'
        OR jsonb_typeof(p_definition->'Enabled') IS DISTINCT FROM 'boolean'
        OR jsonb_typeof(p_definition->'Deleted') IS DISTINCT FROM 'boolean'
        OR p_definition->>'Date' IS NULL OR p_definition->>'TimeZoneId' IS NULL
        OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_definition->'RecipientIds') element
            WHERE jsonb_typeof(element) <> 'string')
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_timezone_names WHERE name = p_trip_zone)
        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_timezone_names WHERE name = p_definition->>'TimeZoneId')
    THEN RETURN jsonb_build_object('Status', 'invalid'); END IF;
    -- An owner can manage a participant's group reminder, but cannot turn it into a private one.
    IF v_author <> v_user AND v_audience = 0 THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
    IF v_audience = 2 AND (jsonb_array_length(p_definition->'RecipientIds') = 0 OR EXISTS (
        SELECT 1 FROM jsonb_array_elements_text(p_definition->'RecipientIds') recipient
        WHERE recipient NOT IN (
            SELECT user_id::text FROM packo_private.trip_member WHERE trip_id = p_trip_id
            UNION SELECT user_id::text FROM public."Trip" WHERE "Id" = p_trip_id
        )
    )) THEN RETURN jsonb_build_object('Status', 'denied'); END IF;
    IF v_trip."StartDate" IS NULL OR v_trip."EndDate" IS NULL OR v_trip."EndDate" <= v_trip."StartDate" THEN
        RETURN jsonb_build_object('Status', 'invalid');
    END IF;
    -- Capture an unambiguous zone for legacy trips instead of assigning it independently per recipient.
    UPDATE public."Trip" SET "TimeZoneId" = coalesce("TimeZoneId", p_trip_zone) WHERE "Id" = p_trip_id;
    v_zone := coalesce(v_trip."TimeZoneId", p_trip_zone);
    v_end := v_trip."EndDate" AT TIME ZONE v_zone;
    v_first := CASE v_timing
        WHEN 1 THEN ((v_trip."StartDate" AT TIME ZONE v_zone) - make_interval(hours => v_offset)) AT TIME ZONE v_zone
        WHEN 2 THEN date_trunc('day', v_trip."StartDate") - make_interval(days => v_offset)
            + make_interval(mins => v_minute)
        ELSE (p_definition->>'Date')::date + make_interval(mins => v_minute) END;
    v_first_instant := CASE WHEN v_timing = 1 THEN (v_trip."StartDate" AT TIME ZONE v_zone) - make_interval(hours => v_offset)
        ELSE v_first AT TIME ZONE CASE WHEN v_timing = 0 AND v_clock = 0
            THEN p_definition->>'TimeZoneId' ELSE v_zone END END;
    -- Floating schedules use the definition's creation zone only for validation. Devices
    -- resolve future occurrences in their current zone and always cap by the trip's instant.
    IF (p_definition->>'Enabled')::boolean AND NOT (p_definition->>'Deleted')::boolean AND (
        v_end <= now() OR v_first_instant > v_end
        OR (v_repeat = 0 AND v_first_instant <= now())
        OR (p_definition->>'RepeatUntil' IS NOT NULL AND (p_definition->>'RepeatUntil')::date < v_first::date)
        OR (v_repeat <> 0 AND p_definition->>'RepeatUntil' IS NOT NULL AND
            (((p_definition->>'RepeatUntil')::date + interval '1 day') AT TIME ZONE
                CASE WHEN v_timing = 0 AND v_clock = 0 THEN p_definition->>'TimeZoneId' ELSE v_zone END) <= now())
    ) THEN RETURN jsonb_build_object('Status', 'invalid'); END IF;
    v_definition := p_definition || jsonb_build_object('Id', v_id, 'Version', v_version, 'AuthorId', v_author,
        'Date', (p_definition->>'Date')::date::timestamp,
        'RepeatUntil', (p_definition->>'RepeatUntil')::date::timestamp,
        'AuthorName', (SELECT left(coalesce(raw_user_meta_data->>'full_name', raw_user_meta_data->>'name', ''),100)
            FROM auth.users WHERE id = v_author));
    INSERT INTO packo_private.trip_reminder(id, trip_id, author_id, version, definition)
        VALUES(v_id, p_trip_id, v_author, v_version, v_definition)
        ON CONFLICT(id) DO UPDATE SET definition = EXCLUDED.definition, version = EXCLUDED.version;
    v_result := jsonb_build_object('Status', 'ok', 'Definition', v_definition);
    INSERT INTO packo_private.reminder_operation VALUES(v_user, p_operation_id, v_result);
    RETURN v_result;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN
    RETURN jsonb_build_object('Status', 'invalid');
END;
$$;

CREATE FUNCTION public.mute_trip_reminder(p_trip_id bigint, p_reminder_id text, p_muted boolean)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
BEGIN
    IF auth.uid() IS NULL OR NOT packo_private.can_access_trip(p_trip_id) THEN RETURN false; END IF;
    IF p_reminder_id <> '*' AND NOT EXISTS (
        SELECT 1 FROM packo_private.trip_reminder r WHERE r.trip_id = p_trip_id AND r.id::text = p_reminder_id
        AND packo_private.reminder_visible(r.author_id, r.definition, r.trip_id)
    ) THEN RETURN false; END IF;
    INSERT INTO packo_private.reminder_preference VALUES(auth.uid(), p_trip_id, p_reminder_id, p_muted)
        ON CONFLICT(user_id, trip_id, reminder_id) DO UPDATE SET muted = EXCLUDED.muted;
    RETURN true;
END;
$$;

CREATE FUNCTION public.trip_reminders_snapshot() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
    SELECT jsonb_build_object(
        'Entries', coalesce((SELECT jsonb_agg(jsonb_build_object('Definition', r.definition,
            'Trip', jsonb_build_object('RemoteTripId', t."Id", 'StartDate', t."StartDate", 'EndDate', t."EndDate",
                'TimeZoneId', t."TimeZoneId", 'Destination', t."Destination", 'IsInTrash', t."IsInTrash",
                'IsJoinedTrip', t.user_id <> auth.uid(), 'RemoteUserId', t.user_id)))
            FROM packo_private.trip_reminder r JOIN public."Trip" t ON t."Id" = r.trip_id
            WHERE NOT t."IsInTrash" AND NOT (r.definition->>'Deleted')::boolean
            AND packo_private.reminder_visible(r.author_id, r.definition, r.trip_id)), '[]'::jsonb),
        'Preferences', coalesce((SELECT jsonb_agg(jsonb_build_object('TripId', trip_id,
            'ReminderId', reminder_id, 'Muted', muted)) FROM packo_private.reminder_preference
            WHERE user_id = auth.uid() AND packo_private.can_access_trip(trip_id)), '[]'::jsonb)
    );
$$;

REVOKE EXECUTE ON FUNCTION packo_private.validate_trip_time_zone(), packo_private.reminder_visible(uuid,jsonb,bigint),
    packo_private.signal_reminder_change(), public.save_trip_reminder(bigint,uuid,bigint,jsonb,text),
    public.mute_trip_reminder(bigint,text,boolean), public.trip_reminders_snapshot() FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.save_trip_reminder(bigint,uuid,bigint,jsonb,text),
    public.mute_trip_reminder(bigint,text,boolean), public.trip_reminders_snapshot() TO authenticated;
NOTIFY pgrst, 'reload schema';
COMMIT;
