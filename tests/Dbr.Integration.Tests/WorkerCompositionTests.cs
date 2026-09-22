// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;
using Dbr.Domain.Search;
using Dbr.Integration.Tests.Fixtures;
using Dbr.Removals;
using Dbr.Search;
using Dbr.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dbr.Integration.Tests;

/// <summary>
/// The container the worker actually runs with, built by the worker's own composition.
/// </summary>
/// <remarks>
/// <para>
/// Every component has tests and every test builds its own container. The one container
/// nobody built was the worker's, and it was wrong: two extension methods registered the
/// connector registry with <c>TryAdd</c>, the empty fallback came first, and every demand on
/// a live stack stayed queued with the worker logging that this build had no connector for
/// a company whose recipe it was shipping. This test would have failed on that day.
/// </para>
/// <para>
/// Configured the way compose configures it — the internal edge on, a relay named, the
/// certificate paths pointing at files that parse — because a composition that validates at
/// startup has to be given something to validate, and a real database because declaring
/// the lanes reads the catalog for the companies to declare them for. Nothing is run; the
/// host is built and asked what it resolved.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class WorkerCompositionTests(PostgresFixture postgres)
{
    private const string Spokeo = "edea346d-25ab-4eab-a9b7-d9b4c6132625";

    [Fact]
    public void The_worker_can_find_a_connector_for_a_company_whose_recipe_it_ships()
    {
        using var host = Build();

        var registry = host.Services.GetRequiredService<IBrokerConnectorRegistry>();

        Assert.IsType<EmailConnectorRegistry>(registry);
        Assert.NotNull(registry.Find(Guid.Parse(Spokeo)));
    }

    [Fact]
    public void The_worker_can_search_a_company_whose_recipe_it_ships()
    {
        using var host = Build();

        var registry = host.Services.GetRequiredService<IBrokerSearchRegistry>();

        Assert.IsType<RecipeSearchRegistry>(registry);
        Assert.NotNull(registry.Find(Guid.Parse(Spokeo)));
    }

    private IHost Build()
    {
        var certificates = TestCertificateFiles.Write();

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // A real database, because declaring the lanes reads the catalog for the
            // companies to declare them for — the composition touches it at build time.
            ["ConnectionStrings:Core"] = postgres.ConnectionString,
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Username"] = "unused",
            ["RabbitMq:Password"] = "unused",
            ["Consent:PolicyVersion"] = "2026-06-01",
            ["InternalApi:Enabled"] = "true",
            ["InternalApi:BaseAddress"] = "https://api:8443",
            ["InternalApi:ClientCertificatePath"] = Path.Combine(certificates, "server.crt"),
            ["InternalApi:ClientKeyPath"] = Path.Combine(certificates, "server.key"),
            ["InternalApi:ServerCertificateAuthorityPath"] = Path.Combine(certificates, "ca.crt"),
            ["Mail:Host"] = "relay.example",
            ["Mail:Port"] = "587",
            ["Mail:Username"] = "unused",
            ["Mail:Password"] = "unused",
            ["Mail:Domain"] = "removals.example",
            ["Mail:RequireTls"] = "true",
        });

        WorkerComposition.Configure(builder);

        return builder.Build();
    }
}
