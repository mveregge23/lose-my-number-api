-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Lets a table say which column carries its tenant.
--
-- Almost every table has a tenant_id, and that stays the default. The tenant table
-- itself is the exception and always will be: the tenant it belongs to is the one it
-- *is*, so its scoping column is its own primary key. Giving it a redundant tenant_id
-- equal to id would work and would be a lie in the schema — a foreign key to itself,
-- which anyone reading the table later has to stop and reason about.
--
-- The alternative was to hand-write the tenant table's policy in its own migration.
-- That splits the definition of what "opted in" means across two places, and the
-- second one drifts: someone changes the procedure to add a rule and the tenant
-- table — the one whose isolation matters most — silently doesn't get it.
--
-- Replacing rather than overloading. A defaulted second parameter alongside the
-- existing single-argument version makes a one-argument call ambiguous, and Postgres
-- refuses it at runtime rather than at migration time.

DROP PROCEDURE IF EXISTS app.enable_tenant_rls(regclass);

CREATE OR REPLACE PROCEDURE app.enable_tenant_rls(
    target_table regclass,
    tenant_column name DEFAULT 'tenant_id')
    LANGUAGE plpgsql
AS $$
BEGIN
    -- Catching this here means the mistake surfaces when the migration runs rather
    -- than when a query later references a column the policy assumed.
    IF NOT EXISTS (
        SELECT FROM pg_attribute
        WHERE attrelid = target_table
          AND attname = tenant_column
          AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION
            'app.enable_tenant_rls: % has no % column. Tenant-scoped tables carry '
            'tenant_id uuid NOT NULL, or name their own scoping column. Tables holding '
            'data shared across tenants sit outside this boundary and must not call this.',
            target_table, tenant_column;
    END IF;

    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', target_table);

    -- FORCE covers the case ENABLE misses: the table's own owner. Belt and braces
    -- next to the dbr_app role, so a deployment that points the application at the
    -- owning role still isolates.
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', target_table);

    -- USING filters what is readable; WITH CHECK stops a write from placing a row
    -- under another tenant. Without the second half, isolation is read-only and a bug
    -- could write across the boundary it cannot read across.
    EXECUTE format(
        'CREATE POLICY tenant_isolation ON %s'
        '    USING (%I = app.current_tenant_id())'
        '    WITH CHECK (%I = app.current_tenant_id())',
        target_table, tenant_column, tenant_column);

    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %s TO dbr_app', target_table);
END
$$;

COMMENT ON PROCEDURE app.enable_tenant_rls(regclass, name) IS
    'Opt a table into the tenant boundary: enable and force row-level security, create '
    'the tenant_isolation policy over app.current_tenant_id(), and grant the '
    'application role its DML. Pass a column name for a table scoped by something '
    'other than tenant_id.';
