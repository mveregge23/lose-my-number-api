-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- A demand does not require having found a listing.
--
-- The previous migration made exposure_id NOT NULL, following §3's relationship and
-- §6.5's request body, which both read as though a removal request resolves an exposure.
-- That is too narrow, and the narrowness is not a detail.
--
-- Nothing about the right to make the demand depends on having found anything. A
-- deletion request under CCPA does not require the consumer to prove the business holds
-- their data, and an opt-out of sale is prospective by its nature — "do not sell my data"
-- is a meaningful thing to say to a company whose search page happens to return nothing
-- today. A scan only ever finds what is publicly searchable, which is a subset of what a
-- broker holds; submitting only where a search returned a hit means declining to ask
-- about everything else.
--
-- So an exposure becomes evidence of what prompted a demand rather than the subject of
-- it. The subject is an identity and a company.
--
-- ---------------------------------------------------------------------------------
-- What the identity is doing here
-- ---------------------------------------------------------------------------------
--
-- A demand is made on behalf of somebody, and until now that somebody was only reachable
-- as exposure -> scan -> privacy_profile. With no exposure there is no such path, and the
-- connector filling in a form has no way to know whose details to ask the vault for. The
-- profile therefore moves onto the request itself, where it was arguably always the more
-- important of the two references.
--
-- Which then creates a second chance for two references to disagree, the same way the
-- broker did: a request could name one identity while citing a listing found for another.
-- Both would belong to the same account, so the tenant key would not notice, and the
-- result would be a form filled with one person's details submitted as evidence about
-- another's listing. So exposure gains the profile its scan was for, and the request's
-- key is over the exposure and the profile together.
--
-- ---------------------------------------------------------------------------------
-- Which demand was actually made
-- ---------------------------------------------------------------------------------
--
-- request_type is not a consequence of the above; it is a gap the above made visible.
-- §11.2 resolves the governing statute by intersecting residency, the broker's confirmed
-- regimes, and the kind of demand — deletion and opt-out are different rights with
-- different deadlines, and legal_basis is unique on that third field. The resolver has
-- always taken it. The row never recorded it, so a stored deadline could not be read back
-- against the demand it was computed for.
--
-- ---------------------------------------------------------------------------------
-- On adding NOT NULL columns to an existing table
-- ---------------------------------------------------------------------------------
--
-- Safe here because removal_request is empty everywhere. It was created yesterday, no API
-- writes to it, and the only rows that have ever existed were written by tests that clean
-- up after themselves. This is stated rather than assumed: the same statements against a
-- populated table would fail, and whoever reads this next should not take it as a
-- precedent for a table with rows in it.

-- ---------------------------------------------------------------------------------

-- The scan already knows which identity it searched for. Pinning it here lets a finding
-- say whose it is without a join, and gives the request something to key against.
ALTER TABLE scan
    ADD CONSTRAINT scan_profile_scoped UNIQUE (id, privacy_profile_id);

ALTER TABLE exposure
    ADD COLUMN privacy_profile_id uuid NOT NULL,
    ADD CONSTRAINT exposure_profile_matches_scan
        FOREIGN KEY (scan_id, privacy_profile_id)
        REFERENCES scan (id, privacy_profile_id),
    ADD CONSTRAINT exposure_profile_scoped UNIQUE (id, privacy_profile_id);

COMMENT ON COLUMN exposure.privacy_profile_id IS
    'Whose listing this is. Denormalized from the scan that found it and pinned to it by '
    'a composite key, so the two cannot drift.';

-- ---------------------------------------------------------------------------------

ALTER TABLE removal_request
    -- Evidence of what prompted the demand, not the subject of it.
    ALTER COLUMN exposure_id DROP NOT NULL,

    ADD COLUMN privacy_profile_id uuid NOT NULL,

    -- Which right is being exercised. The same three values legal_basis is keyed on, so a
    -- request and the regime governing it are talking about the same kind of demand.
    ADD COLUMN request_type text NOT NULL
        CONSTRAINT removal_request_type_known
        CHECK (request_type IN ('delete', 'opt_out_sale', 'opt_out_targeted_ads')),

    ADD CONSTRAINT removal_request_profile_same_tenant
        FOREIGN KEY (tenant_id, privacy_profile_id)
        REFERENCES privacy_profile (tenant_id, id),

    -- Only checked when there is an exposure: a composite foreign key with a NULL column
    -- is not enforced, which is exactly the behaviour wanted here. When a demand cites a
    -- listing, that listing was found for this identity. When it cites none, there is
    -- nothing to agree with.
    ADD CONSTRAINT removal_request_exposure_matches_profile
        FOREIGN KEY (exposure_id, privacy_profile_id)
        REFERENCES exposure (id, privacy_profile_id);

COMMENT ON COLUMN removal_request.exposure_id IS
    'The listing that prompted this demand, or null when none did. A demand does not '
    'require having found anything — an opt-out of sale is prospective, and a deletion '
    'request does not oblige the person to prove the company holds their data.';

COMMENT ON COLUMN removal_request.request_type IS
    'Which right is being exercised. Read alongside legal_basis_id: the governing regime '
    'was resolved for this kind of demand, and deletion and opt-out carry different '
    'deadlines under the same statute.';

-- ---------------------------------------------------------------------------------

-- The rule was "one open demand per listing", which cannot express itself once a demand
-- may cite no listing at all. What it was really protecting against is sending one
-- company the same demand twice for the same person, and that is what this says.
--
-- Keyed on the identity rather than the account: a tenant managing their own profile and
-- a dependent's may legitimately have a deletion demand open with the same broker for
-- each. Keyed on the request type as well, because a deletion and an opt-out are
-- different asks and having both open at once is reasonable — some brokers answer one
-- and not the other.
DROP INDEX removal_request_one_open_per_exposure;

CREATE UNIQUE INDEX removal_request_one_open_per_demand
    ON removal_request (privacy_profile_id, broker_id, request_type)
    WHERE status NOT IN ('expired', 'cancelled');

COMMENT ON INDEX removal_request_one_open_per_demand IS
    'One live demand per identity, company and kind of request. Replaces the '
    'per-exposure rule, which could not be stated once a demand stopped requiring a '
    'listing to point at.';
