-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- A grant can name an attempt at a removal, not only a leg of a scan.
--
-- identity_release was built for one caller and says so in every line: scan_id is NOT
-- NULL, its foreign key points at scan, and the resolver returns a scan id. That was
-- right when the only thing needing part of an identity was a search. It is not right
-- now: a connector filling in a company's form or composing a deletion demand needs a
-- name just as badly, and there was no way to give it one.
--
-- ---------------------------------------------------------------------------------
-- Two nullable columns rather than one polymorphic pair
-- ---------------------------------------------------------------------------------
--
-- The tempting shape is (work_kind, work_id) — one column for what sort of work it is and
-- one for which. It is smaller and it throws away the only thing this table has that
-- makes a grant hard to forge into pointing somewhere it should not: a real foreign key.
-- A work_id referencing nothing can name a row that does not exist, or one belonging to
-- another account, and the check that it does not would live in whichever code path
-- remembered.
--
-- So there are two columns, each with its own composite key back to the table it belongs
-- to, and a check that exactly one of them is set. A grant is for a scan leg or for a
-- removal attempt, never both and never neither.

ALTER TABLE identity_release
    ALTER COLUMN scan_id DROP NOT NULL,

    ADD COLUMN removal_job_id uuid NULL,

    -- The same pairing scan_id already has: the attempt has to belong to the account the
    -- grant is for. Row-level security does not cover this on its own, because Postgres
    -- checks a foreign key with row security off — and what it would mean here is a grant
    -- opening one person's identity for another person's attempt.
    --
    -- ON DELETE CASCADE for the reason the scan side has it: a grant outliving the work it
    -- was minted for is a decryption right belonging to nothing.
    ADD CONSTRAINT identity_release_job_same_tenant
        FOREIGN KEY (tenant_id, removal_job_id)
        REFERENCES removal_job (tenant_id, id)
        ON DELETE CASCADE,

    ADD CONSTRAINT identity_release_names_one_piece_of_work
        CHECK ((scan_id IS NULL) <> (removal_job_id IS NULL));

COMMENT ON COLUMN identity_release.removal_job_id IS
    'The attempt this grant was minted for, or null when it belongs to a scan leg. '
    'Exactly one of this and scan_id is set.';

COMMENT ON TABLE identity_release IS
    'Permission for one piece of work to see part of one identity, once — a leg of a scan '
    'or an attempt at a removal. Presented as an unguessable token by a process that holds '
    'no keys; only the digest is stored, so reading this table yields nothing anybody can '
    'present.';

-- Every grant for an attempt, which is how somebody answers "what was decrypted in order
-- to make this demand" without the token that opened any of it. The mirror of the index
-- the scan side has, and partial because most rows are not attempts.
CREATE INDEX identity_release_by_job
    ON identity_release (tenant_id, removal_job_id)
    WHERE removal_job_id IS NOT NULL;

-- ---------------------------------------------------------------------------------
-- The resolver has to say which kind of work it found
-- ---------------------------------------------------------------------------------
--
-- Dropped and recreated rather than replaced, because CREATE OR REPLACE refuses to change
-- a function's return type and adding a column to a RETURNS TABLE is changing it. The same
-- dance DBR-099 did, for the same reason.
--
-- Everything else about it is unchanged and deliberately so: still keyed on a digest of
-- 256 random bits, still returning no identifying data, still narrow enough that a caller
-- learns nothing they could not infer from being refused. One more id is the whole of the
-- widening — and scan_id now comes back null for an attempt's grant, which is what tells
-- the redeemer it is holding one.

DROP FUNCTION app.find_identity_release(bytea);

CREATE FUNCTION app.find_identity_release(lookup_token_hash bytea)
    RETURNS TABLE (
        id uuid,
        tenant_id uuid,
        scan_id uuid,
        removal_job_id uuid,
        broker_id uuid,
        privacy_profile_id uuid,
        fields text[],
        expires_at timestamptz,
        redeemed_at timestamptz,
        reported_at timestamptz)
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
    SET search_path = pg_catalog
AS $$
    SELECT release.id,
           release.tenant_id,
           release.scan_id,
           release.removal_job_id,
           release.broker_id,
           release.privacy_profile_id,
           release.fields,
           release.expires_at,
           release.redeemed_at,
           release.reported_at
    FROM public.identity_release AS release
    WHERE release.token_hash = lookup_token_hash
$$;

COMMENT ON FUNCTION app.find_identity_release(bytea) IS
    'Resolve a release token to the grant it opens, before the caller is acting for any '
    'tenant. Narrow by construction: it answers only to a token digest, returns no '
    'identifying data, and returns only the state deciding whether the grant may be spent.';

-- Reasserted because the drop above took the old function's privileges with it, and a
-- recreated definer function nothing may execute is a redemption path that fails for
-- everybody at once.
REVOKE ALL ON FUNCTION app.find_identity_release(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.find_identity_release(bytea) TO dbr_app;
