-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- One broker's share of one scan, and how it went.
--
-- A scan fans out to a company at a time, each leg queued in that company's own lane and
-- each running whenever that company may next be spoken to. Nothing is present at the end
-- of a run: whatever dispatched it is long gone, and the last leg to finish has no way of
-- knowing it was the last. This table is what makes "is this scan over" answerable, and it
-- is answered by counting the rows that have not finished.
--
-- ---------------------------------------------------------------------------------
-- Why this is not scan_broker
-- ---------------------------------------------------------------------------------
--
-- scan_broker is the scope the tenant asked for, and a scan of the whole catalog has no
-- rows in it at all — no rows there means "not narrowed", which is the opposite of "no
-- companies". This table is the work: a row per company the run actually planned to ask,
-- written whether or not the tenant narrowed anything. One table doing both jobs would
-- make "the whole catalog" and "nothing was planned" the same absence, and those differ by
-- whether anybody got searched.
--
-- ---------------------------------------------------------------------------------
-- Why an unfinished leg has no outcome rather than a status saying so
-- ---------------------------------------------------------------------------------
--
-- The outcome is nullable and null means "still going". A dedicated status column would
-- be a second thing to keep in step with the first, and the query the whole table exists
-- for — are any legs unfinished — would then have to enumerate the values that count as
-- over. It would also have a middle state nothing writes: a leg is planned, then it is
-- running, then it is over, and the two timestamps already record the first two.
--
-- ---------------------------------------------------------------------------------
-- What is deliberately not here
-- ---------------------------------------------------------------------------------
--
-- No candidate the run rejected. A finding below the confidence floor is not written as an
-- exposure — an exposure is a durable record that a company probably holds this person's
-- data, and keeping rows nobody will ever be shown retains more of somebody than this
-- service does anything with. What is kept is the two counts below, which are numbers about
-- a company rather than claims about a person, and which are the only way to notice a bar
-- set wrong: a broker offering forty candidates a run and recording none is a search
-- matching too loosely or a floor set too high, and neither is visible from the exposures,
-- because the ones that did not clear were never there.
--
-- No page content, and no part of the identity being searched for. `detail` is held to the
-- same rule as a log line and lives longer than one.

CREATE TABLE scan_leg (
    tenant_id           uuid        NOT NULL REFERENCES tenant (id),

    scan_id             uuid        NOT NULL,

    -- Shared reference data, so no composite key to pair against — the same reasoning
    -- scan_broker and identity_release record. RESTRICT by omission: a broker with legs
    -- against it is one somebody's history refers to.
    broker_id           uuid        NOT NULL REFERENCES broker (id),

    -- Which try this is. One row per leg, rewritten by a retry rather than added to,
    -- because a retry needs a fresh grant — the old one is single-use and spent by the
    -- attempt that failed — so what this counts is how many times this company has been
    -- asked for this run.
    attempt_number      int         NOT NULL DEFAULT 1
                                    CONSTRAINT scan_leg_attempt_starts_at_one
                                    CHECK (attempt_number >= 1),

    planned_at          timestamptz NOT NULL DEFAULT now(),

    -- When a worker took it out of the lane. The gap between this and planned_at is how
    -- long the company's lane was busy, which is the number that says whether pacing is
    -- why a scan is slow. One column doing both jobs would report every queued leg as
    -- having started.
    started_at          timestamptz NULL,

    completed_at        timestamptz NULL,

    -- Null is the whole of "unfinished". Text with a check rather than an enum, as
    -- everywhere else in this schema: an enum value cannot be un-added.
    outcome             text        NULL
                                    CONSTRAINT scan_leg_outcome_known
                                    CHECK (outcome IS NULL OR outcome IN (
                                        'found',
                                        'nothing_found',
                                        'no_search_available',
                                        'release_refused',
                                        'contract_broken',
                                        'transient',
                                        'rate_limited',
                                        'page_shape_changed',
                                        'blocked',
                                        'unsupported',
                                        'faulted')),

    -- What actually happened, for whoever reads the row afterwards. Never the identity
    -- being searched for and never the page's content: a status line, a selector that did
    -- not match, the name of a timeout that expired.
    detail              text        NULL,

    candidates_found    int         NOT NULL DEFAULT 0
                                    CONSTRAINT scan_leg_candidates_found_not_negative
                                    CHECK (candidates_found >= 0),

    candidates_recorded int         NOT NULL DEFAULT 0
                                    CONSTRAINT scan_leg_candidates_recorded_not_negative
                                    CHECK (candidates_recorded >= 0),

    PRIMARY KEY (scan_id, broker_id),

    CONSTRAINT scan_leg_scan_same_tenant
        FOREIGN KEY (tenant_id, scan_id)
        REFERENCES scan (tenant_id, id)
        ON DELETE CASCADE,

    -- A leg cannot have recorded more findings than the broker offered. The two counts are
    -- written together by one piece of code, so this is not defending against a race — it
    -- is the statement that one is a subset of the other, which is the only thing that
    -- makes their difference mean "did not clear the bar".
    CONSTRAINT scan_leg_recorded_within_found
        CHECK (candidates_recorded <= candidates_found),

    -- Findings only come from having looked. Any other outcome that claimed candidates
    -- would be a row whose counts came from somewhere other than a search.
    CONSTRAINT scan_leg_candidates_only_when_found
        CHECK (outcome = 'found' OR candidates_found = 0),

    CONSTRAINT scan_leg_started_after_planned
        CHECK (started_at IS NULL OR started_at >= planned_at),

    -- An outcome and a completion are the same event seen two ways, so neither may appear
    -- without the other. A leg with an outcome and no completed_at would be over and
    -- undated; one with a completed_at and no outcome would be over for no reason, and
    -- would also be counted as unfinished by the query this table exists for.
    CONSTRAINT scan_leg_finished_together
        CHECK ((outcome IS NULL) = (completed_at IS NULL)),

    CONSTRAINT scan_leg_completed_after_planned
        CHECK (completed_at IS NULL OR completed_at >= planned_at)
);

