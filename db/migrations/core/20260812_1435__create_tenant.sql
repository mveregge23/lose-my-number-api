-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The tenant: an account, and the thing every other tenant-scoped row belongs to.
--
-- Deliberately thin. Everything identifying a person — names, addresses, dates of
-- birth, contact details — lives envelope-encrypted in the vault schema, not here.
-- What is left is the operational shell: an address to reach the account at, when it
-- was opened, and whether it is allowed to act.

CREATE TABLE tenant (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    email        text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    mfa_enabled  boolean     NOT NULL DEFAULT false,

    -- Text with a check rather than a Postgres enum type. Adding a value to an enum
    -- is a migration that cannot run inside a transaction on older servers and cannot
    -- be reversed at all; a check constraint is dropped and recreated like anything
    -- else. The set is small and the cost of getting it wrong is a deployment.
    status       text        NOT NULL DEFAULT 'active'
                             CONSTRAINT tenant_status_known
                             CHECK (status IN ('active', 'suspended'))
);

COMMENT ON TABLE tenant IS
    'An account. Identifying data about the person behind it lives in the vault '
    'schema, encrypted per tenant — this table holds only what operating the account '
    'requires.';

COMMENT ON COLUMN tenant.status IS
    'active | suspended. Suspension is enforced before authentication and independently '
    'of billing, so a suspended account cannot act in any deployment mode.';

-- One account per address, case-insensitively: addresses are handed out by mail
-- providers that treat them that way, so two rows differing only in capitalisation
-- are the same person and a duplicate signup.
--
-- An expression index rather than storing the address folded: the original casing is
-- what a message gets addressed to, and people notice when that changes.
CREATE UNIQUE INDEX tenant_email_unique ON tenant (lower(email));

-- Scoped by its own primary key, not a tenant_id — the tenant this row belongs to is
-- the one it is.
CALL app.enable_tenant_rls('public.tenant', 'id');

-- A consequence worth stating rather than discovering: with this policy in place, a
-- lookup by email returns nothing unless the caller already knows which tenant it is
-- acting as. That is correct — an unauthenticated caller must not be able to probe
-- which addresses have accounts — but it means the login path cannot simply SELECT
-- from this table to find out who is signing in. Authentication will need a narrow,
-- deliberate way through: a SECURITY DEFINER function returning only what a login
-- attempt needs, rather than a role that can read the table freely. Left for the
-- story that builds authentication, since what it needs to return is not yet known.
--
-- Signup is unaffected and works without any exception: the API generates the new
-- tenant's id, acts as that tenant, and inserts. WITH CHECK then means a caller can
-- only ever create the account it is already claiming to be.
