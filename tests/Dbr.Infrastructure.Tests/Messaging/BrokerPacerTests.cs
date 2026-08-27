// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Dbr.Domain.Messaging;
using Dbr.Infrastructure.Messaging;

namespace Dbr.Infrastructure.Tests.Messaging;

/// <summary>
/// The pacing rule itself, without a bus anywhere near it.
/// </summary>
/// <remarks>
/// Worth having separately from the lane tests. Those prove the rule is wired into the
/// consume pipeline; these prove the rule. Splitting them is what the pacer knowing
/// nothing about queues buys — and it is also what a future transport inherits for free,
/// since the thing being replaced would be the three-line filter rather than this.
/// </remarks>
public class BrokerPacerTests
{
    [Fact]
    public async Task The_first_job_does_not_wait()
    {
        // An idle lane should start immediately. A pacer that made everything wait one
        // delay would add a second per job to a catalog of four hundred.
        using var pacer = new BrokerPacer(new BrokerLane(Guid.NewGuid(), 1, TimeSpan.FromSeconds(5)));

        var clock = Stopwatch.StartNew();
        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(1),
            $"an idle lane made the first job wait {clock.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Consecutive_jobs_are_held_apart()
    {
        var gap = TimeSpan.FromMilliseconds(120);
        using var pacer = new BrokerPacer(new BrokerLane(Guid.NewGuid(), 1, gap));

        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);

        var clock = Stopwatch.StartNew();
        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(90),
            $"the second job started {clock.ElapsedMilliseconds}ms after the first.");
    }

    [Fact]
    public async Task Idling_does_not_bank_credit_to_spend_at_once()
    {
        // The bug that sent the token bucket back. A limiter refilling on a fixed timer
        // lets a lane that has been quiet start two jobs moments apart — measured at 22ms
        // in a lane configured for 150. Waiting well past the gap and then asking twice is
        // exactly that situation.
        var gap = TimeSpan.FromMilliseconds(100);
        using var pacer = new BrokerPacer(new BrokerLane(Guid.NewGuid(), 1, gap));

        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        // Free: the lane has been quiet far longer than the gap.
        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);

        // Not free: the one before it was just now.
        var clock = Stopwatch.StartNew();
        await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(70),
            $"after idling, two jobs started {clock.ElapsedMilliseconds}ms apart — the lane banked "
            + "credit while it was quiet and spent it all at once.");
    }

    [Fact]
    public async Task Callers_are_paced_even_when_they_arrive_together()
    {
        // Concurrency is the endpoint's job, but the gap has to hold regardless of how many
        // are asking: four callers arriving at once must still start one gap apart.
        var gap = TimeSpan.FromMilliseconds(80);
        using var pacer = new BrokerPacer(new BrokerLane(Guid.NewGuid(), 4, gap));

        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            pacer.WaitForTurnAsync(TestContext.Current.CancellationToken)));

        clock.Stop();

        // Three gaps between four starts.
        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(180),
            $"four simultaneous callers all started within {clock.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task A_cancelled_wait_does_not_wedge_the_lane()
    {
        // The gate is held across the delay, so a cancellation that escaped without
        // releasing it would stop the lane permanently — a broker that silently never gets
        // spoken to again.
        var pacer = new BrokerPacer(new BrokerLane(Guid.NewGuid(), 1, TimeSpan.FromSeconds(30)));

        try
        {
            await pacer.WaitForTurnAsync(TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pacer.WaitForTurnAsync(cancelled.Token));

            // The lane still works: a caller that waits gets its turn rather than hanging.
            using var patient = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pacer.WaitForTurnAsync(patient.Token));
        }
        finally
        {
            pacer.Dispose();
        }
    }
}
