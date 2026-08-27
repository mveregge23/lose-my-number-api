// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using Dbr.Infrastructure.Messaging;
using Dbr.Integration.Tests.Fixtures;

namespace Dbr.Integration.Tests;

/// <summary>
/// Where a lane's pace comes from.
/// </summary>
/// <remarks>
/// The point of reading this from the catalog rather than from configuration is that a
/// company known to be twitchy about automated traffic earns a stricter lane than one that
/// has never minded — so the interesting assertions are that the numbers really do come
/// from the row, and that a deactivated entry gets no lane at all.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class BrokerLaneDirectoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private string TwitchyDomain => $"lane-twitchy-{_suffix}.test";

    private string RelaxedDomain => $"lane-relaxed-{_suffix}.test";

    private string DormantDomain => $"lane-dormant-{_suffix}.test";

    public async ValueTask InitializeAsync() =>
        await postgres.ExecuteAsOwnerAsync(
            $"""
             INSERT INTO public.broker
                 (name, domain, removal_method, sla_days, active, max_concurrency, min_delay_ms)
                 VALUES ('Twitchy {_suffix}', '{TwitchyDomain}', 'webform', 45, true, 1, 10000),
                        ('Relaxed {_suffix}', '{RelaxedDomain}', 'api', 30, true, 8, 250),
                        ('Dormant {_suffix}', '{DormantDomain}', 'email', 30, false, 4, 500);
             """);

    public async ValueTask DisposeAsync() =>
        await postgres.ExecuteAsOwnerAsync(
            $"DELETE FROM public.broker WHERE domain LIKE '%{_suffix}.test';");

    [Fact]
    public async Task Each_lane_is_paced_by_its_own_catalog_row()
    {
        var twitchyId = await IdOfAsync(TwitchyDomain);
        var relaxedId = await IdOfAsync(RelaxedDomain);

        var lanes = await new BrokerLaneDirectory(postgres.ConnectionString)
            .ListLanesAsync(TestContext.Current.CancellationToken);

        var twitchy = lanes.Single(lane => lane.BrokerId == twitchyId);
        var relaxed = lanes.Single(lane => lane.BrokerId == relaxedId);

        Assert.Equal(1, twitchy.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(10), twitchy.MinDelay);

        Assert.Equal(8, relaxed.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(250), relaxed.MinDelay);
    }

    [Fact]
    public async Task A_deactivated_broker_gets_no_lane()
    {
        // An entry the operator has stopped dispatching against. A queue standing ready
        // for it would accept work nothing would ever drain.
        var dormant = await IdOfAsync(DormantDomain);

        var lanes = await new BrokerLaneDirectory(postgres.ConnectionString)
            .ListLanesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(lanes, lane => lane.BrokerId == dormant);
    }

    [Fact]
    public async Task A_lanes_queue_name_survives_a_domain_being_corrected()
    {
        // Why the name is the id. A queue named after the domain would be orphaned by this
        // update, with whatever was in it still inside.
        var id = await IdOfAsync(TwitchyDomain);

        var before = await LaneFor(id);

        await postgres.ExecuteAsOwnerAsync(
            $"UPDATE public.broker SET domain = 'corrected-{_suffix}.test' WHERE id = '{id}';");

        var after = await LaneFor(id);

        Assert.Equal(before.QueueName, after.QueueName);
        Assert.Contains(id.ToString(), after.QueueName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BrokerLane> LaneFor(Guid id)
    {
        var lanes = await new BrokerLaneDirectory(postgres.ConnectionString)
            .ListLanesAsync(TestContext.Current.CancellationToken);

        return lanes.Single(lane => lane.BrokerId == id);
    }

    private async Task<Guid> IdOfAsync(string domain) =>
        await postgres.QueryAsOwnerAsync<Guid>(
            $"SELECT id FROM public.broker WHERE domain = '{domain}'");
}
