// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Tenancy;
using Dbr.Migrator;
using DbUp.Engine.Output;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// A real Postgres, migrated by the real migrator, shared across a test collection.
/// </summary>
/// <remarks>
/// <para>
/// The tenant boundary is a property of what Postgres does when a policy is present
/// and a session variable is not — so there is nothing here a fake provider could
/// stand in for. An in-memory <c>DbContext</c> provider has no policies, no roles and
/// no <c>current_setting</c>; a test against one would pass whether the boundary
/// existed or not.
/// </para>
/// <para>
/// The same reasoning applies to the schema: these run <see cref="MigrationRunner"/>
/// over the same embedded scripts that ship in the migrator image, rather than a
/// test-only <c>CREATE TABLE</c>. A second definition of the schema can agree with
/// the tests while disagreeing with what production applies.
/// </para>
/// <para>
/// The image is pinned to the same tag as the compose stack. A test passing against a
/// different major version than the one that will run it is a test that answers a
/// question nobody asked.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Long enough to satisfy the minimum the token settings enforce, and obviously
    /// not a key anybody should copy.
    /// </summary>
    public const string TestSigningKey = "test-signing-key-not-for-any-real-deployment";


    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("dbr")
        .WithUsername("dbr")
        .WithPassword("dbr_test_password")
        .Build();

    /// <summary>Connects as the container's owning superuser — deliberately.</summary>
    /// <remarks>
    /// That is what production does too, and it is the case the boundary has to hold
    /// under: row-level security is skipped for superusers, for BYPASSRLS roles and
    /// for a table's owner, so a test connecting as some lesser role would prove the
    /// boundary works in a configuration nothing actually runs in.
    /// </remarks>
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // MigrationRunner reads connection strings through a delegate rather than
        // touching Environment directly, which is what lets the real runner point at
        // this container without the test mutating process-wide state.
        var log = new CapturingLog();
        var exitCode = new MigrationRunner(
            typeof(MigrationSet).Assembly,
            _ => ConnectionString,
            log).Run(MigrationSet.All);

        if (exitCode != MigrationRunner.ExitSuccess)
        {
            throw new InvalidOperationException(
                $"Migrations failed against the test container (exit {exitCode}). Every test in "
                + $"this collection would fail for the same reason.{Environment.NewLine}{log}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Npgsql pools by connection string. Without this the pool outlives the
        // container and the next run inherits sockets to a port nothing is listening
        // on any more.
        NpgsqlConnection.ClearAllPools();

        await _container.DisposeAsync();
    }

    /// <summary>
    /// A service provider wired exactly as the API's is, pointed at this container.
    /// </summary>
    /// <param name="settings">
    /// Extra configuration, for tests that need something other than the defaults.
    /// </param>
    public ServiceProvider BuildServices(params KeyValuePair<string, string?>[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>(
                    $"ConnectionStrings:{InfrastructureServiceCollectionExtensions.CoreConnectionStringName}",
                    ConnectionString),

                // Both stores live in this one container, as they do in compose. They
                // are told apart by the role each connection assumes, not by the
                // address, which is exactly the arrangement the boundary has to hold
                // under.
                new KeyValuePair<string, string?>(
                    $"ConnectionStrings:{VaultServiceCollectionExtensions.VaultConnectionStringName}",
                    ConnectionString),

                // Token signing has no default and refuses a short key, so every test
                // provider needs one. A fixed value rather than a random one per run:
                // a test that mints a token in one provider and presents it to another
                // is testing something real, and would fail for an uninteresting
                // reason if the two disagreed.
                new KeyValuePair<string, string?>("Tokens:SigningKey", TestSigningKey),
                .. settings,
            ])
            .Build();

        return new ServiceCollection()
            .AddDbrPersistence(configuration)
            .AddDbrPasskeys(configuration)
            .AddDbrSessions(configuration)

            // The catalog and the resolver over it. They need a connection and nothing
            // else — no configuration, no container — so there is no reason for a test
            // provider to differ from the API's here.
            .AddDbrCatalog()
            // The vault context, but not key management: resolving the context needs
            // only a connection, and a provider that demanded a running OpenBao would
            // make every database test pay for a container it does not use. Tests that
            // encrypt build their own provider with both.
            .AddDbrVault(configuration)
            .BuildServiceProvider();
    }

    /// <summary>
    /// Opens a scope acting for <paramref name="tenantId"/>, or for no tenant at all
    /// when it is <see langword="null"/>.
    /// </summary>
    public static IServiceScope ScopeFor(IServiceProvider services, Guid? tenantId)
    {
        var scope = services.CreateScope();

        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(id);
        }

        return scope;
    }

    /// <summary>
    /// Runs SQL as the owning role, bypassing the boundary — for arranging fixtures
    /// and for asking what is really in a table regardless of who may see it.
    /// </summary>
    public async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> QueryAsOwnerAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>
    /// Creates a tenant-scoped table and opts it into the boundary the way a real
    /// table's migration does — through <c>app.enable_tenant_rls</c>, not by
    /// hand-writing a policy the production procedure might no longer match.
    /// </summary>
    public async Task CreateTenantScopedTableAsync(string tableName)
    {
        await ExecuteAsOwnerAsync(
            $"""
             CREATE TABLE public.{tableName} (
                 id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                 tenant_id uuid NOT NULL,
                 note      text NOT NULL
             );
             CALL app.enable_tenant_rls('public.{tableName}');
             """);
    }

    private sealed class CapturingLog : IUpgradeLog
    {
        private readonly List<string> _lines = [];

        public override string ToString() => string.Join(Environment.NewLine, _lines);

        public void LogTrace(string format, params object[] args) => Add(format, args);

        public void LogDebug(string format, params object[] args) => Add(format, args);

        public void LogInformation(string format, params object[] args) => Add(format, args);

        public void LogWarning(string format, params object[] args) => Add(format, args);

        public void LogError(string format, params object[] args) => Add(format, args);

        public void LogError(Exception ex, string format, params object[] args)
        {
            Add(format, args);
            _lines.Add(ex.ToString());
        }

        private void Add(string format, object[] args) =>
            _lines.Add(args.Length == 0 ? format : string.Format(format, args));
    }
}
