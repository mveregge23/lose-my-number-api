-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Which broker rows the curated catalog owns, and which belong to whoever runs this
-- instance. The same split legal_basis got, for the same sync, with one difference in
-- what retraction means.
--
-- ---------------------------------------------------------------------------------
-- Why a retracted company is deactivated rather than deleted
-- ---------------------------------------------------------------------------------
--
-- A regime that leaves the catalog can be deleted: nothing operational points at it
-- except confirmations, and those are reviewed content the sync refuses to drop as a
-- side effect. A company is different. Every scan that ever asked it, every finding
-- on its site and every demand sent to it holds a foreign key to this row, and none of
-- those keys cascade — a demand somebody sent is history, and history that names a
-- company has to keep naming it after the company is gone.
--
-- So on every instance that ever used a company, deleting its row is refused by the
-- schema, and a sync that tried would fail the deploy over a scan somebody ran months
-- ago. Retraction sets active = false instead: the row stops being searched, paced or
-- sent to, and the rows that point at it keep pointing at something. A file that comes
-- back reactivates it under the same id, which is what recipes bind to.

ALTER TABLE broker
    ADD COLUMN source text NOT NULL DEFAULT 'local'
        CONSTRAINT broker_source_known
        CHECK (source IN ('catalog', 'local'));

COMMENT ON COLUMN broker.source IS
    'Whether the curated catalog owns this row and may update or deactivate it, or '
    'whether it belongs to this instance and is left alone. Defaults to local: anything '
    'arriving by a route other than the catalog sync is somebody''s own.';

-- No backfill. Unlike the regimes, no migration ever inserted a company, so there is
-- nothing to hand over: every row here today was entered by an operator and stays theirs.
