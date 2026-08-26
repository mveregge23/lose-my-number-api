-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The demand, and the attempts at it.
--
-- A removal request is one demand made of one broker about one listing. A removal job is
-- one attempt at that demand. The split is the reason the lifecycle in §5 works at all:
-- the request is the thing that is retried, waited on, confirmed and — when a broker
-- re-lists somebody — resubmitted, while each attempt keeps its own record of which
-- connector ran and what came back.
--
-- ---------------------------------------------------------------------------------
-- Two composite keys, for two different reasons
-- ---------------------------------------------------------------------------------
--
-- The first is the one every tenant-scoped child in this schema now carries: the key is
-- over (tenant_id, exposure_id), so a request cannot be attached to a listing belonging
-- to somebody else. Row-level security does not cover that on its own — Postgres checks
-- a foreign key with row security off — and what it would mean here is a demand sent in
-- one person's name about another person's listing.
--
-- The second is new. A removal request names its broker as well as its exposure, because
-- the dispatcher routes by broker on the busiest path it has and should not join to do
-- it. That duplication is a chance for the two to disagree, so the key is over
-- (exposure_id, broker_id) against a matching unique constraint on the exposure. The
-- broker on this row is therefore the exposure's broker as a matter of schema rather
-- than as a matter of whoever wrote the insert being careful. Without it, a request could
-- be routed to a company that was never listed.

ALTER TABLE exposure
    ADD CONSTRAINT exposure_broker_scoped UNIQUE (id, broker_id);

COMMENT ON CONSTRAINT exposure_broker_scoped ON exposure IS
    'Target for the composite foreign key that pins a removal request''s broker to its '
    'exposure''s broker. Redundant with the primary key for uniqueness, load-bearing for '
    'the guarantee that a demand goes to the company the listing was actually found on.';

-- ---------------------------------------------------------------------------------

CREATE TABLE removal_request (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tenant_id       uuid        NOT NULL REFERENCES tenant (id),
    exposure_id     uuid        NOT NULL,
    broker_id       uuid        NOT NULL REFERENCES broker (id),

    -- Text with a check rather than a Postgres enum, as elsewhere: a value added to an
    -- enum type cannot be removed again, and this list is expected to be revisited.
    --
    -- 'cancelled' is not in §5's diagram. That is an omission in the diagram rather than
    -- a decision here: §6.5 gives a cancel route for a request still queued or submitted,
    -- and a lifecycle with no state for it would leave that endpoint writing a value
    -- nothing else recognises.
    status          text        NOT NULL
                                CONSTRAINT removal_request_status_known
                                CHECK (status IN (
                                    'queued', 'submitted', 'requires_human_input',
                                    'awaiting_broker_response', 'removed', 'reappeared',
                                    'failed', 'expired', 'cancelled')),

    strategy        text        NOT NULL
                                CONSTRAINT removal_request_strategy_known
                                CHECK (strategy IN ('automated', 'semi_automated', 'manual_email')),

    attempt         integer     NOT NULL DEFAULT 0
                                CONSTRAINT removal_request_attempt_not_negative
                                CHECK (attempt >= 0),

    -- Null when no confirmed statute reached this company for this person, which is a
    -- real answer rather than a missing one. ON DELETE is left at RESTRICT: a regime with
    -- requests decided under it is a regime somebody's history refers to, and the
    -- catalog sync already refuses to retract one for exactly this reason.
    legal_basis_id  uuid        NULL REFERENCES legal_basis (id),

    deadline_source text        NOT NULL
                                CONSTRAINT removal_request_deadline_source_known
                                CHECK (deadline_source IN ('statutory', 'operational_default')),

    -- Snapshotted at creation, never recomputed. A statute corrected next year must not
    -- silently reinterpret what somebody was told this year.
    deadline_at     timestamptz NOT NULL,

    created_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT removal_request_tenant_scoped UNIQUE (tenant_id, id),

    CONSTRAINT removal_request_exposure_same_tenant
        FOREIGN KEY (tenant_id, exposure_id)
        REFERENCES exposure (tenant_id, id),

    CONSTRAINT removal_request_broker_matches_exposure
        FOREIGN KEY (exposure_id, broker_id)
        REFERENCES exposure (id, broker_id),

    -- A statutory deadline with no regime behind it is a claim with nothing to check it
    -- against, and a regime recorded against a courtesy target misstates what it is. The
    -- two fields are only meaningful together.
    CONSTRAINT removal_request_basis_matches_source
        CHECK ((deadline_source = 'statutory') = (legal_basis_id IS NOT NULL))
);

