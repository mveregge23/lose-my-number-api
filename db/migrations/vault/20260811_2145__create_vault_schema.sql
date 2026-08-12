-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The vault as a separate logical store.
--
-- Personally identifying data is kept apart from operational data so that
-- compromising one does not hand over the other. A schema rather than a database for
-- now, but namespaced from the very first table, so nothing can accidentally join
-- across the boundary in a general query path — and because this migration set
-- already has its own connection string and its own journal, moving it to a database
-- of its own later is a connection-string change and nothing more.
--
-- No grants yet. The role split that makes the boundary enforceable rather than
-- merely tidy — the application's day-to-day role holding no rights in here at all —
-- arrives with the vault service, when there is something to grant access to.

CREATE SCHEMA IF NOT EXISTS vault;

COMMENT ON SCHEMA vault IS
    'Envelope-encrypted personally identifying data. Reached only through the profile '
    'service and the scoped-release path used by workers, never joined into general '
    'query paths. Migrated as its own set from /db/migrations/vault.';
