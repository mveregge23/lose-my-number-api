// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Integration.Tests.Fixtures;
using Dbr.Migrator;
using DbUp.Engine.Output;

namespace Dbr.Integration.Tests;

/// <summary>
/// What the real migration scripts actually build. The fixture has already run them
/// once — reaching these tests at all means the migrator succeeded against a clean
/// database.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Both_sets_journal_what_they_applied()
    {
        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.{MigrationSet.Core.JournalTable}"));

        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            $"SELECT count(*) FROM public.{MigrationSet.Vault.JournalTable}"));
    }

    [Fact]
    public async Task Running_again_applies_nothing_and_still_succeeds()
    {
        // Every `docker compose up` re-runs the migrator, so a second run has to be a
        // no-op rather than an error. It is also the only thing standing between a
        // restart and a failed startup gate.
        var log = new ThrowawayLog();

        var exitCode = new MigrationRunner(
            typeof(MigrationSet).Assembly,
            _ => postgres.ConnectionString,
            log).Run(MigrationSet.All);

        Assert.Equal(MigrationRunner.ExitSuccess, exitCode);
    }

    [Fact]
    public async Task The_vault_schema_exists_and_is_separate_from_the_core_one()
    {
        Assert.Equal(1, await postgres.QueryAsOwnerAsync<long>(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'vault'"));
    }

    [Fact]
    public async Task The_application_role_cannot_bypass_row_level_security()
    {
        // If a future migration ever granted this role BYPASSRLS or superuser — by
        // hand or by inheriting from a role that has it — every isolation test in the
        // suite would keep passing while isolating nothing. This asserts the property
        // directly rather than through its effects.
        var canBypass = await postgres.QueryAsOwnerAsync<bool>(
            """
            SELECT (rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole)
            FROM pg_roles WHERE rolname = 'dbr_app'
            """);

        Assert.False(canBypass);
    }

    [Fact]
    public async Task Enabling_the_boundary_refuses_a_table_without_a_tenant_column()
    {
        // The guard exists so the mistake surfaces when the migration runs rather than
        // when a query later references a column the policy assumed.
        var table = $"no_tenant_probe_{Guid.NewGuid():N}";
        await postgres.ExecuteAsOwnerAsync($"CREATE TABLE public.{table} (id int);");

        try
        {
            var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                postgres.ExecuteAsOwnerAsync($"CALL app.enable_tenant_rls('public.{table}');"));

            Assert.Contains("has no tenant_id column", exception.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await postgres.ExecuteAsOwnerAsync($"DROP TABLE IF EXISTS public.{table};");
        }
    }

    [Fact]
    public async Task Enabling_the_boundary_forces_row_level_security_on_the_table()
    {
        // ENABLE alone leaves the table's owner exempt. Since the migrator creates
        // every table and therefore owns it, FORCE is what stops a deployment that
        // connects as the owning role from seeing everything.
        var table = $"force_probe_{Guid.NewGuid():N}";
        await postgres.CreateTenantScopedTableAsync(table);

        try
        {
            var forced = await postgres.QueryAsOwnerAsync<bool>(
                $"SELECT relforcerowsecurity FROM pg_class WHERE relname = '{table}'");

            var enabled = await postgres.QueryAsOwnerAsync<bool>(
                $"SELECT relrowsecurity FROM pg_class WHERE relname = '{table}'");

            Assert.True(enabled);
            Assert.True(forced);
        }
        finally
        {
            await postgres.ExecuteAsOwnerAsync($"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private sealed class ThrowawayLog : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }

        public void LogDebug(string format, params object[] args) { }

        public void LogInformation(string format, params object[] args) { }

        public void LogWarning(string format, params object[] args) { }

        public void LogError(string format, params object[] args) { }

        public void LogError(Exception ex, string format, params object[] args) { }
    }
}
