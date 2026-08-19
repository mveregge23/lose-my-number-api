-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The first legal regimes: California, Virginia, Colorado, Connecticut and Utah.
--
-- These five come first because they are where the broker volume is, not because they
-- are the only states with a statute. The catalog is expected to stay incomplete for a
-- long time, and that is safe by construction: a jurisdiction with no row falls back to
-- the broker's own operational target, which is labelled as a courtesy rather than
-- presented as a legal deadline. An incomplete catalog under-promises. A wrong row
-- misinforms somebody about the rights they actually have, which is why every row here
-- carries the primary source it was read from and the name of whoever read it.
--
-- ---------------------------------------------------------------------------------
-- Content, not code
-- ---------------------------------------------------------------------------------
--
-- These are rows rather than constants in a dispatcher for the reason the whole legal
-- half of this schema exists: a deadline that changes because a statute was amended
-- should be a reviewed change to data, not a code change somebody has to find. They
-- arrive as a migration because that is the reviewed path that exists today, and it
-- runs with the privileges migrations run with — the role that serves requests holds
-- SELECT on this table and could not have written them.
--
-- ---------------------------------------------------------------------------------
-- Deadlines are stored in calendar days, and one of these is not
-- ---------------------------------------------------------------------------------
--
-- response_deadline_days is an integer of calendar days. California's opt-out timing
-- is expressed in *business* days, which this column cannot say, so it is converted
-- and the conversion rounds the way that errs safely: fifteen business days is at most
-- twenty-one calendar days without holidays, so twenty-one is stored. Rounding the
-- other way would have a request look overdue while the business still had time, and
-- telling somebody they have recourse they do not have is the failure this table is
-- shaped to avoid. Rounding up only delays noticing a genuine miss.

WITH reviewer AS (
    -- One place to set this, because every row below takes the same value and a
    -- reviewer named in fifteen literals is a reviewer named wrong in one of them.
    --
    -- Provenance is the point of this table: reviewed_by answers "who read the statute
    -- and stands behind this reading". Until somebody has actually done that, it says
    -- so rather than naming a person who has not.
    SELECT
        'unreviewed seed — pending maintainer review'::text AS reviewed_by,
        now()                                               AS reviewed_at
)
INSERT INTO legal_basis (
    code,
    request_type,
    residency_scope,
    response_deadline_days,
    extension_days,
    verification_level,
    citation_url,
    reviewed_at,
    reviewed_by
)
SELECT
    seed.code,
    seed.request_type,
    seed.residency_scope,
    seed.response_deadline_days,
    seed.extension_days,
    seed.verification_level,
    seed.citation_url,
    reviewer.reviewed_at,
    reviewer.reviewed_by
FROM (
    VALUES
        -- California — CCPA as amended by the CPRA.
        --
        -- Deletion answers within 45 days of a verifiable consumer request, extendable
        -- once by a further 45 when reasonably necessary and the consumer is told
        -- inside the first window. Verification is required, which is what "verifiable
        -- consumer request" means throughout the act.
        ('CCPA', 'delete', 'US-CA', 45, 45, 'basic',
         'https://leginfo.legislature.ca.gov/faces/codes_displaySection.xhtml?lawCode=CIV&sectionNum=1798.105'),

        -- Opting out of sale, and of sharing for cross-context behavioural advertising,
        -- is deliberately not a verifiable request — requiring proof of identity to
        -- stop a sale would put a toll on the right itself. The timing comes from the
        -- regulations rather than the code section, in business days; see the note on
        -- the conversion above.
        ('CCPA', 'opt_out_sale', 'US-CA', 21, 0, 'none',
         'https://leginfo.legislature.ca.gov/faces/codes_displaySection.xhtml?lawCode=CIV&sectionNum=1798.120'),
        ('CCPA', 'opt_out_targeted_ads', 'US-CA', 21, 0, 'none',
         'https://leginfo.legislature.ca.gov/faces/codes_displaySection.xhtml?lawCode=CIV&sectionNum=1798.120'),

        -- Virginia — VCDPA. Forty-five days from receipt, extendable once by another
        -- forty-five. Virginia authenticates every consumer request including an
        -- opt-out, which is why these three agree where California's do not.
        ('VCDPA', 'delete', 'US-VA', 45, 45, 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),
        ('VCDPA', 'opt_out_sale', 'US-VA', 45, 45, 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),
        ('VCDPA', 'opt_out_targeted_ads', 'US-VA', 45, 45, 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),

        -- Colorado — CPA. Forty-five days, extendable once by forty-five. Opt-outs are
        -- reachable by a universal opt-out signal, which a controller has to honour
        -- without authenticating whoever sent it.
        ('CPA', 'delete', 'US-CO', 45, 45, 'basic',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),
        ('CPA', 'opt_out_sale', 'US-CO', 45, 45, 'none',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),
        ('CPA', 'opt_out_targeted_ads', 'US-CO', 45, 45, 'none',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),

        -- Connecticut — CTDPA, Public Act 22-15.
        ('CTDPA', 'delete', 'US-CT', 45, 45, 'basic',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),
        ('CTDPA', 'opt_out_sale', 'US-CT', 45, 45, 'none',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),
        ('CTDPA', 'opt_out_targeted_ads', 'US-CT', 45, 45, 'none',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),

        -- Utah — UCPA. The narrowest of the five in what it grants, and the same
        -- forty-five day answer window once a right does apply.
        --
        -- All three cite § 13-61-202 because that is where the obligation these rows
        -- actually encode lives: "respond to a consumer request without unreasonable
        -- delay, but in no case later than 45 days after receipt", plus the single
        -- 45-day extension. The rights themselves are enumerated a section earlier;
        -- citing that instead would point a reader at what may be asked rather than at
        -- how long there is to answer, which is the number stored here.
        ('UCPA', 'delete', 'US-UT', 45, 45, 'basic',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S202.html'),
        ('UCPA', 'opt_out_sale', 'US-UT', 45, 45, 'none',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S202.html'),
        ('UCPA', 'opt_out_targeted_ads', 'US-UT', 45, 45, 'none',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S202.html')
) AS seed (
    code,
    request_type,
    residency_scope,
    response_deadline_days,
    extension_days,
    verification_level,
    citation_url
), reviewer

-- An operator who has already entered their own reading of one of these keeps it. The
-- unique key is the regime, the request type and the scope, so a conflict here means
-- the instance already has a row saying what this one would say — and theirs may carry
-- a reviewer and a date this script cannot improve on.
ON CONFLICT (code, request_type, residency_scope) DO NOTHING;
