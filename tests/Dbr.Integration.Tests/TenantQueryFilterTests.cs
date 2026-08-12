// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Dbr.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dbr.Integration.Tests;

/// <summary>
/// The query filters, observed in the only situation where they matter: with
/// row-level security not enforcing.
/// </summary>
/// <remarks>
/// <para>
/// While the database policies are working, the filters are invisible — every
/// assertion about them would pass with the filters deleted, because the policies
/// would have returned the same rows. So these tests deliberately connect in a way
/// that bypasses the policies entirely: no session interceptor, therefore no
/// <c>SET ROLE</c>, therefore a superuser connection that row-level security skips.
/// Whatever the filters do here, they do on their own.
/// </para>
/// <para>
/// That is also a fair model of what the filters are for. They are not the tenant
/// boundary; they are what still narrows a query when the boundary has been
/// misconfigured somewhere the application cannot see — a table whose migration
/// forgot to opt in, a revoked <c>FORCE</c>, a connection that arrived as the wrong
/// role.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TenantQueryFilterTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _table = $"filter_probe_{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync()
    {
        // Deliberately not opted into row-level security. Adding the policy here would
        // hide exactly the thing under test.
        await postgres.ExecuteAsOwnerAsync(
            $"""
             CREATE TABLE public.{_table} (
                 id        uuid PRIMARY KEY,
                 tenant_id uuid NOT NULL,
                 note      text NOT NULL
             );
             INSERT INTO public.{_table} (id, tenant_id, note) VALUES
                 (gen_random_uuid(), '{Alice}', 'alice note'),
                 (gen_random_uuid(), '{Bob}', 'bob note');
             """);
    }

    public async ValueTask DisposeAsync() =>
        await postgres.ExecuteAsOwnerAsync($"DROP TABLE IF EXISTS public.{_table};");

    [Fact]
    public async Task Every_tenant_scoped_entity_is_filtered_without_being_listed()
    {
        // The filter is applied by convention, to anything implementing ITenantScoped.
        // Listing entities by hand would work until someone forgot one, and the
        // symptom of forgetting one is a cross-tenant read.
        using var context = ContextFor(Alice);

        Assert.NotEmpty(context.Model.FindEntityType(typeof(FilterProbe))!.GetDeclaredQueryFilters());
    }

    [Fact]
    public async Task A_query_returns_only_the_current_tenants_rows()
    {
        using var context = ContextFor(Alice);

        var notes = await context.Set<FilterProbe>().Select(p => p.Note).ToListAsync();

        Assert.Equal(["alice note"], notes);
    }

    [Fact]
    public async Task The_connection_really_is_bypassing_row_level_security()
    {
        // Without this the suite proves nothing: if the connection happened to be
        // subject to the policies, every assertion here would pass whether the filters
        // existed or not. IgnoreQueryFilters removes the only thing under test, and
        // both rows come back — so the database is not narrowing anything.
        using var context = ContextFor(Alice);

        var notes = await context.Set<FilterProbe>()
            .IgnoreQueryFilters()
            .Select(p => p.Note)
            .OrderBy(note => note)
            .ToListAsync();

        Assert.Equal(["alice note", "bob note"], notes);
    }

    [Fact]
    public async Task A_unit_of_work_with_no_tenant_reads_nothing()
    {
        // Same fail-closed direction the database policies take: an unset tenant
        // compares against NULL, which is never true.
        using var context = ContextFor(null);

        Assert.Empty(await context.Set<FilterProbe>().ToListAsync());
    }

    [Fact]
    public async Task The_filter_survives_an_explicit_predicate_for_another_tenant()
    {
        // A caller asking for Bob's rows by id gets nothing: the filter is ANDed with
        // whatever the query already says, not replaced by it.
        using var context = ContextFor(Alice);

        var notes = await context.Set<FilterProbe>()
            .Where(p => p.TenantId == Bob)
            .ToListAsync();

        Assert.Empty(notes);
    }

    [Fact]
    public async Task Two_contexts_in_the_same_process_do_not_share_a_tenant()
    {
        // EF caches the compiled model per context type, so a filter that captured the
        // tenant value rather than reading it through the context would freeze the
        // first one seen and serve it to every request afterwards. Reading through the
        // context is what makes this pass.
        using var alice = ContextFor(Alice);
        using var bob = ContextFor(Bob);

        Assert.Equal(["alice note"], await alice.Set<FilterProbe>().Select(p => p.Note).ToListAsync());
        Assert.Equal(["bob note"], await bob.Set<FilterProbe>().Select(p => p.Note).ToListAsync());
    }

    private FilterProbeContext ContextFor(Guid? tenantId)
    {
        var tenantContext = new TenantContext();

        if (tenantId is { } id)
        {
            tenantContext.SetTenant(id);
        }

        // UseDbr without AddInterceptors: no SET ROLE, so this connects as the
        // container's owning superuser and the policies do not apply.
        return new FilterProbeContext(
            new DbContextOptionsBuilder<FilterProbeContext>()
                .UseDbr(postgres.ConnectionString)
                .Options,
            tenantContext,
            _table);
    }

    /// <summary>
    /// The real <see cref="DbrDbContext"/> with one extra entity mapped, so the
    /// filters under test are the ones production applies rather than a copy.
    /// </summary>
    private sealed class FilterProbeContext(
        DbContextOptions options,
        ITenantContext tenantContext,
        string tableName) : DbrDbContext(options, tenantContext)
    {
        public string TableName => tableName;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.ReplaceService<IModelCacheKeyFactory, FilterProbeCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FilterProbe>().ToTable(tableName);

            // After the entity exists, so the convention has something to find.
            base.OnModelCreating(modelBuilder);
        }
    }

    /// <summary>
    /// The table name is a constructor argument, and EF's default cache key is the
    /// context type alone — so without this, two probes would share one model.
    /// </summary>
    private sealed class FilterProbeCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((FilterProbeContext)context).TableName, designTime);
    }

    private sealed class FilterProbe : ITenantScoped
    {
        public Guid Id { get; init; }

        public Guid TenantId { get; init; }

        public string Note { get; init; } = string.Empty;
    }
}