COMMENT ON TABLE scan_leg IS
    'One broker''s share of one scan, and how it went. Distinct from scan_broker, which is '
    'the scope a tenant asked for: no rows there means the whole catalog, while a row here '
    'means this company was actually planned to be asked.';

COMMENT ON COLUMN scan_leg.outcome IS
    'How the leg ended, or NULL while it has not. NULL is what "is this scan finished" is '
    'a query for, which is why there is no separate status column to keep in step with it.';

COMMENT ON COLUMN scan_leg.candidates_recorded IS
    'How many of the candidates cleared the confidence floor and became findings. The '
    'difference from candidates_found is the only surviving trace of the ones that did '
    'not, and deliberately the only one.';

COMMENT ON COLUMN scan_leg.detail IS
    'What happened, for whoever reads this afterwards. Held to the same rule as a log '
    'line — never the identity being searched for, never the page''s content — and it '
    'lives longer than a log line does.';

-- Every leg of a run, which is the query the table exists for: are any of them
-- unfinished, and if not, did they all get an answer.
CREATE INDEX scan_leg_by_scan ON scan_leg (tenant_id, scan_id);

CALL app.enable_tenant_rls('public.scan_leg');

-- ---------------------------------------------------------------------------------
-- Finding the runs nobody is waiting on
-- ---------------------------------------------------------------------------------
--
-- A scan is recorded as queued and left there, by a request handler acting for one account
-- or by the scheduler acting for another. What starts it is neither of them: it is a
-- process that wakes up and asks which runs are waiting, and that question reaches past
-- the tenant boundary in exactly the way "which accounts exist" does.
--
-- The alternative was to keep the existing privilege and sweep — list every account, take
-- a scope for each, ask that account whether it has anything queued. It needs no new grant
-- and it is worse: on an instance with ten thousand accounts a fifteen-second poll is
-- forty thousand queries a minute, almost all of them answering "nothing", and the cost
-- grows with how many people use the service rather than with how much work there is.
--
-- So dbr_scheduler learns one more question, and the policy is narrower than the one it
-- already has: four columns, and only the rows that are actually waiting. A run that has
-- been claimed is invisible to this role, so it cannot be used to watch what an account is
-- doing — only to find work nobody has picked up. Everything that follows from the answer
-- goes back through dbr_app, for one account at a time, inside the boundary.
--
-- Column-level again, so the grant itself says what may be read. The identity a scan is
-- for is not among the columns: which profile is being searched for is none of this
-- role's business, and it is the dispatcher acting as the account that reads it.
--
-- requested_at is granted because a column-level privilege covers every column a statement
-- names, ORDER BY included — and the order matters here. Oldest first is what stops a
-- backlog larger than one batch from leaving the earliest request behind on every pass.
GRANT SELECT (id, tenant_id, status, requested_at) ON scan TO dbr_scheduler;

CREATE POLICY scheduler_reads_queued_scans ON scan
    FOR SELECT
    TO dbr_scheduler
    USING (status = 'queued');

COMMENT ON POLICY scheduler_reads_queued_scans ON scan IS
    'Lets the dispatcher find runs nobody has started, across accounts it is not acting '
    'for. Read-only, three columns, and only rows still waiting — a claimed run is '
    'invisible to this role, so it cannot be used to watch what an account is doing.';
