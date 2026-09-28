-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a company's answer turned out to say, and what made it read that way.
--
-- ---------------------------------------------------------------------------------
-- The prose is read and not kept, which is the decision this column exists instead of
-- ---------------------------------------------------------------------------------
--
-- Telling a confirmation from a refusal means reading what a company wrote, and the
-- obvious middle course — keep a short excerpt so somebody can see why it was read that
-- way — does not survive contact with what companies actually send. The first real answer
-- this system received quoted the request back inside its first few lines, identity block
-- and all. An excerpt cannot be made safe by being short, because the part quoted first is
-- precisely the part worth not keeping.
--
-- So the body is fetched, matched against the phrases the company's own file declares, and
-- dropped. What lands here is a verdict and the catalog phrase that produced it — which is
-- text this project wrote and reviewed, not text a company wrote about a person. Anybody
-- who needs the words themselves has the mailbox, which is theirs.
--
-- ---------------------------------------------------------------------------------
-- Four readings, and the default is that nobody knows
-- ---------------------------------------------------------------------------------
--
-- 'unclear' is not a failure; it is the ordinary state of a conversation with a company
-- nobody has written phrases for yet, and it means a person reads the reply. Every company
-- starts there and leaves it one pull request at a time.
--
-- The other three are ranked by what acting on them wrongly would cost. 'needs_us' wins
-- over everything: if any part of an answer says the company is waiting on us then it is,
-- whatever else the same message says. 'confirmed' ranks last deliberately — it is the
-- reading with the largest consequence, because a demand recorded as honoured is a demand
-- nobody asks about again.
--
-- Nothing here moves a demand yet. A reading is recorded; acting on one — settling a
-- request, opening a refusal, putting a question in front of somebody — is the state
-- machine's business and its own story. Recording first means the day that story is built,
-- there is a body of real readings to check it against rather than a guess.

ALTER TABLE broker_reply
    ADD COLUMN reading text NOT NULL DEFAULT 'unclear'
        CONSTRAINT broker_reply_reading_known
        CHECK (reading IN ('unclear', 'needs_us', 'refused', 'confirmed')),

    -- The declared phrase that produced the reading, so a reading can be explained without
    -- keeping what the company wrote. Null when nothing matched, which is every 'unclear'
    -- row and only those.
    ADD COLUMN matched_phrase text NULL
        CONSTRAINT broker_reply_matched_phrase_bounded
        CHECK (matched_phrase IS NULL OR length(matched_phrase) BETWEEN 12 AND 512),

    ADD CONSTRAINT broker_reply_unclear_matched_nothing
        CHECK ((reading = 'unclear') = (matched_phrase IS NULL));

-- The default is for the rows already filed, which were read by nothing because there was
-- nothing to read them with. It comes off so that a later insert has to say what it means:
-- a reading is a judgement, and a column that quietly supplies one is how a row ends up
-- claiming a judgement nobody made.
ALTER TABLE broker_reply ALTER COLUMN reading DROP DEFAULT;

COMMENT ON COLUMN broker_reply.reading IS
    'What the company''s answer was read as. unclear is the default and the honest state '
    'for a company nobody has written phrases for — it means a person reads it. Nothing '
    'acts on a reading yet; it is recorded so that whatever does can be checked against '
    'real answers.';

COMMENT ON COLUMN broker_reply.matched_phrase IS
    'The catalog phrase that produced the reading. Text this project reviewed, not text a '
    'company wrote about a person — which is the whole reason it can be kept while the '
    'body it was found in cannot.';

-- What somebody looking for the replies nobody has read asks for.
CREATE INDEX broker_reply_by_reading ON broker_reply (reading, received_at);
