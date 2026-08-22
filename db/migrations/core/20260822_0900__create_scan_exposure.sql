-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Scans and what they found.
--
-- A scan asks a set of brokers what they hold about one identity; an exposure is one
-- broker's answer being yes. These are the first tenant-scoped tables since consent, and
-- the first anywhere in this schema that reference another tenant-scoped table rather
-- than only the tenant.
--
-- ---------------------------------------------------------------------------------
-- Why the foreign keys are over pairs
-- ---------------------------------------------------------------------------------
--
-- Row-level security decides which rows a statement can see and write. It does not
-- police referential integrity: Postgres checks a foreign key with row security off, as
-- the referencing table's owner, because a constraint that could be satisfied or not
-- depending on who is asking would not be a constraint. That is the right behaviour and
-- it leaves a specific hole here.
--
-- A scan naming a privacy_profile by id alone would pass its foreign key against any
-- profile in the table, including one belonging to somebody else. The policy would stop
-- that tenant reading the row back, so nothing would look wrong — but the row would
-- exist, and the thing it records is a request to go and search the internet for another
-- person. This is the exact risk §10.4 is about, and a check in application code is the
-- weaker answer: it holds until one write path forgets it, and forgetting is silent.
--
-- So every reference to a tenant-scoped parent is a foreign key over (tenant_id, id),
-- against a unique constraint on the same pair. A child can then only point at a parent
-- belonging to the same tenant, and it is the database saying so. The tenant_id on a
-- child row stops being redundant with its parent's and becomes the thing that ties the
-- two together.
--
-- privacy_profile predates this and gets its half of the pair here.

ALTER TABLE privacy_profile
    ADD CONSTRAINT privacy_profile_tenant_scoped UNIQUE (tenant_id, id);

COMMENT ON CONSTRAINT privacy_profile_tenant_scoped ON privacy_profile IS
    'Target for composite foreign keys from tenant-scoped children. Redundant with the '
    'primary key for uniqueness, and load-bearing for the guarantee that a child row '
    'cannot reference a profile belonging to a different tenant.';

-- ---------------------------------------------------------------------------------

CREATE TABLE scan (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tenant_id          uuid        NOT NULL REFERENCES tenant (id),

    -- What is being searched for, named and never described. There is no column here
    -- for a name, an address or a date of birth, and there is no endpoint that accepts
    -- one: the identity is always a profile the tenant already created under their own
    -- account. That is the structural half of §10.4 — anything that let this be an
    -- arbitrary identity would turn a removal tool into a people-search tool, and a
    -- shape the schema cannot express is a stronger guarantee than a rule checked at
    -- runtime.
    privacy_profile_id uuid        NOT NULL,

    -- Text with a check rather than a Postgres enum, as elsewhere: a value added to an
    -- enum type cannot be removed again, and this list is expected to be revisited.
    trigger            text        NOT NULL
                                   CONSTRAINT scan_trigger_known
                                   CHECK (trigger IN ('manual', 'scheduled')),

    status             text        NOT NULL
                                   CONSTRAINT scan_status_known
                                   CHECK (status IN ('queued', 'running', 'completed', 'failed')),

    -- Asked for, started, stopped: three separate facts, and two of them are unknown
    -- when the row is written. Collapsing requested_at and started_at into one column
    -- would make every queued scan claim a start time it does not have, and would hide
    -- the difference between a run that sat in a queue and one that ran slowly — which
    -- is the difference somebody is looking for when they ask why a scan took a day.
    requested_at       timestamptz NOT NULL DEFAULT now(),
    started_at         timestamptz NULL,
    completed_at       timestamptz NULL,

    CONSTRAINT scan_tenant_scoped UNIQUE (tenant_id, id),

    CONSTRAINT scan_profile_same_tenant
        FOREIGN KEY (tenant_id, privacy_profile_id)
        REFERENCES privacy_profile (tenant_id, id),

    -- A run cannot have finished before it started, and cannot have started before it
    -- was asked for. Cheap to state, and it is the kind of thing a worker writing
    -- timestamps out of order produces without failing.
    CONSTRAINT scan_timestamps_ordered
        CHECK (started_at IS NULL OR started_at >= requested_at),

    CONSTRAINT scan_completion_follows_start
        CHECK (completed_at IS NULL OR (started_at IS NOT NULL AND completed_at >= started_at))
);

