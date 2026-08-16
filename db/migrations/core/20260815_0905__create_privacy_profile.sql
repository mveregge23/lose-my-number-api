-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- A privacy profile: one identity a tenant manages, and the thing a scan searches for.
--
-- This is the half of it that is not identifying. The names, addresses, contact
-- details and date of birth live encrypted in vault.profile_identity, keyed by this
-- row's id; what is here is what the rest of the system needs to route work without
-- ever asking for a decryption: whose profile it is, what relationship the tenant
-- claims to it, and roughly which jurisdiction its removals fall under.
--
-- Splitting one entity across the two stores rather than keeping it whole in either:
--
--   * Whole in the vault, and resolving which statute governs a removal request — a
--     routine operation on every request — would need the vault, so the day-to-day
--     path would hold vault access permanently and the separation would be a
--     formality.
--   * Whole in core, and identities sit in the store that serves ordinary traffic,
--     which is the arrangement the vault exists to avoid.
--
-- The seam is drawn where the sensitivity changes rather than where the entity does.

CREATE TABLE privacy_profile (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           uuid        NOT NULL REFERENCES tenant (id),

    -- Text with a check rather than a Postgres enum, as elsewhere: a value added to an
    -- enum type cannot be removed again, and this list is expected to be revisited.
    relationship_type   text        NOT NULL
                                    CONSTRAINT privacy_profile_relationship_known
                                    CHECK (relationship_type IN ('self', 'dependent', 'authorized_other')),

    -- Coarse on purpose, and constrained to stay that way. Working out which regimes
    -- apply to a removal needs to know roughly where someone lives; it does not need
    -- their street. This is the one geographic fact kept outside the vault, so the
    -- constraint is what stops it from quietly becoming an address — 'US-CA' passes,
    -- '123 Main St, Sacramento' does not.
    residency_region    text        NULL
                                    CONSTRAINT privacy_profile_residency_region_coarse
                                    CHECK (residency_region ~ '^[A-Z]{2}(-[A-Z0-9]{1,3})?$'),

    -- Which attestation text the tenant agreed to, and when. For a self profile this
    -- is the terms accepted at signup; for every other relationship it is an explicit
    -- claim to be entitled to act for someone else, recorded rather than verified.
    attested_at         timestamptz NOT NULL DEFAULT now(),
    attestation_version text        NOT NULL,

    created_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE privacy_profile IS
    'One identity a tenant manages. Identifying fields live encrypted in '
    'vault.profile_identity under the same id; this table holds only what routing and '
    'jurisdiction resolution need.';

COMMENT ON COLUMN privacy_profile.residency_region IS
    'Coarse region code (US-CA, EU). Deliberately outside the vault: resolving which '
    'statute governs a removal happens on every request and must not require a '
    'decryption.';

-- Exactly one self profile per account. It is created at signup and is not separately
-- deletable, so a second one is a bug rather than a state to handle; a partial unique
-- index says so once instead of every write path checking.
CREATE UNIQUE INDEX privacy_profile_one_self_per_tenant
    ON privacy_profile (tenant_id)
    WHERE relationship_type = 'self';

CALL app.enable_tenant_rls('public.privacy_profile');
