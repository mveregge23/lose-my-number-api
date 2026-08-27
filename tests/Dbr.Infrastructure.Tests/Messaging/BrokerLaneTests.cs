// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using Dbr.Domain.Messaging;
using Dbr.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.Tests.Messaging;

/// <summary>
/// That a broker's lane actually paces it.
/// </summary>
/// <remarks>
/// <para>
/// Over an in-memory bus rather than RabbitMQ, which is what the lane configuration being
/// written against <see cref="IBusFactoryConfigurator"/> rather than the RabbitMQ one
/// buys. What is under test here is the arrangement — one endpoint per broker, a
/// concurrency ceiling, and a minimum gap between starts — and none of that is a property
/// of the transport. Whether RabbitMQ then declares the queues is checked against a real
/// stack instead.
/// </para>
/// <para>
/// These assert timing, so the margins are deliberately loose: the claim is "at least this
/// far apart", never "exactly". A test that pinned the gap precisely would fail on a busy
/// machine and teach everyone to rerun it, which is worse than not having it.
/// </para>
/// </remarks>
public class BrokerLaneTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void A_lane_is_named_by_id_rather_than_anything_that_can_be_corrected()
    {
        // A queue named after a domain would be orphaned by a catalog correction, with
        // whatever was in it still inside.
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var lane = new BrokerLane(id, 1, Gap);

        Assert.Equal("broker-11111111-2222-3333-4444-555555555555", lane.QueueName);
    }

    [Fact]
    public async Task Work_for_one_broker_is_spaced_by_that_brokers_delay()
    {
        // The steady-state control. However many accounts are queued behind this company,
        // it is spoken to at the pace its catalog row allows.
        var brokerId = Guid.NewGuid();
        var probe = new ArrivalLog();

        await using var provider = BuildBus(probe, new BrokerLane(brokerId, 1, Gap));
        var harness = provider.GetRequiredService<IBusControl>();

        await harness.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var started = Stopwatch.StartNew();
            await SendAsync(provider, brokerId, count: 3);
            await probe.WaitForAsync(3);
            started.Stop();

            // Two gaps between three messages. Asserted well under nominal so an ordinary
            // scheduling hiccup does not fail it.
            Assert.True(
                started.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"three jobs took {started.ElapsedMilliseconds}ms, which is faster than two "
                + $"{Gap.TotalMilliseconds}ms gaps allows.");

            Assert.All(
                probe.Gaps(),
                gap => Assert.True(
                    gap >= TimeSpan.FromMilliseconds(90),
                    $"two jobs started {gap.TotalMilliseconds}ms apart."));
        }
        finally
        {
            await harness.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task One_brokers_pace_does_not_hold_up_another()
    {
        // The reason lanes are per broker rather than one queue. A company having a bad
        // day, or simply configured to be spoken to slowly, must not become everybody
        // else's throughput.
        var slow = Guid.NewGuid();
        var quick = Guid.NewGuid();
        var probe = new ArrivalLog();

        await using var provider = BuildBus(
            probe,
            new BrokerLane(slow, 1, TimeSpan.FromMilliseconds(600)),
            new BrokerLane(quick, 1, TimeSpan.FromMilliseconds(10)));

        var harness = provider.GetRequiredService<IBusControl>();
        await harness.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await SendAsync(provider, slow, count: 3);
            await SendAsync(provider, quick, count: 3);

            // The quick lane's three arrive without waiting on the slow lane's, which at
            // 600ms apart cannot possibly have finished.
            await probe.WaitForAsync(broker: quick, count: 3);

            Assert.True(probe.CountFor(slow) < 3);
        }
        finally
        {
            await harness.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_lane_runs_no_more_at_once_than_its_catalog_row_allows()
    {
        // Concurrency and pacing are different controls and both are read from the row.
        // A lane allowed one at a time that ran two would be sending a company twice what
        // it agreed to regardless of the gap between starts.
        var brokerId = Guid.NewGuid();
        var probe = new ArrivalLog { HoldFor = TimeSpan.FromMilliseconds(120) };

        await using var provider = BuildBus(
            probe,
            new BrokerLane(brokerId, MaxConcurrency: 1, MinDelay: TimeSpan.FromMilliseconds(1)));

        var harness = provider.GetRequiredService<IBusControl>();
        await harness.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await SendAsync(provider, brokerId, count: 4);
            await probe.WaitForAsync(4);

            Assert.Equal(1, probe.PeakConcurrency);
        }
        finally
        {
            await harness.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static ServiceProvider BuildBus(ArrivalLog probe, params BrokerLane[] lanes)
    {
        var registrations = new BrokerLaneRegistrations().Consume<PacingProbeConsumer>();

        return new ServiceCollection()
            .AddSingleton(probe)
            .AddMassTransit(bus =>
            {
                foreach (var register in registrations.Registrations)
                {
                    register(bus);
                }

                bus.UsingInMemory((context, cfg) =>
                    BrokerLaneEndpoints.Configure(cfg, context, lanes, registrations));
            })
            .BuildServiceProvider();
    }

    private static async Task SendAsync(IServiceProvider provider, Guid brokerId, int count)
    {
        var endpoint = await provider
            .GetRequiredService<ISendEndpointProvider>()
            .GetSendEndpoint(new Uri($"queue:broker-{brokerId:D}"));

        for (var i = 0; i < count; i++)
        {
            await endpoint.Send(new PacingProbe(brokerId), TestContext.Current.CancellationToken);
        }
    }

    private sealed record PacingProbe(Guid BrokerId) : IBrokerScopedMessage;

    private sealed class PacingProbeConsumer(ArrivalLog log) : IConsumer<PacingProbe>
    {
        public async Task Consume(ConsumeContext<PacingProbe> context) =>
            await log.RecordAsync(context.Message.BrokerId);
    }

    /// <summary>When each job started, and how many were running at once.</summary>
    private sealed class ArrivalLog
    {
        private readonly ConcurrentQueue<(Guid Broker, TimeSpan At)> _arrivals = new();

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly Lock _gate = new();

        private int _running;

        public TimeSpan HoldFor { get; init; } = TimeSpan.Zero;

        public int PeakConcurrency { get; private set; }

        public async Task RecordAsync(Guid brokerId)
        {
            lock (_gate)
            {
                _running++;
                PeakConcurrency = Math.Max(PeakConcurrency, _running);
            }

            _arrivals.Enqueue((brokerId, _clock.Elapsed));

            if (HoldFor > TimeSpan.Zero)
            {
                await Task.Delay(HoldFor);
            }

            lock (_gate)
            {
                _running--;
            }
        }

        public int CountFor(Guid brokerId) => _arrivals.Count(a => a.Broker == brokerId);

        public IEnumerable<TimeSpan> Gaps()
        {
            var times = _arrivals.Select(a => a.At).OrderBy(at => at).ToList();

            return times.Zip(times.Skip(1), (earlier, later) => later - earlier);
        }

        public async Task WaitForAsync(int count, Guid? broker = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
                var seen = broker is { } id ? CountFor(id) : _arrivals.Count;

                if (seen >= count)
                {
                    return;
                }

                await Task.Delay(15);
            }

            throw new TimeoutException(
                $"Waited 20s for {count} jobs and saw {_arrivals.Count}. Either the lane never "
                + "delivered, or it is pacing far more slowly than its row asks for.");
        }

        public Task WaitForAsync(Guid broker, int count) => WaitForAsync(count, broker);
    }
}
