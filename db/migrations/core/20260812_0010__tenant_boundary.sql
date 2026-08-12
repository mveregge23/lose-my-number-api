-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- The tenant boundary (§4): the machinery, before there is a tenant-scoped table to
-- apply it to. Every such table from DBR-008 onward opts in with a single
-- `CALL app.enable_tenant_rls('public.<table>')`.
--
-- Why this is more than a policy definition. §4 asks for RLS to be "the real tenant
-- boundary, not just an EF Core .Where(tenantId) convention", so that "a missing
-- filter in application code fails closed instead of leaking rows". Postgres will not
-- give you that for free: RLS is skipped entirely for a superuser, for any role
-- holding BYPASSRLS, and — unless the table is FORCEd — for the table's own owner.
-- The role this stack connects as is all three at once. Enabling a policy and
-- stopping there would have produced a boundary that reads correctly in the schema,
-- passes a casual look, and isolates nothing.

-- ---------------------------------------------------------------------------------
-- 1. Where the current tenant lives
-- ---------------------------------------------------------------------------------

CREATE SCHEMA IF NOT EXISTS app;

COMMENT ON SCHEMA app IS
    'Machinery for the tenant boundary of §4 — the current-tenant accessor and the '
    'procedure tables use to opt into row-level security. No application data.';

-- Set per connection by TenantSessionInterceptor, read by every tenant policy.
--
-- Unset, blank, or absent all resolve to NULL, and `tenant_id = NULL` is NULL rather
-- than true — so a connection that never identified a tenant sees no rows at all
-- instead of every row. That is the fail-closed property, and it comes from SQL's
-- three-valued logic rather than from anyone remembering to check.
--
-- A malformed value raises instead of resolving to NULL. Only the interceptor writes
-- this setting, and it writes a Guid, so a non-uuid here means a bug or an injection
-- attempt; neither should be quietly downgraded to "no tenant".
CREATE OR REPLACE FUNCTION app.current_tenant_id()
    RETURNS uuid
    LANGUAGE sql
    STABLE
    -- Empty search_path: this runs inside every policy check, so it must not be
    -- resolvable against a caller-controlled path.
    SET search_path = pg_catalog
AS $$
    SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
$$;

COMMENT ON FUNCTION app.current_tenant_id() IS
    'The tenant this connection is acting for, or NULL when none was set. NULL makes '
    'every tenant policy match zero rows (§4, fail closed).';

-- ---------------------------------------------------------------------------------
-- 2. A role that RLS actually applies to
-- ---------------------------------------------------------------------------------

-- NOLOGIN on purpose: the application reaches this role with SET ROLE over its
-- existing connection, never by authenticating as it. That is what lets the tenant
-- boundary hold without introducing a second credential to provision, distribute and
-- rotate — a self-hoster's `docker compose up` keeps working untouched, and a
-- deployment that already has a least-privilege login role loses nothing by also
-- passing through this one.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dbr_app') THEN
        CREATE ROLE dbr_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
    END IF;

    -- Whoever runs migrations must be able to SET ROLE to it; so must anyone else who
    -- later connects the application. Granting to the migrating role covers the
    -- single-role setup this stack uses today.
    EXECUTE format('GRANT dbr_app TO %I', current_user);
END
$$;

COMMENT ON ROLE dbr_app IS
    'The role the application acts as (via SET ROLE) so row-level security applies to '
    'it. Deliberately NOSUPERUSER/NOBYPASSRLS/NOLOGIN — see §4.';

GRANT USAGE ON SCHEMA app TO dbr_app;
GRANT EXECUTE ON FUNCTION app.current_tenant_id() TO dbr_app;
GRANT USAGE ON SCHEMA public TO dbr_app;

-- ---------------------------------------------------------------------------------
-- 3. How a table opts in
-- ---------------------------------------------------------------------------------

-- One call per tenant-scoped table, from that table's own migration. Centralised so
-- the thirteen tables of §3 cannot each get the policy subtly different, and so a
-- table cannot be given a policy while quietly missing FORCE.
CREATE OR REPLACE PROCEDURE app.enable_tenant_rls(target_table regclass)
    LANGUAGE plpgsql
AS $$
BEGIN
    -- A table without tenant_id would otherwise get a policy referencing a column
    -- that doesn't exist, and fail at query time rather than migration time.
    IF NOT EXISTS (
        SELECT FROM pg_attribute
        WHERE attrelid = target_table
          AND attname = 'tenant_id'
          AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION
            'app.enable_tenant_rls: % has no tenant_id column. Tenant-scoped tables '
            'carry tenant_id uuid NOT NULL; broker_health is the one table in §3 that '
            'is deliberately outside this boundary and must not call this.',
            target_table;
    END IF;

    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', target_table);

    -- FORCE covers the case the ENABLE above misses: the table's own owner. Belt and
    -- braces next to the dbr_app role — a deployment that points the application at
    -- the owning role still isolates.
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', target_table);

    -- USING filters what is readable; WITH CHECK stops a write from placing a row
    -- under another tenant. Without the second half, isolation is read-only and a
    -- bug could write across the boundary it cannot read across.
    EXECUTE format(
        'CREATE POLICY tenant_isolation ON %s'
        '    USING (tenant_id = app.current_tenant_id())'
        '    WITH CHECK (tenant_id = app.current_tenant_id())',
        target_table);

    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %s TO dbr_app', target_table);
END
$$;

COMMENT ON PROCEDURE app.enable_tenant_rls(regclass) IS
    'Opt a tenant-scoped table into the §4 boundary: enable + force RLS, create the '
    'tenant_isolation policy over app.current_tenant_id(), and grant the app role DML.';
