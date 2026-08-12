-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The vault as a separate logical store (§4).
--
-- A schema rather than a database, for now. §4 calls for a store that "could start
-- as a separate schema, promoted to a separate database/service later without an API
-- shape change", and this is the first half of that: everything holding
-- envelope-encrypted PII is namespaced away from operational data from the very
-- first table, so nothing ever accidentally joins across the boundary in a general
-- query path. The second half is already in place — this set has its own connection
-- string and its own journal, so the promotion is a connection-string change here and
-- nothing else.
--
-- No grants yet. The role split that makes this boundary enforceable rather than
-- merely tidy (the API's day-to-day role having no rights in here at all, per §1's
-- "a breach of one shouldn't automatically yield the other") arrives with the vault
-- service in DBR-015/016, when there is something to grant access to.

CREATE SCHEMA IF NOT EXISTS vault;

COMMENT ON SCHEMA vault IS
    'Envelope-encrypted PII (§1, §4). Reached only through the Profile service and '
    'the scoped-release path used by workers — never joined into general query paths. '
    'Migrated as its own set from /db/migrations/vault.';
