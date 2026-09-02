-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- A leg's grant gains a second thing it may do: say what it found.
--
-- ---------------------------------------------------------------------------------
-- Why the finding stopped being something the worker could write
-- ---------------------------------------------------------------------------------
--
-- Until now a leg wrote its own exposure rows. That was fine because an exposure held
-- nothing restricted: a broker id, a status, a number. The pointer to the listing changes
-- that. A broker's profile URL routinely contains the name, the city and sometimes the age
-- of the person it is about, so the link is a copy of the identity rather than a pointer to
-- one — §3 classes it Restricted-PII, which puts it in the vault store under field-level
-- encryption alongside the names it repeats.
--
-- The process that finds listings holds no keys and no vault connection, deliberately. So
-- recording a finding has to become something it asks a process that does — the same shape
-- as opening an identity, one step later in the leg.
--
-- ---------------------------------------------------------------------------------
-- Why the same token rather than a second one
-- ---------------------------------------------------------------------------------
--
-- The obvious design mints a second grant for reporting and puts both on the queue message.
-- It would carry identical claims: the same tenant, the same scan, the same broker, the same
-- profile, the same window. Two rows saying the same thing, two digests to store, two
-- lookups, and a second way for one of them to be present without the other.
--
-- So the grant is the leg's capability rather than one decryption right, and it has two
-- distinct single-use spends. redeemed_at records that the identity was opened; reported_at
-- records that the findings were recorded. Each is claimed by its own conditional update, so
-- neither can happen twice and one happening does not consume the other.
--
-- What a leaked token now permits is one company's leg of one scan being reported as well as
-- opened. That is a widening and it is a small one: anything holding the token could already
-- decrypt the part of an identity the grant covered, which is the worse of the two, and the
-- window is the same one — sized to the work rather than to the queue.

ALTER TABLE identity_release
    ADD COLUMN reported_at timestamptz NULL,

    -- Same shape as the redemption constraint beside it: a spend cannot predate the issue.
    ADD CONSTRAINT identity_release_reported_after_issue
        CHECK (reported_at IS NULL OR reported_at >= issued_at);

COMMENT ON COLUMN identity_release.reported_at IS
    'When this leg recorded what it found, or NULL while it has not. The second of the '
    'grant''s two single-use spends; the first is redeemed_at. Separate columns because '
    'they are separate permissions and the question asked afterwards — was an identity '
    'decrypted, were findings written — has two answers.';

-- The application role may now record both spends and still nothing else. Widening a scope,
-- extending an expiry and changing which work a grant belongs to remain unavailable, which
-- is what the column grant is for: the two most useful edits to an attacker holding an
-- application-level foothold are still not privileges any code path has.
REVOKE UPDATE ON identity_release FROM dbr_app;
GRANT UPDATE (redeemed_at, reported_at) ON identity_release TO dbr_app;

-- ---------------------------------------------------------------------------------
-- The resolver has to answer for the new spend as well
-- ---------------------------------------------------------------------------------
--
-- Recreated rather than left alone, because a caller deciding whether findings may be
-- recorded has to be able to see whether they already have — and it resolves the token
-- before it is acting for any tenant, so the ordinary path cannot read the row for it.
--
-- Everything else about the function is unchanged and deliberately so: still keyed on a
-- digest of 256 random bits, still returning no identifying data, still narrow enough that a
-- caller learns nothing they could not infer from being refused. One more timestamp is the
-- whole of the widening.
--
-- Dropped and recreated rather than replaced, because CREATE OR REPLACE refuses to change a
-- function's return type — and adding a column to a RETURNS TABLE is changing it.
DROP FUNCTION app.find_identity_release(bytea);

CREATE FUNCTION app.find_identity_release(lookup_token_hash bytea)
    RETURNS TABLE (
        id uuid,
        tenant_id uuid,
        scan_id uuid,
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
           release.broker_id,
           release.privacy_profile_id,
           release.fields,
           release.expires_at,
           release.redeemed_at,
           release.reported_at
    FROM public.identity_release AS release
    WHERE release.token_hash = lookup_token_hash
$$;

-- Reasserted because the drop above took the old function's privileges with it, and a
-- recreated definer function that nothing may execute is a redemption path that fails for
-- everybody at once.
REVOKE ALL ON FUNCTION app.find_identity_release(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.find_identity_release(bytea) TO dbr_app;

-- ---------------------------------------------------------------------------------
-- One listing per company per run
-- ---------------------------------------------------------------------------------
--
-- A results page can print the same listing twice, and a search that followed a link and
-- came back would report it twice. Neither is corroboration — one listing is one candidate,
-- and counting it twice would make a single page look like two independent findings.
--
-- The search contract already refuses a result carrying two candidates with one source
-- reference, which handles the honest case. This handles the rest: a retried leg, a
-- redelivered message, a search that produces two URLs differing by a tracking parameter.
-- It is expressed over the digest rather than the URL itself because the URL is in the other
-- store, encrypted, and unavailable to an index here.
ALTER TABLE exposure
    ADD COLUMN source_ref_digest bytea NULL,

    ADD CONSTRAINT exposure_one_listing_per_broker_per_scan
        UNIQUE (tenant_id, scan_id, broker_id, source_ref_digest);

COMMENT ON COLUMN exposure.source_ref_digest IS
    'SHA-256 of the listing URL, which lives encrypted in the vault store. Here so that one '
    'listing cannot become two findings, and so that a later scan can recognise a listing it '
    'has seen before without decrypting anything. It is a digest of a restricted value and '
    'is not itself readable back into one — but it is guessable for a known URL, so it is '
    'not shown to anybody and not part of any API response.';
