-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The id a demand went out under, kept on the attempt that sent it.
--
-- ---------------------------------------------------------------------------------
-- Why an attempt remembers the message it sent
-- ---------------------------------------------------------------------------------
--
-- The relay hands back a Message-ID for every demand it accepts, and until now that id was
-- written into a header, logged once where it was generated, and dropped. Two moments want
-- it back.
--
-- The first is the day a company says it never received a demand. An attempt that can name
-- the id the company's own server acknowledged is answering with that company's record
-- rather than with an assertion of ours; an attempt that kept only 'succeeded' has the
-- claim and none of the evidence, which is the same position the failure_reason column was
-- added to get out of.
--
-- The second is every reply that comes back. A mail client answering a demand quotes the id
-- it is answering in In-Reply-To and repeats it in References, so an inbound message is
-- tied to an attempt by what the company's own client wrote rather than by reading a
-- subject line somebody may have edited. The job mailbox already resolves a reply to the
-- job it was addressed from; this resolves it to the attempt within that job, which is the
-- row that carries a connector, a deadline and a place to record what the answer was.
--
-- ---------------------------------------------------------------------------------
-- Stored without the angle brackets, which is a decision rather than a formality
-- ---------------------------------------------------------------------------------
--
-- A header writes the id inside <>; the id itself is what sits between them. The receipt
-- this column is written from already strips them, and storing either spelling would leave
-- every future lookup having to try both — the failure mode being a reply that matches
-- nothing and is filed against no attempt, which reads exactly like a company that never
-- answered.
--
-- So the check makes the convention a property of the table rather than of whoever wrote
-- the last insert: an id has an @ in it, and nothing with a bracket or a space in it is an
-- id. The bound is well under the line length a header is allowed, because a Message-ID
-- that needed folding is not one anything generated here.
--
-- Nullable, and most rows will keep the null. Only a connector that hands a message to a
-- relay has one — a web form is submitted and answers on the page — so nothing may read an
-- absent id as an attempt that did not happen.

ALTER TABLE removal_job
    ADD COLUMN sent_message_id text NULL
        CONSTRAINT removal_job_sent_message_id_shaped
        CHECK (sent_message_id IS NULL
               OR (sent_message_id ~ '^[^[:space:]<>]+@[^[:space:]<>]+$'
                   AND length(sent_message_id) <= 512));

COMMENT ON COLUMN removal_job.sent_message_id IS
    'The Message-ID the relay acknowledged for the demand this attempt sent, without the '
    'angle brackets it wears in a header. Null on an attempt that sent no message, which '
    'is most of them. What answers a company claiming it never received a demand, and what '
    'a reply''s In-Reply-To is matched against.';

-- At most one attempt per id, which is the shape correlating a reply depends on. An id is
-- generated per message and is unique by construction, so two rows carrying the same one
-- means something copied a row rather than sent a demand — and a reply quoting that id
-- would have two attempts it could belong to and no way to choose between them. Partial,
-- because null is the ordinary case and every attempt that sent nothing would otherwise
-- collide with every other.
CREATE UNIQUE INDEX removal_job_one_attempt_per_sent_message
    ON removal_job (sent_message_id)
    WHERE sent_message_id IS NOT NULL;
