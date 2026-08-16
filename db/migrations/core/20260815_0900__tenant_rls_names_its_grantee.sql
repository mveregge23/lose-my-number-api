-- SPDX-FileCopyrightText: 2026 Max Veregge
-- SPDX-License-Identifier: AGPL-3.0-or-later
--
-- Lets a table say which role gets to read and write it.
--
-- Until now every tenant-scoped table granted its DML to dbr_app, because every
-- tenant-scoped table held operational data and there was one role that touched it.
-- The vault schema breaks that: the whole point of keeping identifying data in a
-- store of its own is that the role serving day-to-day traffic holds no rights in
-- there at all, so its tables have to grant to a different role.
--
-- Parameterising the existing procedure rather than writing a second one for the
-- vault. A copy would start identical and drift — someone strengthens the policy here
-- and the tables holding names and addresses quietly keep the old one, which is
-- exactly backwards. One definition of what "opted in" means, two grantees.
--
-- Replacing rather than overloading, for the same reason as the last change to this
-- procedure: a defaulted parameter alongside the older signature makes the shorter
-- call ambiguous, and Postgres refuses it when the call runs rather than when the
-- migration does.

DROP PROCEDURE IF EXISTS app.enable_tenant_rls(regclass, name);

CREATE OR REPLACE PROCEDURE app.enable_tenant_rls(
    target_table regclass,
    tenant_column name DEFAULT 'tenant_id',
    grantee name DEFAULT 'dbr_app')
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

    -- A misspelled role would otherwise produce a table nobody can read, discovered on
    -- the first query rather than here.
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = grantee) THEN
        RAISE EXCEPTION
            'app.enable_tenant_rls: role % does not exist. The role a table grants to '
            'must be created before the table opts in.', grantee;
    END IF;

    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', target_table);

    -- FORCE covers the case ENABLE misses: the table's own owner. Belt and braces
    -- next to the application roles, so a deployment that points the application at the
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

    EXECUTE format(
        'GRANT SELECT, INSERT, UPDATE, DELETE ON %s TO %I', target_table, grantee);
END
$$;

COMMENT ON PROCEDURE app.enable_tenant_rls(regclass, name, name) IS
    'Opt a table into the tenant boundary: enable and force row-level security, create '
    'the tenant_isolation policy over app.current_tenant_id(), and grant one role its '
    'DML. Pass a column name for a table scoped by something other than tenant_id, and '
    'a role name for a table outside the reach of the day-to-day application role.';
