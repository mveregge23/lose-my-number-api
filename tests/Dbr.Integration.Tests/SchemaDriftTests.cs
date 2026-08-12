// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Integration.Tests;

/// <summary>
/// The backstop for owning the schema in hand-written SQL: the model and the database
/// have to still agree, and nothing but this notices when they stop.
/// </summary>
/// <remarks>
/// The real assertion — <see cref="The_model_matches_the_migrated_schema"/> — is
/// vacuous until the first entity exists, so the probes below verify the detector
/// itself. A drift detector nobody has watched catch drift is an assumption, and one
/// that would be discovered to be broken at exactly the moment it was needed.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SchemaDriftTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _table = $"drift_probe_{Guid.NewGuid():N}";
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _services = postgres.BuildServices();

        await postgres.ExecuteAsOwnerAsync(
            $"""
             CREATE TABLE public.{_table} (
                 id        uuid PRIMARY KEY,
                 tenant_id uuid NOT NULL,
                 note      text NOT NULL,
                 seen_at   timestamp with time zone NULL
             );
             """);
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.ExecuteAsOwnerAsync($"DROP TABLE IF EXISTS public.{_table};");
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task The_model_matches_the_migrated_schema()
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DbrDbContext>();

        var drift = await SchemaDrift.DetectAsync(context, postgres.ConnectionString);
        var queryFailures = await SchemaDrift.ProbeEveryEntityAsync(context);

        Assert.Empty(drift);
        Assert.Empty(queryFailures);
    }

    [Fact]
    public async Task A_model_that_matches_reports_no_drift()
    {
        // Guards the other direction: a detector that flagged a correct mapping would
        // be worse than none, because the first response to a false alarm is to stop
        // believing the alarm.
        using var context = ProbeContext<MatchingProbe>();

        Assert.Empty(await SchemaDrift.DetectAsync(context, postgres.ConnectionString));
        Assert.Empty(await SchemaDrift.ProbeEveryEntityAsync(context));
    }

    [Fact]
    public async Task A_missing_table_is_caught()
    {
        using var context = ProbeContext<MatchingProbe>("table_that_was_never_created");

        var drift = await SchemaDrift.DetectAsync(context, postgres.ConnectionString);

        Assert.Contains(drift, message => message.Contains("does not exist", StringComparison.Ordinal));
        Assert.NotEmpty(await SchemaDrift.ProbeEveryEntityAsync(context));
    }

    [Fact]
    public async Task A_renamed_column_is_caught()
    {
        // The realistic accident: a migration renames note -> body and the entity is
        // never updated. Everything still compiles.
        using var context = ProbeContext<RenamedColumnProbe>();

        var drift = await SchemaDrift.DetectAsync(context, postgres.ConnectionString);

        Assert.Contains(drift, message => message.Contains("body", StringComparison.Ordinal));
        Assert.NotEmpty(await SchemaDrift.ProbeEveryEntityAsync(context));
    }

    [Fact]
    public async Task A_type_mismatch_is_caught()
    {
        // Not reachable by querying an empty table — nothing is materialized, so
        // nothing is cast. This is why the metadata comparison exists alongside the
        // query probe rather than instead of it.
        using var context = ProbeContext<WrongTypeProbe>();

        var drift = await SchemaDrift.DetectAsync(context, postgres.ConnectionString);

        Assert.Contains(drift, message => message.Contains("mapped as", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_nullability_mismatch_is_caught()
    {
        // Also invisible to the query probe, and the one that corrupts data rather
        // than failing loudly: the model thinks a column is optional, the database
        // rejects the insert at the worst possible moment.
        using var context = ProbeContext<WrongNullabilityProbe>();

        var drift = await SchemaDrift.DetectAsync(context, postgres.ConnectionString);

        Assert.Contains(drift, message => message.Contains("nullable", StringComparison.Ordinal));
    }

    private ProbeDbContext<TEntity> ProbeContext<TEntity>(string? tableName = null)
        where TEntity : class =>
        new(new DbContextOptionsBuilder<ProbeDbContext<TEntity>>()
                .UseDbr(postgres.ConnectionString)
                .Options,
            tableName ?? _table);

    /// <summary>Maps <typeparamref name="TEntity"/> onto the probe table.</summary>
    private sealed class ProbeDbContext<TEntity>(
        DbContextOptions<ProbeDbContext<TEntity>> options,
        string tableName) : DbContext(options), IProbeContext
        where TEntity : class
    {
        public string TableName => tableName;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.ReplaceService<IModelCacheKeyFactory, ProbeModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TEntity>().ToTable(tableName);
    }

    private interface IProbeContext
    {
        string TableName { get; }
    }

    /// <summary>
    /// Without this, two probes of the same entity type pointed at different tables
    /// would share one model: EF caches by context type, and a table name passed to
    /// the constructor is not part of that key. The second probe would then silently
    /// be tested against the first one's mapping — which is how this class briefly
    /// reported that a table which does not exist matches the model perfectly.
    /// </summary>
    private sealed class ProbeModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((IProbeContext)context).TableName, designTime);
    }

    private sealed class MatchingProbe
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; init; }

        public string Note { get; init; } = string.Empty;

        public DateTimeOffset? SeenAt { get; init; }
    }

    private sealed class RenamedColumnProbe
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; init; }

        public string Body { get; init; } = string.Empty;
    }

    private sealed class WrongTypeProbe
    {
        public Guid Id { get; init; }

        // tenant_id is uuid in the table.
        public string TenantId { get; init; } = string.Empty;
    }

    private sealed class WrongNullabilityProbe
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; init; }

        // note is NOT NULL in the table.
        public string? Note { get; init; }
    }
}
