// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using Dbr.Domain.Profiles;
using Dbr.Domain.Search;
using Dbr.Infrastructure.InternalEdge;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// A search that answers whatever a test told it to.
/// </summary>
/// <remarks>
/// The one thing that has to be a stand-in. Everything else in a leg — the run, the grant,
/// the catalog row, the tenant boundary — is real, because those are the parts whose
/// behaviour is worth asserting. What a broker's website says is not something a test can
/// have an opinion about, so the search is where the seam goes.
/// </remarks>
public sealed class StubBrokerSearch(SearchCapabilities capabilities, Func<SearchContext, SearchResult> answer)
    : IBrokerSearch
{
    /// <summary>The context it was last given, so a test can assert what it was handed.</summary>
    public SearchContext? LastContext { get; private set; }

    public SearchCapabilities Capabilities { get; } = capabilities;

    public Task<SearchResult> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        LastContext = context;

        return Task.FromResult(answer(context));
    }

    /// <summary>A search that needs these groups and finds nothing.</summary>
    public static StubBrokerSearch Needing(params IdentityField[] fields) =>
        new(
            new SearchCapabilities(SearchKind.Recipe, fields.ToHashSet()),
            _ => new SearchResult.NothingFound());
}

/// <summary>A registry holding whatever a test put in it.</summary>
public sealed class StubBrokerSearchRegistry : IBrokerSearchRegistry
{
    private readonly Dictionary<Guid, IBrokerSearch> _searches = [];

    public IBrokerSearch? Find(Guid brokerId) =>
        _searches.TryGetValue(brokerId, out var search) ? search : null;

    public StubBrokerSearchRegistry With(Guid brokerId, IBrokerSearch search)
    {
        _searches[brokerId] = search;

        return this;
    }
}

/// <summary>A lane that keeps what it was handed instead of putting it on a queue.</summary>
/// <remarks>
/// Standing up RabbitMQ to assert that a message was addressed to the right company would
/// test the transport rather than the dispatcher. What the dispatcher decides is which work
/// exists and what is on it, which is what this records.
/// </remarks>
public sealed class RecordingWorkDispatcher : IBrokerWorkDispatcher
{
    private readonly List<IBrokerScopedMessage> _sent = [];

    public IReadOnlyList<IBrokerScopedMessage> Sent => _sent;

    public Task DispatchAsync<TWork>(TWork work, CancellationToken cancellationToken)
        where TWork : class, IBrokerScopedMessage
    {
        _sent.Add(work);

        return Task.CompletedTask;
    }
}

/// <summary>
/// The internal edge, without the edge.
/// </summary>
/// <remarks>
/// A handler test is about what a leg does with what came back, not about mutual TLS —
/// which has its own tests. This spends the grant against the real service and hands over
/// what it opened, so the identity a search receives is genuinely the one the vault
/// released for that token.
/// </remarks>
public sealed class DirectReleaseClient(
    Func<string, CancellationToken, Task<ReleaseResponse?>> redeem,
    Func<string, IReadOnlyList<ReportedListingPayload>, CancellationToken, Task<ReportFindingsResponse?>>? report = null)
    : IReleaseClient
{
    public Task<ReleaseResponse?> RedeemAsync(string token, CancellationToken cancellationToken) =>
        redeem(token, cancellationToken);

    public Task<ReportFindingsResponse?> ReportAsync(
        string token,
        IReadOnlyList<ReportedListingPayload> listings,
        CancellationToken cancellationToken) =>
        report is null
            ? throw new InvalidOperationException(
                "This edge was given no way to record findings, and something tried to. A test "
                + "reaching here is one whose search found something it did not expect to.")
            : report(token, listings, cancellationToken);

    /// <summary>An edge that refuses everything, however good the grant.</summary>
    public static DirectReleaseClient Refusing() =>
        new(
            (_, _) => Task.FromResult<ReleaseResponse?>(null),
            (_, _, _) => Task.FromResult<ReportFindingsResponse?>(null));
}
