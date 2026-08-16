-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The role split the vault schema was created without: a second application role that
-- is the only one able to reach identifying data, and a day-to-day role that cannot.
--
-- Two roles, each blind to the other's tables:
--
--   dbr_app    serves ordinary traffic — accounts, sessions, jobs, catalog. It has no
--              USAGE on this schema, so a query that reaches a vault table through it
--              is refused by the database rather than returning ciphertext.
--   dbr_vault  reaches nothing but this schema. It is not granted USAGE on public, so
--              a query issued through it cannot join a profile's encrypted fields onto
--              the account, the scans, or anything else operational.
--
-- The second half is what makes "never joined into general query paths" a property of
-- the database instead of a habit. A join across the two stores does not silently
-- work today and break later when the vault moves to its own database; it does not
-- work now.
--
-- What this is not: a barrier against a process that can run arbitrary SQL. Both roles
-- are reached with SET ROLE over the same connection, and SET ROLE is judged against
-- the session user, so code holding the connection can switch between them at will.
-- What it stops is the ordinary failure — a query written against the wrong context, a
-- join added because the two tables happen to live in one database today. The
-- credential-level version of this boundary is already available and is a deployment
-- choice rather than a code change: the vault set has had its own connection string
-- since it was created, so pointing it at a different user, or a different database,
-- is one line of configuration.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dbr_vault') THEN
        CREATE ROLE dbr_vault NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;

    -- NOLOGIN and reached by SET ROLE, exactly as dbr_app is, so this introduces no
    -- second credential to provision or rotate for a self-hoster whose whole
    -- deployment is `docker compose up`.
    EXECUTE format('GRANT dbr_vault TO %I', current_user);
END
$$;

COMMENT ON ROLE dbr_vault IS
    'The role the profile service acts as. Reaches the vault schema and nothing else — '
    'no USAGE on public — so encrypted identity fields cannot be joined onto '
    'operational data.';

GRANT USAGE ON SCHEMA vault TO dbr_vault;

-- The tenant boundary applies in here too, and applies through the same accessor, so
-- a vault table's policy asks the same question the core tables do.
GRANT USAGE ON SCHEMA app TO dbr_vault;
GRANT EXECUTE ON FUNCTION app.current_tenant_id() TO dbr_vault;

-- Stated rather than assumed. Postgres grants no schema privileges to PUBLIC by
-- default, so this changes nothing today; it is here so that a later migration adding
-- a broad convenience grant somewhere has to argue with this line first.
REVOKE ALL ON SCHEMA vault FROM PUBLIC;
