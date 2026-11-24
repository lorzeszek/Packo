BEGIN;
CREATE SCHEMA IF NOT EXISTS packo_private;

CREATE TABLE IF NOT EXISTS packo_private.places_user_usage (
    user_id uuid NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    usage_day date NOT NULL,
    action text NOT NULL CHECK (action IN ('search', 'details', 'website', 'photo')),
    used integer NOT NULL DEFAULT 0 CHECK (used >= 0),
    last_used_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, usage_day, action)
);
CREATE TABLE IF NOT EXISTS packo_private.places_global_usage (
    usage_day date NOT NULL,
    action text NOT NULL CHECK (action IN ('search', 'details', 'website', 'photo')),
    used integer NOT NULL DEFAULT 0 CHECK (used >= 0),
    PRIMARY KEY (usage_day, action)
);
ALTER TABLE packo_private.places_user_usage ENABLE ROW LEVEL SECURITY;
ALTER TABLE packo_private.places_global_usage ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON packo_private.places_user_usage, packo_private.places_global_usage FROM PUBLIC, anon, authenticated;

-- Only the server may debit quota, after verifying the caller with Supabase Auth.
-- The global pilot caps limit all users together, including simultaneous requests.
CREATE OR REPLACE FUNCTION public.places_consume_quota(p_user_id uuid, p_action text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE
    v_day date := (pg_catalog.now() AT TIME ZONE 'UTC')::date;
    v_user_limit integer; v_global_limit integer;
    v_global integer; v_user integer; v_last timestamptz;
BEGIN
    CASE p_action
        WHEN 'search' THEN v_user_limit := 10; v_global_limit := 100;
        WHEN 'details' THEN v_user_limit := 20; v_global_limit := 100;
        WHEN 'photo' THEN v_user_limit := 10; v_global_limit := 20;
        WHEN 'website' THEN v_user_limit := 10; v_global_limit := 20;
        ELSE RETURN false;
    END CASE;
    IF NOT EXISTS (SELECT 1 FROM auth.users WHERE id = p_user_id) THEN RETURN false; END IF;
    INSERT INTO packo_private.places_global_usage(usage_day, action) VALUES(v_day, p_action) ON CONFLICT DO NOTHING;
    SELECT used INTO v_global FROM packo_private.places_global_usage
        WHERE usage_day = v_day AND action = p_action FOR UPDATE;
    IF v_global >= v_global_limit THEN RETURN false; END IF;
    SELECT used, last_used_at INTO v_user, v_last FROM packo_private.places_user_usage
        WHERE user_id = p_user_id AND usage_day = v_day AND action = p_action FOR UPDATE;
    IF coalesce(v_user, 0) >= v_user_limit OR v_last > pg_catalog.now() - interval '2 seconds' THEN RETURN false; END IF;
    INSERT INTO packo_private.places_user_usage(user_id, usage_day, action, used)
        VALUES(p_user_id, v_day, p_action, 1)
        ON CONFLICT (user_id, usage_day, action) DO UPDATE
        SET used = places_user_usage.used + 1, last_used_at = pg_catalog.now();
    UPDATE packo_private.places_global_usage SET used = used + 1 WHERE usage_day = v_day AND action = p_action;
    DELETE FROM packo_private.places_user_usage WHERE usage_day < v_day - 35;
    DELETE FROM packo_private.places_global_usage WHERE usage_day < v_day - 35;
    RETURN true;
END;
$$;
REVOKE ALL ON FUNCTION public.places_consume_quota(uuid, text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.places_consume_quota(uuid, text) TO service_role;
NOTIFY pgrst, 'reload schema';
COMMIT;
