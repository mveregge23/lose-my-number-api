-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a company said back, and which attempt it said it about.
--
-- ---------------------------------------------------------------------------------
-- Why a demand needs somewhere to record that it was answered
-- ---------------------------------------------------------------------------------
--
-- Until now a demand sat at 'awaiting_broker_response' whatever happened next, and three
-- completely different situations read identically while the deadline ran on each: a
-- company working through a queue, a company that closed the request because nobody
-- replied to its question, and a message that bounced and never arrived at all.
--
-- The first real demand this system sent made that concrete rather than theoretical. The
-- company's privacy mailbox answered within the minute with a boilerplate saying that a
-- request nobody replies to is considered resolved. Nothing here could see that, and the
-- record would have gone on saying a deadline was running against a request already
-- closed on the other side.
--
-- ---------------------------------------------------------------------------------
-- No body column, which is a decision and not something left for later
-- ---------------------------------------------------------------------------------
--
-- What a company writes back is prose of its own composition, and the first one quoted
-- the request it was answering — so a reply body is as likely to hold somebody's name and
-- address as a demand is, and this table lives in the store the ordinary path reads
-- rather than in the vault. Reading that prose is what tells a confirmation from a
-- refusal, and the story that has to read it is the one that should settle where, if
-- anywhere, it is kept. What is here is what resolves and records an answer: who sent it,
-- when, which attempt it answers, and how that was established.
--
-- ---------------------------------------------------------------------------------
-- matched_by, because it is the only place a rewritten sender is observable
-- ---------------------------------------------------------------------------------
--
-- An attempt sends from removals-{jobId}@, so a reply addressed there names the attempt
-- exactly. That is the better key and it is also the one that frequently never leaves: a
-- relay permitting only its own authenticated account as a sender rewrites the address on
-- the way out, and says nothing about having done it. The first real demand went out that
-- way — believed sent from an attempt's own mailbox, delivered to the company as having
-- come from the account — and the reply came back naming nothing.
--
-- Nothing on the sending side can see that happen. A filed reply can: one resolved by
-- address proves the return address survived the journey, and one resolved by the id the
-- demand was sent under proves only that the thread did. Which is why the column is here
-- rather than in a log.

CREATE TABLE broker_reply (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    tenant_id          uuid        NOT NULL REFERENCES tenant (id),
    removal_request_id uuid        NOT NULL,
    removal_job_id     uuid        NOT NULL,

    -- What the source that delivered this called it. Kept so the same message arriving
    -- twice is refused rather than filed twice: a source offers a reply until it is
    -- acknowledged, so a process that files one and stops before acknowledging is offered
    -- it again — which is the right way round, but only if the second filing cannot land.
    source_ref         text        NOT NULL
                                   CONSTRAINT broker_reply_source_ref_present
                                   CHECK (length(source_ref) BETWEEN 1 AND 512),

    -- The reply's own id, bare, in the spelling an attempt stores the id it sent under.
    -- Nullable because a message is not obliged to carry one, and one that does not is
    -- still an answer.
    message_id         text        NULL
                                   CONSTRAINT broker_reply_message_id_shaped
                                   CHECK (message_id IS NULL
                                          OR (message_id ~ '^[^[:space:]<>]+@[^[:space:]<>]+$'
                                              AND length(message_id) <= 512)),

    from_address       text        NOT NULL
                                   CONSTRAINT broker_reply_from_address_present
                                   CHECK (length(from_address) BETWEEN 1 AND 320),

    -- Bounded rather than free, and kept out of anything that formats a row for a log:
    -- a subject line is written by the company and several of the ticketing systems that
    -- answer these demands quote the request back in it.
    subject            text        NULL
                                   CONSTRAINT broker_reply_subject_bounded
                                   CHECK (subject IS NULL OR length(subject) <= 998),

    matched_by         text        NOT NULL
                                   CONSTRAINT broker_reply_matched_by_known
                                   CHECK (matched_by IN ('mailbox_address', 'thread_headers')),

    received_at        timestamptz NOT NULL,
    filed_at           timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT broker_reply_tenant_scoped UNIQUE (tenant_id, id),

    CONSTRAINT broker_reply_job_same_tenant
        FOREIGN KEY (tenant_id, removal_job_id)
        REFERENCES removal_job (tenant_id, id),

    CONSTRAINT broker_reply_request_same_tenant
        FOREIGN KEY (tenant_id, removal_request_id)
        REFERENCES removal_request (tenant_id, id),

    -- One filing per message. The scope is the account rather than the table because a
    -- source's handle for a message is only unique within that source, and two accounts
    -- reading two different mailboxes may well both call something "14".
    CONSTRAINT broker_reply_one_per_message UNIQUE (tenant_id, source_ref),

    CONSTRAINT broker_reply_filed_after_received
        CHECK (filed_at >= received_at)
);

COMMENT ON TABLE broker_reply IS
    'A company answered one attempt at one demand. Metadata only: who answered, when, '
    'which attempt, and what tied the two together. The prose a company wrote is not '
    'kept here — it is as likely to hold somebody''s identity as a demand is, and this '
    'is the store the ordinary path reads.';

COMMENT ON COLUMN broker_reply.matched_by IS
    'Which of the two keys resolved this reply. mailbox_address means the per-attempt '
    'return address survived the journey out and back; thread_headers means only the '
    'thread did, which is what a relay that rewrites the sender leaves behind — and the '
    'sending side cannot observe that at all.';

-- What the timeline asks for: this demand's answers, in the order they came.
CREATE INDEX broker_reply_by_request ON broker_reply (removal_request_id, received_at);

CALL app.enable_tenant_rls('public.broker_reply');

-- ---------------------------------------------------------------------------------
-- The fourth question dbr_scheduler is allowed to ask
-- ---------------------------------------------------------------------------------
--
-- A reply arrives with no account attached to it: a company answers an address or quotes
-- an id, and neither says whose demand it was. The alternative to asking across accounts
-- is asking every account whether an arriving message is theirs, which is the shape the
-- dispatcher already rejected — cost growing with how many people use the service rather
-- than with how much work there is, and every account being asked about somebody else's
-- mail.
--
-- So the narrowest question yet: four columns, and only attempts that actually sent
-- something. An attempt that sent nothing has no id to quote and no mailbox anybody could
-- have answered, so it is invisible here — this role can find the attempt a reply belongs
-- to and cannot use the grant to watch what an account is doing. Everything that follows
-- goes back through dbr_app, one account at a time, inside the boundary.

GRANT SELECT (id, tenant_id, removal_request_id, sent_message_id) ON removal_job TO dbr_scheduler;

CREATE POLICY scheduler_resolves_answered_demands ON removal_job
    FOR SELECT
    TO dbr_scheduler
    USING (sent_message_id IS NOT NULL);

COMMENT ON POLICY scheduler_resolves_answered_demands ON removal_job IS
    'Lets an arriving reply be tied to the attempt it answers, across accounts this role '
    'is not acting for. Read-only, four columns, and only attempts that sent a message — '
    'an attempt nobody could have replied to is invisible to it.';
