-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Passkeys: the public half of a WebAuthn credential, and the one narrow way a login
-- attempt is allowed to find it.
--
-- Nothing secret is stored here. A WebAuthn credential's private key never leaves the
-- authenticator, so this table holds a public key, a counter, and the handle the
-- authenticator uses to refer to itself. Losing this table to an attacker gains them
-- nothing they could sign with.

CREATE TABLE passkey (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid        NOT NULL REFERENCES tenant (id) ON DELETE CASCADE,

    -- The authenticator's own handle for this credential, and the only thing a login
    -- attempt arrives holding. Variable length by specification — 16 bytes for some
    -- security keys, 100+ for platform authenticators that encrypt their state into
    -- it — so no length is asserted here.
    credential_id      bytea       NOT NULL,

    -- COSE-encoded, exactly as the authenticator produced it. Stored rather than
    -- re-derived because the encoding carries the algorithm along with the key.
    public_key         bytea       NOT NULL,

    -- The authenticator's own use counter, when it keeps one. A value that fails to
    -- advance between assertions is how a cloned authenticator gives itself away, so
    -- this is written back on every successful login rather than left at its initial
    -- value. Authenticators that do not count report zero forever, which is
    -- permitted, and for those this proves nothing.
    signature_count    bigint      NOT NULL DEFAULT 0,

    -- Two things an authenticator offers at registration and that are deliberately
    -- not kept. Its AAGUID names its make and model, which is a fact about the person
    -- holding it that nothing here needs — and asking for it means requesting
    -- attestation, which this service does not, so browsers report zeros anyway. Its
    -- transport hints exist to populate allowCredentials, which a login that never
    -- asks who you are has no use for.

    -- Whether the authenticator says this credential may be, and has been, copied to
    -- a backup. Together they are what separates a passkey synced to a password
    -- manager from one that exists on a single device and vanishes with it — which
    -- decides whether losing that device loses the account.
    is_backup_eligible boolean     NOT NULL,
    is_backed_up       boolean     NOT NULL,

    created_at         timestamptz NOT NULL DEFAULT now(),
    last_used_at       timestamptz
);

COMMENT ON TABLE passkey IS
    'The public half of a passkey. Holds no secret: the private key never leaves the '
    'authenticator, so this table is useless to anyone who cannot sign with it.';

-- Globally unique, not per tenant. A credential id identifies one authenticator's one
-- credential, and the login path resolves an account *from* it — two rows sharing one
-- would make that resolution ambiguous in the one place where guessing is not an
-- option. Enforced beneath the tenant policy, which is the only reason it holds at
-- all: a second tenant inserting a duplicate cannot see the row it collides with.
CREATE UNIQUE INDEX passkey_credential_id_unique
    ON passkey (credential_id);

-- Listing an account's own passkeys, and the cascade when an account is deleted.
CREATE INDEX passkey_tenant_id ON passkey (tenant_id);

CALL app.enable_tenant_rls('public.passkey');

-- ---------------------------------------------------------------------------------
-- The way in
-- ---------------------------------------------------------------------------------

-- Login has a chicken-and-egg problem that the tenant boundary creates on purpose: a
-- caller who has not authenticated yet is acting for no tenant, so every policy
-- matches zero rows, so the credential needed to authenticate them is invisible. The
-- table cannot be read out of that state, and it should not be — a role able to read
-- credentials freely is a role that can enumerate accounts.
--
-- This is the exception, made as small as it can be. SECURITY DEFINER runs the body
-- as the function's owner, which row-level security does not apply to, so the lookup
-- succeeds where the caller's own query would return nothing.
--
-- What makes that safe is the argument. A credential id is high-entropy and minted by
-- an authenticator; nobody can supply one without already holding the passkey or
-- having seen it. It is not an address, a name, or anything a person would type, so
-- there is no dictionary to walk. And the answer is useless on its own: the caller
-- still has to produce a signature over a challenge this server issued.
--
-- What it returns is the minimum an assertion check needs and nothing else. No email,
-- no created date, no account state. Adding a field here widens what an unauthenticated
-- caller can learn, so each one has to earn its place.
CREATE FUNCTION app.find_passkey(lookup_credential_id bytea)
    RETURNS TABLE (tenant_id uuid, public_key bytea, signature_count bigint)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    -- Everything below is schema-qualified and this path holds nothing else, so no
    -- object the body names can be shadowed by a caller-controlled schema. A
    -- SECURITY DEFINER function without this is a way to run arbitrary code as its
    -- owner.
    SET search_path = pg_catalog
AS $$
    SELECT credential.tenant_id, credential.public_key, credential.signature_count
    FROM public.passkey AS credential
    WHERE credential.credential_id = lookup_credential_id
$$;

COMMENT ON FUNCTION app.find_passkey(bytea) IS
    'Resolve a passkey to its account during login, before the caller is acting for '
    'any tenant. The one path through the tenant boundary, deliberately narrow: it '
    'answers only to a credential id, and returns only what verifying an assertion '
    'needs.';

-- Nobody but the application, and only to call it.
REVOKE ALL ON FUNCTION app.find_passkey(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.find_passkey(bytea) TO dbr_app;

-- There is no counterpart for writing. Once an assertion verifies, the tenant is
-- known, so recording the new counter goes through the ordinary policy-enforced path
-- like any other write. A definer function that could update this table would be a
-- second way past the boundary in exchange for nothing.
