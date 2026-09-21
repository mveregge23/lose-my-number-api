-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What a listing agreed with, kept beside the finding instead of thrown away after scoring.
--
-- ---------------------------------------------------------------------------------
-- Why a finding remembers which groups the page showed
-- ---------------------------------------------------------------------------------
--
-- A scan compares a listing against four groups of an identity and reports how closely
-- each agreed. Until now that reading was folded into one confidence number and dropped.
-- The number says how sure we are; it does not say what the company was seen to hold.
--
-- The demand that follows needs the second thing. A request to delete is written to a
-- company that already holds some of this person's data, and it should hand over exactly
-- that and nothing more: a company whose page showed a name and a city has no business
-- receiving a home address and an email in the request to remove them. So the grant
-- minted for an attempt at a demand that cites a finding is narrowed to the groups the
-- finding agreed on, and a partial agreement on an address releases a coarse one.
--
-- One column per group rather than an array, mirroring the four separately encrypted
-- fields in the vault: which groups a company was seen to hold is exactly as granular as
-- which groups can be released to it. Null means the page showed nothing to compare.
-- None of this is identity data — it says a name agreed, never which name.

ALTER TABLE exposure
    ADD COLUMN agreed_names text NULL
        CONSTRAINT exposure_agreed_names_known
        CHECK (agreed_names IS NULL OR agreed_names IN ('exact', 'partial', 'conflicting')),

    ADD COLUMN agreed_addresses text NULL
        CONSTRAINT exposure_agreed_addresses_known
        CHECK (agreed_addresses IS NULL OR agreed_addresses IN ('exact', 'partial', 'conflicting')),

    ADD COLUMN agreed_contacts text NULL
        CONSTRAINT exposure_agreed_contacts_known
        CHECK (agreed_contacts IS NULL OR agreed_contacts IN ('exact', 'partial', 'conflicting')),

    ADD COLUMN agreed_date_of_birth text NULL
        CONSTRAINT exposure_agreed_date_of_birth_known
        CHECK (agreed_date_of_birth IS NULL OR agreed_date_of_birth IN ('exact', 'partial', 'conflicting'));

COMMENT ON COLUMN exposure.agreed_names IS
    'How closely the listing agreed with the names on file, or null if it showed none. '
    'Decides whether a demand citing this finding may disclose a name.';

COMMENT ON COLUMN exposure.agreed_addresses IS
    'How closely the listing agreed with an address on file. Partial — typically a city '
    'with no street on the page — releases only the city and region to a demand.';

COMMENT ON COLUMN exposure.agreed_contacts IS
    'How closely the listing agreed with a contact point on file, or null if it showed none.';

COMMENT ON COLUMN exposure.agreed_date_of_birth IS
    'How closely the listing agreed with the date of birth on file, or null if it showed none.';

-- Findings recorded before these columns existed keep null in every group. A recorded
-- listing always agreed with something — a candidate that agreed with nothing is never
-- written — so four nulls can only mean the row predates the columns, and a demand citing
-- such a finding is released as it was before: what the wording declares. The next scan
-- that sees the listing again does not rewrite the row; a new finding does.
