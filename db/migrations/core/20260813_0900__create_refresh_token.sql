-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Refresh tokens: the half of a session that can be taken away.
--
-- Access tokens are signed, short-lived, and checked without asking the database
-- anything — which is what makes them fast and also what makes them impossible to
-- revoke. The revocable half lives here. Signing out, detecting a stolen token, and
-- capping how long a session may live are all operations on these rows.

CREATE TABLE refresh_token (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid        NOT NULL REFERENCES tenant (id) ON DELETE CASCADE,

    -- The SHA-256 of the token, never the token. A refresh token is a bearer
    -- credential: whoever holds it can mint access tokens until it expires, so a
    -- database dump containing them would be a dump containing live sessions. Hashing
    -- makes this column useless to whoever reads it.
    --
    -- A plain digest rather than a password hash, deliberately. Slow hashing exists to
    -- make guessing a low-entropy human-chosen secret expensive; these are 256 bits
    -- from a CSPRNG, so there is nothing to guess and the cost would be paid on every
    -- refresh for nothing.
    token_hash         bytea       NOT NULL,

    -- Every token descended from one sign-in shares this. Rotation replaces a token
    -- but keeps the session, so this is what makes "sign this session out" and "this
    -- token was stolen, kill everything it came from" expressible as a single
    -- statement.
    session_id         uuid        NOT NULL,

    -- When the sign-in behind this session happened — copied forward across
    -- rotations, not refreshed by them. Without it a token that keeps being rotated
    -- never expires: each rotation extends the deadline, so a session that is stolen
    -- and kept warm outlives every window meant to contain it.
    session_started_at timestamptz NOT NULL,

    created_at         timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,

    -- Set when this token is exchanged for its successor. It is not deleted, and that
    -- is the point: a token presented after it has been spent is either a replay or a
    -- copy in somebody else's hands, and a deleted row cannot tell the difference
    -- between that and a token that never existed.
    used_at            timestamptz,

    -- Set when the token is deliberately invalidated — signing out, or the whole
    -- session being torn down after a spent token reappeared.
    revoked_at         timestamptz
);

COMMENT ON TABLE refresh_token IS
    'The revocable half of a session. Stores digests, never tokens; rotated on every '
    'use, and grouped by session_id so one stolen token invalidates everything that '
    'came from the same sign-in.';

COMMENT ON COLUMN refresh_token.used_at IS
    'When this token was exchanged for its successor. Spent rows are kept so that '
    'presenting one again is recognisable as theft rather than as an unknown token.';

-- The lookup every refresh performs, and unique because two rows answering to one
-- token would make "which session is this?" ambiguous at the moment that question has
-- to be answered exactly.
CREATE UNIQUE INDEX refresh_token_token_hash_unique ON refresh_token (token_hash);

-- Revoking a session touches every row in it.
CREATE INDEX refresh_token_session_id ON refresh_token (session_id);

-- Sweeping tokens nobody can use any more.
CREATE INDEX refresh_token_expires_at ON refresh_token (expires_at);

CALL app.enable_tenant_rls('public.refresh_token');

-- ---------------------------------------------------------------------------------
-- The way in, again
-- ---------------------------------------------------------------------------------

-- Refreshing has the same shape as signing in, and needs the same narrow exception.
-- The caller's access token has expired — that is why they are here — so they are
-- acting for no tenant, so every policy on this table matches nothing, so the row
-- that would tell us who they are is invisible.
--
-- The argument is what makes it safe, exactly as with app.find_passkey. A refresh
-- token is 256 bits from a CSPRNG and this takes its digest, so supplying one means
-- holding it; there is no dictionary to walk and nothing here a guess can reach.
--
-- It returns the session's own state and nothing about the account beyond which one
-- it is. A caller learning that a token they already hold is revoked or spent learns
-- nothing they could not infer from being refused.
CREATE FUNCTION app.find_refresh_token(lookup_token_hash bytea)
    RETURNS TABLE (
        id uuid,
        tenant_id uuid,
        session_id uuid,
        session_started_at timestamptz,
        expires_at timestamptz,
        used_at timestamptz,
        revoked_at timestamptz)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    -- Everything below is schema-qualified and this path holds nothing else, so no
    -- object the body names can be shadowed by a caller-controlled schema.
    SET search_path = pg_catalog
AS $$
    SELECT token.id,
           token.tenant_id,
           token.session_id,
           token.session_started_at,
           token.expires_at,
           token.used_at,
           token.revoked_at
    FROM public.refresh_token AS token
    WHERE token.token_hash = lookup_token_hash
$$;

COMMENT ON FUNCTION app.find_refresh_token(bytea) IS
    'Resolve a refresh token to its session before the caller is acting for any '
    'tenant. Narrow by construction: it answers only to a token digest, and returns '
    'only the session state deciding whether the exchange may proceed.';

REVOKE ALL ON FUNCTION app.find_refresh_token(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.find_refresh_token(bytea) TO dbr_app;

-- As with passkeys, there is no definer function that writes. Once the token
-- resolves, the tenant is known, so rotating and revoking go through the ordinary
-- policy-enforced path.
