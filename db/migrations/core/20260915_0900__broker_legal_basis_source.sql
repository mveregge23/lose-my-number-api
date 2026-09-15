-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- A confirmation that a statute reaches a company becomes reviewed content, and reviewed
-- content has to say where it was read.
--
-- ---------------------------------------------------------------------------------
-- Why a confirmation is the company's to carry
-- ---------------------------------------------------------------------------------
--
-- broker_legal_basis was created as a table somebody fills in by hand, because
-- applicability turns on thresholds this system cannot verify and a guess would be a
-- guess presented as a legal position. That is still true. What has changed is where
-- the judgement is written down: a company's catalog file now lists the regimes that
-- reach it, and the sync writes the confirmations from that list, one per regime row.
--
-- It is the company's file rather than the regime's because it is a fact about the
-- company. And it is citable, which the original table never asked for: a state's
-- data-broker registry is a primary source that a given company is subject to that
-- state's law — a company on California's registry has declared itself a data broker
-- under the CCPA. The judgement no code was allowed to make is one a reviewer can now
-- follow a link to check.
--
-- ---------------------------------------------------------------------------------
-- Why evidence is required of the catalog and not of an operator
-- ---------------------------------------------------------------------------------
--
-- A catalog confirmation is a claim shipped to every instance, so it carries its
-- source. An operator's own confirmation already carries the one thing the table
-- always demanded — a person's name behind it — and may have been made on advice
-- nobody can link to. The constraint says exactly that, rather than pretending the two
-- are the same kind of row.

ALTER TABLE broker_legal_basis
    ADD COLUMN source text NOT NULL DEFAULT 'local'
        CONSTRAINT broker_legal_basis_source_known
        CHECK (source IN ('catalog', 'local')),

    ADD COLUMN evidence_url text NULL
        CONSTRAINT broker_legal_basis_evidence_present
        CHECK (evidence_url IS NULL OR length(btrim(evidence_url)) > 0),

    ADD CONSTRAINT broker_legal_basis_catalog_cites_evidence
        CHECK (source <> 'catalog' OR evidence_url IS NOT NULL);

COMMENT ON COLUMN broker_legal_basis.source IS
    'Whether the curated catalog owns this confirmation and may update or remove it, or '
    'whether it belongs to this instance and is left alone. Defaults to local: anything '
    'arriving by a route other than the catalog sync is somebody''s own.';

COMMENT ON COLUMN broker_legal_basis.evidence_url IS
    'Where it was read that this regime reaches this company — a registry entry, or the '
    'company''s own privacy notice. Required of the catalog, optional for an operator.';

COMMENT ON TABLE broker_legal_basis IS
    'Confirmed applicability: this regime governs this broker, confirmed by this person '
    'on this date, and where that was read. Curated in the company''s catalog file or '
    'entered by an operator. Never inferred.';
