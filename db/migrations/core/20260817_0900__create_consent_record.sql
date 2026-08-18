-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a tenant has agreed the system may do on their behalf, and when they agreed it.
--
-- Three separate permissions rather than one: searching for somebody, opening removal
-- requests for what the search found, and opening them again when data reappears are
-- different things to agree to, and somebody who wants to see where they are listed
-- without anything being sent in their name has to be able to say exactly that.
--
-- The table is a history, not a switchboard. A revocation is a new row saying the
-- permission is no longer granted, on top of the row that granted it, rather than an
-- edit to the earlier one. This is the whole reason the table exists: the question that
-- gets asked later is not "may this run now" — a boolean column would answer that — but
-- "was this permitted at the time it ran, and under which policy", and an in-place
-- update destroys the only record that could answer it. Current state is the newest row
-- for a scope; scopes with no rows have never been granted.

CREATE TABLE consent_record (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    -- No ON DELETE CASCADE, matching privacy_profile and unlike passkeys and refresh
    -- tokens. Those are credentials, and an account that is gone has no use for them.
    -- This is evidence that a person permitted something, which account deletion has to
    -- deal with deliberately — purge or tombstone is a decision, not a side effect of
    -- the row it hangs off disappearing.
    tenant_id      uuid        NOT NULL REFERENCES tenant (id),

    -- Text with a check rather than a Postgres enum, as elsewhere: a value added to an
    -- enum type cannot be removed again, and this list is expected to be revisited.
    scope          text        NOT NULL
                               CONSTRAINT consent_record_scope_known
                               CHECK (scope IN ('scan', 'auto_removal', 'auto_resubmit')),

    granted        boolean     NOT NULL,

    effective_at   timestamptz NOT NULL DEFAULT now(),

    -- Which consent text was on screen when this was agreed to. A version rather than a
    -- boolean for the same reason the profile's attestation carries one: when the
    -- wording changes, what somebody actually agreed to is still answerable.
    policy_version text        NOT NULL
);

COMMENT ON TABLE consent_record IS
    'Append-only history of what a tenant has permitted, per scope. The newest row for '
    'a scope is the current state; earlier rows are what was true before, which is what '
    'makes "was this permitted when it ran" answerable.';

COMMENT ON COLUMN consent_record.effective_at IS
    'When the tenant made this decision. Also the ordering key: newest wins.';

-- Serves the "newest row per scope" read and makes the ordering total at the same time.
-- Unique rather than plain: two rows for one scope sharing a timestamp would leave
-- "current" depending on which one the planner happened to return first. Timestamps
-- here have microsecond resolution, so this only ever fires for two genuinely
-- concurrent decisions about the same permission — where one of them failing and being
-- retried is a better answer than an order nobody chose.
CREATE UNIQUE INDEX consent_record_current
    ON consent_record (tenant_id, scope, effective_at DESC);

CALL app.enable_tenant_rls('public.consent_record');

-- The application role may add rows and read them, and may not rewrite one. An UPDATE
-- is the operation that turns this table from a history into a switchboard: a revoke
-- written over the grant it replaces leaves a row that says the permission was never
-- held, which is both false and unnoticeable. Nothing in the application needs it, so
-- nothing in the application gets it, and a future mistake fails at the database rather
-- than silently succeeding.
--
-- DELETE is deliberately left in place. Account deletion has to actually erase, and
-- routing the one legitimate removal path around the application role would make
-- erasure the operation that needs special privileges.
REVOKE UPDATE ON consent_record FROM dbr_app;
