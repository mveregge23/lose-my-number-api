// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Dbr.Migrator;
using DbUp.Engine.Output;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal(6, await postgres.QueryAsOwnerAsync<long>(
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

    [Fact]
    public async Task Every_table_with_row_level_security_has_an_entity_that_filters()
    {
        // The mismatch this catches is silent in both directions and invisible in
        // review: a migration opts a table into the boundary, the entity mapped to it
        // never implements ITenantScoped, and the application's own queries stop
        // narrowing. Row-level security still holds, so nothing breaks and nothing
        // says so — until a connection reaches the database as the wrong role and the
        // defence in depth that should have covered it was never there.
        using var scope = postgres.BuildServices().CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var unfiltered = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { } tableName || entityType.IsOwned())
            {
                continue;
            }

            var isProtected = await postgres.QueryAsOwnerAsync<bool>(
                $"SELECT coalesce((SELECT relrowsecurity FROM pg_class WHERE relname = '{tableName}'), false)");

            if (isProtected && entityType.GetDeclaredQueryFilters().Count == 0)
            {
                unfiltered.Add($"{entityType.ClrType.Name} -> {tableName}");
            }
        }

        Assert.True(
            unfiltered.Count == 0,
            "These tables are protected by row-level security but their entities have no query "
            + "filter, so the application asks for every tenant's rows and only the database "
            + "declines. Implement ITenantScoped, or filter explicitly if the entity is scoped by "
            + "something other than a tenant_id: " + string.Join(", ", unfiltered));
    }

    [Fact]
    public async Task Every_entity_that_belongs_to_a_tenant_sits_on_a_table_that_enforces_it()
    {
        // The other direction, and the dangerous one. Above, the database holds the
        // line while the application forgets to ask narrowly. Here the application
        // asks narrowly and nothing holds the line — so the day a query is written
        // without the filter, or reaches the database by some path the filter does not
        // cover, there is no second answer. It reads as protected in the C# and is not
        // protected anywhere.
        //
        // Tables genuinely shared across tenants are outside this by not implementing
        // ITenantScoped, which is a decision visible on the entity rather than an
        // omission in a migration.
        using var scope = postgres.BuildServices().CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var unprotected = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { } tableName
                || entityType.IsOwned()
                || !typeof(Dbr.Domain.Tenancy.ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var isProtected = await postgres.QueryAsOwnerAsync<bool>(
                $"SELECT coalesce((SELECT relrowsecurity FROM pg_class WHERE relname = '{tableName}'), false)");

            if (!isProtected)
            {
                unprotected.Add($"{entityType.ClrType.Name} -> {tableName}");
            }
        }

        Assert.True(
            unprotected.Count == 0,
            "These entities declare that they belong to a tenant, but their tables have no "
            + "row-level security — so the only thing keeping one account's rows out of another's "
            + "is the application remembering to filter. Add a CALL app.enable_tenant_rls to the "
            + "migration: " + string.Join(", ", unprotected));
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
