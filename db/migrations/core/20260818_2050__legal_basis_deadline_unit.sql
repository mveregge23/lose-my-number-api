-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- What unit a regime counts its deadline in.
--
-- Nearly every privacy statute answers in calendar days, which is why the column
-- holding the number never needed to say so. California's opt-out rule does not: a
-- business must stop selling "no later than 15 business days from the date the business
-- receives the request", and fifteen business days is about twenty-one calendar ones.
--
-- ---------------------------------------------------------------------------------
-- Why this is a column rather than a conversion at the point of writing the row
-- ---------------------------------------------------------------------------------
--
-- The alternative was to convert on the way in and store 21, which reads correctly and
-- is wrong in a way that gets worse rather than better. The row would no longer say
-- what the statute says, so anybody checking it against the citation beside it finds a
-- number that is not in the source — and the conversion is lossy in a direction nobody
-- can recover: business days skip public holidays, so the real date drifts a day or two
-- further out and no amount of arithmetic on a stored 21 gets it back.
--
-- Storing the rule as written moves the conversion to where the date is actually
-- computed, which is the only place that knows the date the clock started and can
-- therefore count weekends and holidays properly.
--
-- The unit governs this row's whole clock, extension_days included: an extension is
-- more of the same regime's time, and a statute counting one in business days and the
-- other in calendar days would be a strange thing that none of these five do.

ALTER TABLE legal_basis
    ADD COLUMN deadline_unit text NOT NULL DEFAULT 'calendar'
        CONSTRAINT legal_basis_deadline_unit_known
        CHECK (deadline_unit IN ('calendar', 'business'));

COMMENT ON COLUMN legal_basis.deadline_unit IS
    'How response_deadline_days and extension_days are counted. Calendar unless the '
    'regime itself says business days, which is rare enough that calendar is the default '
    'and common enough that it cannot be assumed.';

-- Defaulted rather than left for each row to state, because calendar is what a regime
-- means when it says nothing, and a row that forgot to answer should land on the common
-- case rather than on an error. The check constraint is what stops it landing on a
-- third value nobody has taught the code to read.
