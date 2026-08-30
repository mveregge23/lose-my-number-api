// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dbr.BrokerFixtures;

/// <summary>What the thing under test actually asked the company for.</summary>
/// <remarks>
/// Recorded because half of what a search gets wrong is in the request rather than in the
/// parse. A recipe that built its query string from the wrong field, or one that went out
/// without the User-Agent this service is supposed to identify itself by, both return a
/// perfectly good page — and a test that only looked at the answer would pass.
/// </remarks>
public sealed record RecordedRequest(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>One header, or nothing when it was not sent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// One recorded company, answering on localhost.
/// </summary>
/// <remarks>
/// <para>
/// §21.4 asks for the real engine to run against <c>localhost</c> exactly as it would
/// against the broker's own domain, and this is the localhost. <b>Everything above the
/// socket is real</b> — a real client, a real request, real status codes and headers — so
/// what a test exercises is the engine and not a stand-in for the half of it that talks to
/// the network. A fake <c>HttpClient</c> would test the parser and skip everything between
/// it and a company.
/// </para>
/// <para>
/// <b>It is put into a scenario rather than routing by path.</b> "This company is
/// rate-limiting today" is a property of the company on that day, not of a URL, and a
/// server that served the throttle from one path and the results from another would be
/// describing something no company does. A scenario that genuinely has two pages — a
/// results list and a listing behind it — says so with a path on each response.
/// </para>
/// <para>
/// The port is taken from the operating system rather than chosen, so two of these can run
/// at once and a developer's own service on a memorable number is never in the way.
/// </para>
/// </remarks>
public sealed class BrokerFixtureServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private readonly List<RecordedRequest> _requests = [];

    private readonly Lock _gate = new();

    private BrokerFixtureServer(WebApplication app, FixtureScenario scenario, Uri baseAddress)
    {
        _app = app;
        Scenario = scenario;
        BaseAddress = baseAddress;
    }

    /// <summary>What this company is doing today.</summary>
    public FixtureScenario Scenario { get; }

    /// <summary>Where it answers. The host to point a recipe at.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Every request it was asked, in the order they arrived.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts a company behaving the way this scenario describes.</summary>
    public static async Task<BrokerFixtureServer> StartAsync(FixtureScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var builder = WebApplication.CreateSlimBuilder();

        // Port 0 asks the operating system for a free one. Two fixtures running at once is
        // the ordinary case — a scan fans out across companies — so a fixed port would make
        // the test suite serial for no reason.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // A recorded company has nothing to say to a log. What is worth reading when one of
        // these tests fails is the request list, which is on the object rather than in a
        // stream somebody has to go and find.
        builder.Logging.ClearProviders();

        var app = builder.Build();

        BrokerFixtureServer? server = null;

        app.Run(context => server!.AnswerAsync(context));

        await app.StartAsync().ConfigureAwait(false);

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            .FirstOrDefault();

        if (address is null)
        {
            await app.DisposeAsync().ConfigureAwait(false);

            throw new InvalidOperationException(
                "The fixture server started without binding an address, so there is nowhere to "
                + "point a recipe.");
        }

        server = new BrokerFixtureServer(app, scenario, new Uri(address));

        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AnswerAsync(HttpContext context)
    {
        var request = context.Request;

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(
                request.Method,
                request.Path.Value ?? "/",
                request.QueryString.Value ?? string.Empty,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase)));
        }

        var response = Scenario.ResponseFor(request.Path.Value ?? "/");

        if (response is null)
        {
            // A plain 404 with a sentence, because the alternative — an empty body — reads
            // as a company that has removed a page rather than as a scenario that never
            // recorded one, and those send whoever is debugging in opposite directions.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";

            await context.Response
                .WriteAsync(
                    $"The '{Scenario.Name}' scenario records no response for {request.Path}.")
                .ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;

        foreach (var (name, value) in response.Headers)
        {
            context.Response.Headers[name] = value;
        }

        await context.Response.WriteAsync(response.Body).ConfigureAwait(false);
    }
}
