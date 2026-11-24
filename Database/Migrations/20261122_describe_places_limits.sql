BEGIN;

-- Same budgets and counters as the pilot. Return a reason instead of treating a
-- short cooldown, an exhausted photo budget and all other limits as one error.
CREATE OR REPLACE FUNCTION public.places_reserve_request(p_user_id uuid, p_action text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE
    v_day date := (pg_catalog.now() AT TIME ZONE 'UTC')::date;
    v_user_limit integer; v_global_limit integer;
    v_global integer; v_user integer; v_last timestamptz;
    v_retry integer := greatest(1, ceil(extract(epoch FROM
        (((pg_catalog.now() AT TIME ZONE 'UTC')::date + 1)::timestamp AT TIME ZONE 'UTC') - pg_catalog.now()))::integer);
BEGIN
    CASE p_action
        WHEN 'search' THEN v_user_limit := 10; v_global_limit := 100;
        WHEN 'details' THEN v_user_limit := 20; v_global_limit := 100;
        WHEN 'photo' THEN v_user_limit := 10; v_global_limit := 20;
        WHEN 'website' THEN v_user_limit := 10; v_global_limit := 20;
        ELSE RETURN jsonb_build_object('allowed', false, 'reason', 'invalid_action');
    END CASE;
    IF NOT EXISTS (SELECT 1 FROM auth.users WHERE id = p_user_id) THEN
        RETURN jsonb_build_object('allowed', false, 'reason', 'invalid_user');
    END IF;
    INSERT INTO packo_private.places_global_usage(usage_day, action) VALUES(v_day, p_action) ON CONFLICT DO NOTHING;
    SELECT used INTO v_global FROM packo_private.places_global_usage
        WHERE usage_day = v_day AND action = p_action FOR UPDATE;
    IF v_global >= v_global_limit THEN
        RETURN jsonb_build_object('allowed', false, 'reason', 'daily_limit', 'scope', 'project',
            'limit', v_global_limit, 'used', v_global, 'retryAfter', v_retry);
    END IF;
    SELECT used, last_used_at INTO v_user, v_last FROM packo_private.places_user_usage
        WHERE user_id = p_user_id AND usage_day = v_day AND action = p_action FOR UPDATE;
    IF coalesce(v_user, 0) >= v_user_limit THEN
        RETURN jsonb_build_object('allowed', false, 'reason', 'daily_limit', 'scope', 'account',
            'limit', v_user_limit, 'used', v_user, 'retryAfter', v_retry);
    END IF;
    IF v_last > pg_catalog.now() - interval '2 seconds' THEN
        RETURN jsonb_build_object('allowed', false, 'reason', 'cooldown', 'retryAfter', 2);
    END IF;
    INSERT INTO packo_private.places_user_usage(user_id, usage_day, action, used)
        VALUES(p_user_id, v_day, p_action, 1)
        ON CONFLICT (user_id, usage_day, action) DO UPDATE
        SET used = places_user_usage.used + 1, last_used_at = pg_catalog.now();
    UPDATE packo_private.places_global_usage SET used = used + 1 WHERE usage_day = v_day AND action = p_action;
    DELETE FROM packo_private.places_user_usage WHERE usage_day < v_day - 35;
    DELETE FROM packo_private.places_global_usage WHERE usage_day < v_day - 35;
    RETURN jsonb_build_object('allowed', true);
END;
$$;
REVOKE ALL ON FUNCTION public.places_reserve_request(uuid, text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.places_reserve_request(uuid, text) TO service_role;

-- Keep the old Edge Function compatible during rollout. No counters are reset.
CREATE OR REPLACE FUNCTION public.places_consume_quota(p_user_id uuid, p_action text)
RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path = '' AS $$
    SELECT coalesce((public.places_reserve_request(p_user_id, p_action)->>'allowed')::boolean, false)
$$;
REVOKE ALL ON FUNCTION public.places_consume_quota(uuid, text) FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.places_consume_quota(uuid, text) TO service_role;
NOTIFY pgrst, 'reload schema';
COMMIT;
