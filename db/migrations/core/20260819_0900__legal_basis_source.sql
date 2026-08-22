-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Which rows the curated catalog owns, and which belong to whoever runs this instance.
--
-- Until now every legal_basis row arrived the same way, by migration, so there was
-- nothing to tell apart. Content is moving to reviewed files under /catalog, applied on
-- deploy by a sync that has to be able to retract as well as add: a statute read wrongly
-- and corrected should stop governing requests when the file goes, not linger until
-- somebody deletes it by hand on every install.
--
-- ---------------------------------------------------------------------------------
-- Why a sync that can delete needs to know what it may delete
-- ---------------------------------------------------------------------------------
--
-- An operator may enter their own rows — a jurisdiction the shared catalog has not got
-- to yet, or a reading of their own they prefer. The design is explicit that nobody is
-- forced to take catalog content they have not looked at, and a sync that treated the
-- files as the whole truth would quietly delete exactly that work on the next deploy.
--
-- So the sync manages only what it wrote. A row marked 'catalog' is the shared
-- content's to insert, update and remove; a row marked 'local' is never touched, and
-- keeps its own reading even where a file claims the same regime. 'local' is the
-- default because a row appearing by any route other than the sync is, by definition,
-- somebody's own.

ALTER TABLE legal_basis
    ADD COLUMN source text NOT NULL DEFAULT 'local'
        CONSTRAINT legal_basis_source_known
        CHECK (source IN ('catalog', 'local'));

COMMENT ON COLUMN legal_basis.source IS
    'Whether the curated catalog owns this row and may update or remove it, or whether '
    'it belongs to this instance and is left alone. Defaults to local: anything arriving '
    'by a route other than the catalog sync is somebody''s own.';

-- The five jurisdictions seeded by migration are the content moving into files, so they
-- become the catalog's to manage. Named rather than matched on some marker because that
-- is what they are: a fixed, historical list of what that one migration inserted.
--
-- An operator who has since edited one of these and wants to keep their version can set
-- it back to 'local', which puts it permanently out of the sync's reach.
UPDATE legal_basis
    SET source = 'catalog'
    WHERE code IN ('CCPA', 'VCDPA', 'CPA', 'CTDPA', 'UCPA');
