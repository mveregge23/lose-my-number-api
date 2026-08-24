-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a recurring scan needs that a tenant-requested one does not: a way to find out
-- which accounts exist, and a guarantee that firing twice does not scan twice.
--
-- ---------------------------------------------------------------------------------
-- A third role, and why it is not simply dbr_app
-- ---------------------------------------------------------------------------------
--
-- Every operation so far has been on behalf of one account, which is what makes the
-- tenant boundary expressible: the request establishes who is asking, and the policies
-- answer for that one. A scheduler has no such caller. Before it can act for anybody it
-- has to ask a question no tenant-scoped role can answer — who is there — and dbr_app
-- reading the tenant table sees exactly one row, or none.
--
-- The tempting answers are both worse than a third role. Running the scheduler as the
-- owner would hand the process that enumerates accounts every privilege in the schema.
-- Giving dbr_app the ability to see every tenant would remove the boundary from the role
-- that serves ordinary traffic, in order to fix something ordinary traffic never does.
--
-- So dbr_scheduler exists, and is deliberately almost powerless. It reads one column of
-- one table. It cannot see an email address, cannot read a profile, cannot write
-- anything at all. The scheduler uses it for exactly one query — which accounts exist —
-- and then does all the work that has consequences through dbr_app, one account at a
-- time, inside the same boundary as every other write in this system. The privileged
-- step is one statement wide.
--
-- BYPASSRLS is deliberately not used. It is an attribute of the role rather than of a
-- table, so it would exempt this role from every policy on every table, present and
-- future, in order to relax one. A policy naming the role instead relaxes exactly what
-- it names, and a table added later is not silently included.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dbr_scheduler') THEN
        CREATE ROLE dbr_scheduler NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;

    -- NOLOGIN and reached by SET ROLE, as dbr_app and dbr_vault are, so this adds no
    -- second credential for a self-hoster to provision or rotate.
    EXECUTE format('GRANT dbr_scheduler TO %I', current_user);
END
$$;

COMMENT ON ROLE dbr_scheduler IS
    'Reads the list of account ids so recurring work can be planned, and nothing else. '
    'No email, no profile, no writes. Everything with a consequence runs as dbr_app for '
    'one account at a time.';

GRANT USAGE ON SCHEMA public TO dbr_scheduler;

-- Column-level, so the grant itself says what may be read. An account's email is on this
-- table and the scheduler has no use for it; a table-level grant would hand it over on
-- the grounds that the id happens to live next to it.
GRANT SELECT (id) ON tenant TO dbr_scheduler;

-- Permissive policies combine with OR, and the isolation policy applies to every role, so
-- this adds a second way for one role to pass rather than weakening the first. SELECT
-- only: there is no WITH CHECK here, so this grants no way to write a row.
CREATE POLICY scheduler_reads_every_account ON tenant
    FOR SELECT
    TO dbr_scheduler
    USING (true);

COMMENT ON POLICY scheduler_reads_every_account ON tenant IS
    'The one place the tenant boundary is relaxed, for the one role that has to plan work '
    'for accounts it is not acting for. Read-only, one column, and only for dbr_scheduler.';

-- ---------------------------------------------------------------------------------
-- Firing twice must not scan twice
-- ---------------------------------------------------------------------------------
--
-- A scheduler restarted mid-run, a misfire replayed, a second worker started by an
-- operator who did not know one was already running: all of them end with the same job
-- executing again on the same day. The scheduler checks before inserting, which handles
-- the ordinary case and does nothing at all for two of them checking at once.
--
-- The index is the actual guarantee. One scheduled scan per profile per UTC day — a
-- second insert is refused by the database rather than arriving as a duplicate somebody
-- notices later in their history. Manual scans are excluded: asking twice in a day is a
-- perfectly reasonable thing for a person to do, and this is not a rate limit.
--
-- The date is taken at UTC explicitly rather than by casting, because casting a
-- timestamptz to a date uses the session's TimeZone setting — which makes the expression
-- depend on who is connected, and is why Postgres will not index it.
CREATE UNIQUE INDEX scan_one_scheduled_per_profile_per_day
    ON scan (privacy_profile_id, ((requested_at AT TIME ZONE 'UTC')::date))
    WHERE trigger = 'scheduled';

COMMENT ON INDEX scan_one_scheduled_per_profile_per_day IS
    'One scheduled scan per identity per UTC day. What makes a replayed or doubled '
    'scheduler run harmless rather than merely unlikely. Manual scans are excluded — '
    'asking twice in a day is a person''s prerogative.';