COMMENT ON TABLE scan IS
    'One run of asking a set of brokers what they hold about one of the tenant''s own '
    'identities. The identity is referenced, never described — there is no column here '
    'for identifying data and no endpoint that accepts any.';

COMMENT ON COLUMN scan.requested_at IS
    'When the run was asked for. Distinct from started_at, which is null until a worker '
    'picks it up.';

-- Serves the history read, which is the only way this table is queried by a client:
-- everything for one tenant, newest first.
CREATE INDEX scan_by_tenant_recent ON scan (tenant_id, requested_at DESC);

CALL app.enable_tenant_rls('public.scan');

-- ---------------------------------------------------------------------------------

CREATE TABLE scan_broker (
    tenant_id uuid NOT NULL REFERENCES tenant (id),
    scan_id   uuid NOT NULL,

    -- No composite key here: broker is shared reference data with no tenant to pair
    -- against. ON DELETE RESTRICT by omission is deliberate — a broker with scans
    -- against it is a broker somebody's history refers to, and dropping the catalog row
    -- would leave that history describing a company the instance can no longer name.
    broker_id uuid NOT NULL REFERENCES broker (id),

    PRIMARY KEY (scan_id, broker_id),

    CONSTRAINT scan_broker_same_tenant
        FOREIGN KEY (tenant_id, scan_id)
        REFERENCES scan (tenant_id, id)
        ON DELETE CASCADE
);

COMMENT ON TABLE scan_broker IS
    'The brokers a scan was deliberately narrowed to. No rows for a scan means the '
    'unnarrowed case — the whole catalog as it stands when the scan runs — and not a '
    'scan of no brokers. The opposite reading is equally available and differs by '
    'whether anything is searched at all, so it is written down rather than inferred.';

CREATE INDEX scan_broker_by_broker ON scan_broker (broker_id);

CALL app.enable_tenant_rls('public.scan_broker');

-- ---------------------------------------------------------------------------------

CREATE TABLE exposure (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tenant_id        uuid        NOT NULL REFERENCES tenant (id),
    scan_id          uuid        NOT NULL,
    broker_id        uuid        NOT NULL REFERENCES broker (id),

    status           text        NOT NULL
                                 CONSTRAINT exposure_status_known
                                 CHECK (status IN ('new', 'requested', 'removed', 'reappeared', 'dismissed')),

    -- How sure the match is. Bounded here because a score outside the range is
    -- meaningless rather than merely wrong, and because a client sorting on it would
    -- rank a stray 47 above every genuine match.
    confidence       double precision NOT NULL
                                 CONSTRAINT exposure_confidence_ranged
                                 CHECK (confidence >= 0 AND confidence <= 1),

    discovered_at    timestamptz NOT NULL DEFAULT now(),

    -- Null until something has looked again. A listing confirmed present yesterday and
    -- one nobody has rechecked since it was found are different answers to "is this
    -- still true", and defaulting this to now() at insert would erase that difference
    -- the same way it would on the catalog's verified timestamp.
    last_verified_at timestamptz NULL,

    CONSTRAINT exposure_tenant_scoped UNIQUE (tenant_id, id),

    CONSTRAINT exposure_scan_same_tenant
        FOREIGN KEY (tenant_id, scan_id)
        REFERENCES scan (tenant_id, id),

    CONSTRAINT exposure_verification_follows_discovery
        CHECK (last_verified_at IS NULL OR last_verified_at >= discovered_at)
);

COMMENT ON TABLE exposure IS
    'One broker appearing to hold data about one identity. What the match was actually '
    'made on is not here: a pointer to the broker''s own profile page is a third '
    'party''s copy of somebody''s identity, which belongs in the vault store with the '
    'names and addresses rather than on a row the ordinary API path reads. Nothing '
    'writes exposures yet, so no such column exists to be filled in wrongly in the '
    'meantime — see KNOWN-GAPS.md.';

COMMENT ON COLUMN exposure.confidence IS
    'Match score from 0 to 1. A ranking aid, not a claim — the tenant is the only one '
    'who can say whether a listing is actually them.';

-- The findings list, which is read per tenant and filtered by status far more often
-- than by anything else.
CREATE INDEX exposure_by_tenant_status ON exposure (tenant_id, status);

-- Every finding from one run, which is how a scan's detail view reaches them.
CREATE INDEX exposure_by_scan ON exposure (scan_id);

CALL app.enable_tenant_rls('public.exposure');
