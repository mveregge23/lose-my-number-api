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
-- Every row stores the number the statute prints
-- ---------------------------------------------------------------------------------
--
-- Fourteen of these fifteen rows count in calendar days and one does not, so each says
-- which it is rather than being converted on the way in. A converted number reads fine
-- and is wrong in the way that matters: it is not the number beside the citation, so
-- anybody checking the row against its source finds a figure that is not in it — and
-- business days skip public holidays, which no arithmetic on a stored calendar figure
-- can put back. Turning days into a date is the job of the code that knows when the
-- clock started.

-- Who has actually read each of these, which is not the same answer for all five.
--
-- Provenance is the point of this table: reviewed_by answers "who read the statute and
-- stands behind this reading". Review happened jurisdiction by jurisdiction, so this is
-- keyed that way rather than set once — and the ones nobody has finished say so instead
-- of naming somebody who has not.
--
-- The reviewer is a GitHub handle because that is how this repository already identifies
-- the people who answer for its content, in CODEOWNERS, and because it is checkable by
-- whoever reads it off the API without publishing an email address.
--
-- The date is a literal rather than now(). It records when the reading happened, and an
-- install running this migration next year did not review anything — a reviewed_at that
-- moved with the install would make stale content look freshly checked, which for a
-- statutory deadline is the misrepresentation that matters.
WITH reviewer AS (
    SELECT *
    FROM (VALUES
        -- Read against primary text and signed off.
        ('VCDPA', '@mveregge23',                                 '2026-08-18'::timestamptz),
        ('CPA',   '@mveregge23',                                 '2026-08-18'::timestamptz),
        ('UCPA',  '@mveregge23',                                 '2026-08-18'::timestamptz),

        -- Still open. California's opt-out extension and Connecticut's remaining
        -- judgement calls are unsettled, and a row is unreviewed until all of it is.
        ('CCPA',  'unreviewed seed — pending maintainer review', '2026-08-18'::timestamptz),
        ('CTDPA', 'unreviewed seed — pending maintainer review', '2026-08-18'::timestamptz)
    ) AS r (code, reviewed_by, reviewed_at)
)
INSERT INTO legal_basis (
    code,
    request_type,
    residency_scope,
    response_deadline_days,
    extension_days,
    deadline_unit,
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
    seed.deadline_unit,
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
        ('CCPA', 'delete', 'US-CA', 45, 45, 'calendar', 'basic',
         'https://leginfo.legislature.ca.gov/faces/codes_displaySection.xhtml?lawCode=CIV&sectionNum=1798.105'),

        -- Opting out of sale, and of sharing for cross-context behavioural advertising,
        -- is deliberately not a verifiable request — requiring proof of identity to
        -- stop a sale would put a toll on the right itself.
        --
        -- The clock here is unlike the other four states' in two ways. It is a deadline
        -- to *comply* rather than to answer — the obligation is to stop selling, so
        -- there is no separate reply step for it to run to — and it is counted in
        -- business days: "cease selling to and/or sharing with third parties the
        -- consumer's personal information as soon as feasibly possible, but no later
        -- than 15 business days from the date the business receives the request."
        -- Stored as fifteen, marked business, and left for the deadline calculation to
        -- turn into a date — these are the only two rows here that are not calendar.
        --
        -- No extension, and that is the section's own wording rather than an omission.
        -- The forty-five-plus-forty-five in § 1798.130(a)(2) enumerates what it extends:
        -- "the time period to provide the required information, to correct inaccurate
        -- personal information, or to delete personal information". Opting out is none
        -- of those three, and that subdivision does not address opt-out timing at all —
        -- it is a rule about answering a verifiable consumer request, and an opt-out is
        -- deliberately not one.
        ('CCPA', 'opt_out_sale', 'US-CA', 15, 0, 'business', 'none',
         'https://cppa.ca.gov/regulations/pdf/ccpa_statute_eff_20260101.pdf'),
        ('CCPA', 'opt_out_targeted_ads', 'US-CA', 15, 0, 'business', 'none',
         'https://cppa.ca.gov/regulations/pdf/ccpa_statute_eff_20260101.pdf'),

        -- Virginia — VCDPA. Forty-five days from receipt, extendable once by another
        -- forty-five. Virginia authenticates every consumer request, opt-outs included:
        -- unlike Connecticut, whose act carves opt-outs out of authentication by name,
        -- this one does not, so the general rule reaches them. That is why these three
        -- agree on verification where California's and Connecticut's do not.
        ('VCDPA', 'delete', 'US-VA', 45, 45, 'calendar', 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),
        ('VCDPA', 'opt_out_sale', 'US-VA', 45, 45, 'calendar', 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),
        ('VCDPA', 'opt_out_targeted_ads', 'US-VA', 45, 45, 'calendar', 'basic',
         'https://law.lis.virginia.gov/vacode/title59.1/chapter53/section59.1-577/'),

        -- Colorado — CPA. Forty-five days, extendable once by forty-five. Opt-outs are
        -- reachable by a universal opt-out signal, which a controller has to honour
        -- without authenticating whoever sent it.
        ('CPA', 'delete', 'US-CO', 45, 45, 'calendar', 'basic',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),
        ('CPA', 'opt_out_sale', 'US-CO', 45, 45, 'calendar', 'none',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),
        ('CPA', 'opt_out_targeted_ads', 'US-CO', 45, 45, 'calendar', 'none',
         'https://content.leg.colorado.gov/sites/default/files/2021a_190_signed.pdf'),

        -- Connecticut — CTDPA, Public Act 22-15. Forty-five days from receipt with one
        -- forty-five day extension, per § 4(c)(1), covering every consumer right the act
        -- grants — opt-outs included.
        --
        -- The act also contains a fifteen-day clock, and it is not this one. § 6(a)(6)
        -- requires a controller to stop processing within fifteen days of a consumer
        -- *revoking consent*, which is a different act from asking to opt out of a sale:
        -- revocation withdraws a permission the consumer previously gave, and an opt-out
        -- is a right exercised against processing that never needed permission. Nothing
        -- here models revocation, so no row encodes that fifteen. Recorded because the
        -- two numbers sit a few pages apart in one document and the wrong one is the
        -- easier to reach for.
        --
        -- The opt-outs need no verification, and that is read rather than assumed:
        -- § 4(c)(4) says "a controller shall not be required to authenticate an opt-out
        -- request", and the act's definition of "authenticate" reaches only the rights
        -- in subdivisions (1) to (4) — the opt-outs are (5). Deletion is (3), so it is
        -- authenticated, which is why these three rows disagree. Virginia's do not
        -- disagree: its statute carves nothing out, so an opt-out there is authenticated
        -- like everything else.
        ('CTDPA', 'delete', 'US-CT', 45, 45, 'calendar', 'basic',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),
        ('CTDPA', 'opt_out_sale', 'US-CT', 45, 45, 'calendar', 'none',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),
        ('CTDPA', 'opt_out_targeted_ads', 'US-CT', 45, 45, 'calendar', 'none',
         'https://www.cga.ct.gov/2022/act/pa/pdf/2022PA-00015-R00SB-00006-PA.PDF'),

        -- Utah — UCPA. The narrowest of the five in what it grants, and the same
        -- forty-five day answer window once a right does apply.
        --
        -- All three cite § 13-61-203, which carries the timeline these rows encode.
        -- The rights themselves — deletion, opting out of a sale, and opting out of
        -- targeted advertising, so all three of these belong here — are enumerated in
        -- § 13-61-201, which is worth reading alongside it but is not where the number
        -- stored here comes from.
        ('UCPA', 'delete', 'US-UT', 45, 45, 'calendar', 'basic',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S203.html'),
        ('UCPA', 'opt_out_sale', 'US-UT', 45, 45, 'calendar', 'none',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S203.html'),
        ('UCPA', 'opt_out_targeted_ads', 'US-UT', 45, 45, 'calendar', 'none',
         'https://le.utah.gov/xcode/Title13/Chapter61/13-61-S203.html')
) AS seed (
    code,
    request_type,
    residency_scope,
    response_deadline_days,
    extension_days,
    deadline_unit,
    verification_level,
    citation_url
)
JOIN reviewer ON reviewer.code = seed.code

-- An operator who has already entered their own reading of one of these keeps it. The
-- unique key is the regime, the request type and the scope, so a conflict here means
-- the instance already has a row saying what this one would say — and theirs may carry
-- a reviewer and a date this script cannot improve on.
ON CONFLICT (code, request_type, residency_scope) DO NOTHING;
