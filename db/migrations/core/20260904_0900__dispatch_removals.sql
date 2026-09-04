-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a dispatcher needs in order to move a demand, and what an attempt has to be able
-- to say afterwards.
--
-- ---------------------------------------------------------------------------------
-- Finding demands nobody has dispatched
-- ---------------------------------------------------------------------------------
--
-- The same question the scan dispatcher already asks, about the other table, and it gets
-- the same answer for the same reasons. A removal request is recorded as queued by the
-- account that opened it and left there; what picks it up is a process acting for nobody
-- in particular, which has to find out what is waiting before it can act for anybody.
--
-- The alternative was to sweep every account and ask each whether it has anything queued.
-- On an instance with ten thousand accounts a fifteen-second poll is forty thousand
-- queries a minute, almost all answering "nothing", and the cost grows with how many
-- people use the service rather than with how much work there is.
--
-- So dbr_scheduler learns a third question, narrower than either of the first two: four
-- columns, and only rows still waiting. A demand that has been dispatched is invisible to
-- this role, so it cannot be used to watch what an account is doing — only to find work
-- nobody has picked up. Everything that follows goes back through dbr_app, one account at
-- a time, inside the boundary.
--
-- The identity is not among the columns, deliberately. Whose data is being demanded gone
-- is none of this role's business; it is the dispatcher, acting as the account, that reads
-- it. created_at is granted because a column-level privilege covers every column a
-- statement names, ORDER BY included, and the order is what stops a backlog larger than
-- one batch from leaving the earliest demand behind on every pass.

GRANT SELECT (id, tenant_id, status, created_at) ON removal_request TO dbr_scheduler;

CREATE POLICY scheduler_reads_queued_removals ON removal_request
    FOR SELECT
    TO dbr_scheduler
    USING (status = 'queued');

COMMENT ON POLICY scheduler_reads_queued_removals ON removal_request IS
    'Lets the dispatcher find demands nobody has sent, across accounts it is not acting '
    'for. Read-only, four columns, and only rows still waiting — a dispatched demand is '
    'invisible to this role, so it cannot be used to watch what an account is doing.';

-- ---------------------------------------------------------------------------------
-- What an attempt says about itself
-- ---------------------------------------------------------------------------------
--
-- removal_job's own comment says it exists so that "a retry failing the same way and one
-- failing differently are different problems, and a counter reports them alike". Until now
-- the row could not actually tell those apart: it recorded that an attempt failed and had
-- nowhere to say why, which is the same information a counter carries.
--
-- Two columns rather than one. The reason is a small closed set the connector chooses from
-- and the dispatcher branches on — whether to try again at all is read from it — while the
-- detail is a sentence for whoever reads the log. Collapsing them would mean parsing prose
-- to decide a retry.

ALTER TABLE removal_job
    ADD COLUMN failure_reason text NULL
        CONSTRAINT removal_job_failure_reason_known
        CHECK (failure_reason IN (
            'transient', 'rate_limited', 'broker_form_changed', 'rejected', 'unsupported')),

    -- Never the identity the demand was made for, and never the page's content. A status
    -- line, a selector that did not match, the name of a timeout that expired.
    ADD COLUMN detail text NULL
        CONSTRAINT removal_job_detail_bounded
        CHECK (detail IS NULL OR length(detail) <= 1000),

    -- A reason only means something on an attempt that failed. On a successful one it
    -- would be a field nothing sets and everything has to remember to ignore.
    ADD CONSTRAINT removal_job_reason_only_when_failed
        CHECK (failure_reason IS NULL OR status = 'failed');

COMMENT ON COLUMN removal_job.failure_reason IS
    'Why this attempt did not complete, in the connector''s own vocabulary. Null on an '
    'attempt that ran. What the dispatcher reads to decide whether trying again is worth '
    'anything — a refusal and a timeout are both failures and only one of them is.';

COMMENT ON COLUMN removal_job.detail IS
    'What actually happened, for whoever reads the log. Never the identity the demand was '
    'made for and never the page''s content.';