COMMENT ON TABLE removal_request IS
    'One demand made of one broker about one listing. Outlives its attempts: this is the '
    'row that is retried, waited on, confirmed and resubmitted, which is why a listing '
    'that reappears returns here rather than opening a second demand.';

COMMENT ON COLUMN removal_request.deadline_at IS
    'Snapshotted when the request was created. Read deadline_source to know whether '
    'missing it is disappointing or actionable.';

-- At most one live demand per listing. Two open requests for one exposure would send the
-- same broker the same demand twice in one person's name, and the lifecycle already loops
-- on a single row: a listing that comes back reappears on the request that removed it.
--
-- Expired and cancelled are excluded, so a demand that ran out of retries or was called
-- off does not block a fresh one later. 'removed' is not excluded, deliberately — a
-- removed request is the one a reappearance belongs to.
CREATE UNIQUE INDEX removal_request_one_open_per_exposure
    ON removal_request (exposure_id)
    WHERE status NOT IN ('expired', 'cancelled');

-- The tenant's own list, newest first.
CREATE INDEX removal_request_by_tenant_recent ON removal_request (tenant_id, created_at DESC);

-- What the dispatcher asks: what is outstanding for this broker.
CREATE INDEX removal_request_by_broker_status ON removal_request (broker_id, status);

CALL app.enable_tenant_rls('public.removal_request');

-- ---------------------------------------------------------------------------------

CREATE TABLE removal_job (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tenant_id         uuid        NOT NULL REFERENCES tenant (id),
    removal_request_id uuid       NOT NULL,

    -- Which connector ran. Free text as far as the schema is concerned, because the set
    -- of connectors is a build-time fact rather than a database one — but shaped, so it
    -- stays an identifier rather than becoming somewhere a sentence ends up.
    connector_id      text        NOT NULL
                                  CONSTRAINT removal_job_connector_id_shaped
                                  CHECK (connector_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),

    status            text        NOT NULL
                                  CONSTRAINT removal_job_status_known
                                  CHECK (status IN ('pending', 'running', 'succeeded', 'failed')),

    attempt_number    integer     NOT NULL
                                  CONSTRAINT removal_job_attempt_number_positive
                                  CHECK (attempt_number >= 1),

    run_at            timestamptz NOT NULL,

    -- Null when there is not going to be another. On the job rather than the request
    -- because backoff is a property of what just happened: a rate-limited refusal and a
    -- malformed page both fail, and should not be retried on the same schedule.
    next_retry_at     timestamptz NULL,

    -- No checkpoint column. §3 classes encryptedCheckpoint as restricted-tier — it is a
    -- partly-filled form carrying somebody's name and address — which by that section's
    -- own rule puts it in the vault store rather than on a row the ordinary path reads.
    -- It arrives with DBR-039, which builds the resume path it exists for. A nullable
    -- bytea sitting here in the meantime is exactly what gets filled in by whoever needs
    -- one without noticing which store they are in.

    CONSTRAINT removal_job_tenant_scoped UNIQUE (tenant_id, id),

    CONSTRAINT removal_job_request_same_tenant
        FOREIGN KEY (tenant_id, removal_request_id)
        REFERENCES removal_request (tenant_id, id),

    -- One job per attempt. A second row claiming to be attempt three would make the
    -- history of a request unreadable in exactly the case somebody is reading it because
    -- something went wrong repeatedly.
    CONSTRAINT removal_job_one_per_attempt UNIQUE (removal_request_id, attempt_number),

    CONSTRAINT removal_job_retry_follows_run
        CHECK (next_retry_at IS NULL OR next_retry_at >= run_at)
);

COMMENT ON TABLE removal_job IS
    'One attempt at one removal request. Separate rows rather than a counter, because '
    'what is worth keeping is what happened each time — a retry failing the same way and '
    'one failing differently are different problems, and a counter reports them alike.';

-- What a dispatcher asks for: work that is due.
CREATE INDEX removal_job_due ON removal_job (status, run_at);

CALL app.enable_tenant_rls('public.removal_job');
