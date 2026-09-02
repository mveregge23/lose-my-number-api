-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Where a finding was found: the pointer to the broker's own listing page.
--
-- ---------------------------------------------------------------------------------
-- Why a URL is in the vault at all
-- ---------------------------------------------------------------------------------
--
-- It looks like a pointer and it is a copy. A people-search site's profile URL routinely
-- spells out the name, the city and sometimes the age of the person it is about —
-- /profile/alex-whitfield-sacramento-ca-41 is an ordinary shape for one. Storing that beside
-- a tenant id in the core store would put somebody's name in the table the ordinary API path
-- reads, having spent the whole of §7 keeping it out.
--
-- §3 classes it Restricted-PII for that reason, which puts it here: the vault store, under a
-- data key of its own, encrypted the same way a name is. The exposure row on the other side
-- keeps what is not identifying — which company, how sure, what state it is in — and points
-- at this by id.
--
-- ---------------------------------------------------------------------------------
-- A key per exposure rather than per profile
-- ---------------------------------------------------------------------------------
--
-- vault.profile_identity holds one wrapped key for a whole identity, because those four
-- fields are written and rewritten together by one person editing their profile. Findings
-- are not: they arrive one at a time over months, from different runs, and they are purged
-- one at a time as removals complete and their verification windows pass. A shared key would
-- mean the last surviving finding keeps a key alive for everything already deleted.
--
-- No foreign key to public.exposure, deliberately, and for the reason profile_identity
-- records: the reference is real and the id is the same, but a constraint between the two
-- stores is one that has to be dropped on the day the vault moves to a database of its own,
-- and it would be discovered on that day.

CREATE TABLE vault.exposure_source (
    -- The finding this belongs to: one listing, one row, the same id on both sides.
    exposure_id      uuid        PRIMARY KEY,

    -- Carried here as well, because the boundary is enforced independently in each store.
    -- Resolving the owner by reading the other one would mean this store trusting a table it
    -- deliberately cannot see.
    tenant_id        uuid        NOT NULL,

    -- This row's own data key, as the key manager returned it. Opaque and stored verbatim,
    -- so a different provider is a configuration change rather than a migration.
    wrapped_data_key text        NOT NULL,

    -- The listing's address, encrypted under the key above and bound to this exposure and
    -- this tenant, so the bytes decrypt in this position and nowhere else.
    encrypted_source_ref bytea   NOT NULL,

    created_at       timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE vault.exposure_source IS
    'Envelope-encrypted pointer to the broker listing one finding was found on. A URL is '
    'here rather than in the core store because a broker''s profile URL is a copy of '
    'somebody''s identity rather than a reference to one.';

COMMENT ON COLUMN vault.exposure_source.wrapped_data_key IS
    'This row''s data key, encrypted by the tenant''s wrapping key. One per finding rather '
    'than one per identity, because findings arrive and are purged one at a time — a shared '
    'key would outlive everything it once protected.';

CALL app.enable_tenant_rls('vault.exposure_source', 'tenant_id', 'dbr_vault');
