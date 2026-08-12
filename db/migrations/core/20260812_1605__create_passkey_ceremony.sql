-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The half-finished WebAuthn ceremony.
--
-- Registering or using a passkey takes two round trips: the server issues a challenge,
-- the authenticator signs it, the server checks the signature against the challenge it
-- issued. The middle of that is stateful, and the state has to be the server's — a
-- challenge the client hands back to itself proves nothing, because a client that can
-- choose the challenge can replay an old signature over it.

CREATE TABLE passkey_ceremony (
    -- Handed to the client and quoted back on the second leg. Random rather than
    -- sequential because it is the only thing naming this row, and a guessable name
    -- would let one caller complete another's ceremony.
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    purpose     text        NOT NULL
                            CONSTRAINT passkey_ceremony_purpose_known
                            CHECK (purpose IN ('registration', 'authentication')),

    -- The exact options issued, including the challenge, stored whole. Verification
    -- has to run against what was actually sent — rebuilding an equivalent-looking
    -- object at check time means the two can drift, and the drift would show up as an
    -- authentication that passes against options nobody issued.
    options     jsonb       NOT NULL,

    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,

    -- Set when the second leg claims this row, which is what makes a ceremony
    -- single-use. Kept rather than deleting the row so that a replay is a claim
    -- against an already-consumed ceremony — distinguishable from one that expired,
    -- and from one that never existed.
    consumed_at timestamptz
);

COMMENT ON TABLE passkey_ceremony IS
    'A WebAuthn challenge awaiting its answer. Single-use and short-lived; rows are '
    'pruned once expired.';

-- Both the sweep that prunes and the claim that checks expiry read this.
CREATE INDEX passkey_ceremony_expires_at ON passkey_ceremony (expires_at);

-- ---------------------------------------------------------------------------------
-- Why this table sits outside the tenant boundary
-- ---------------------------------------------------------------------------------

-- Every other table holding anything about an account opts into row-level security.
-- This one cannot, and the reason is structural rather than an oversight worth
-- revisiting: a ceremony exists precisely during the window when there is no tenant to
-- scope it to. A login ceremony is issued to someone who has not identified themselves
-- at all — that is what logging in means. A signup ceremony is issued before the
-- account row exists, so a policy comparing against it would have nothing to compare.
--
-- What stands in for the policy is the primary key. A row is only ever reached by its
-- own id, which is random, unguessable, and known solely to whoever received it. That
-- is a capability rather than a boundary, and it is deliberately weaker — so the rows
-- are kept correspondingly cheap: a random challenge, the options built from it, and
-- for a signup the address the account is being opened under. Each is minutes old at
-- most, and none of it is the identifying data the vault exists to protect.
--
-- Since app.enable_tenant_rls is what usually grants the application its DML, this
-- table has to be granted directly.
GRANT SELECT, INSERT, UPDATE, DELETE ON passkey_ceremony TO dbr_app;
