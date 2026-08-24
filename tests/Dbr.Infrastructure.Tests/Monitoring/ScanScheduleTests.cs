// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;

namespace Dbr.Infrastructure.Tests.Monitoring;

/// <summary>
/// Which day of the month an account's scans fall on.
/// </summary>
public class ScanScheduleTests
{
    /// <summary>
    /// Accounts whose day is written down here rather than computed by the test.
    /// </summary>
    /// <remarks>
    /// This is the test that matters. Every other property below — determinism within a
    /// run, range, spread — would still hold if the hash were swapped for a
    /// process-randomized one, and the symptom of that swap is an account's scan day
    /// moving on every restart while nothing errors and nothing is logged. Pinning known
    /// ids to known days is what turns that into a build failure. If these change, the
    /// schedule of every existing account changed with them.
    /// </remarks>
    public static TheoryData<string, int> PinnedDays => new()
    {
        { "11111111-2222-3333-4444-555555555555", 2 },
        { "00000000-0000-0000-0000-000000000001", 3 },
        { "deadbeef-dead-beef-dead-beefdeadbeef", 26 },
        { "ffffffff-ffff-ffff-ffff-ffffffffffff", 14 },
    };

    [Theory]
    [MemberData(nameof(PinnedDays))]
    public void A_known_account_lands_on_a_known_day(string id, int expected) =>
        Assert.Equal(expected, ScanSchedule.DayOfMonthFor(Guid.Parse(id)));

    [Fact]
    public void The_same_account_always_gets_the_same_day()
    {
        var id = Guid.NewGuid();
        var first = ScanSchedule.DayOfMonthFor(id);

        Assert.All(
            Enumerable.Range(0, 100),
            _ => Assert.Equal(first, ScanSchedule.DayOfMonthFor(id)));
    }

    [Fact]
    public void Every_account_lands_on_a_day_every_month_has()
    {
        // The reason the spread is 28 and not 31. An account on the 31st would be scanned
        // seven times a year; one on the 29th would be skipped in most Februaries. A
        // monthly rhythm that quietly is not monthly is worse than a smaller spread.
        var days = Enumerable.Range(0, 5000)
            .Select(_ => ScanSchedule.DayOfMonthFor(Guid.NewGuid()))
            .ToList();

        Assert.All(days, day => Assert.InRange(day, 1, 28));
    }

    [Fact]
    public void The_spread_actually_uses_every_day()
    {
        // A hash that compiled and ran but bucketed everybody onto a handful of days
        // would pass every other test here while leaving the thundering herd exactly as it
        // was — just on the 4th instead of the 1st.
        var used = Enumerable.Range(0, 5000)
            .Select(_ => ScanSchedule.DayOfMonthFor(Guid.NewGuid()))
            .ToHashSet();

        Assert.Equal(ScanSchedule.SpreadDays, used.Count);
    }

    [Fact]
    public void No_day_takes_a_disproportionate_share()
    {
        // Uniform enough that the spread is doing its job. With 28 buckets and 28,000
        // draws the expected share is 1,000; anything past double that is a hash
        // collapsing rather than ordinary variance.
        var counts = new int[ScanSchedule.SpreadDays + 1];

        for (var i = 0; i < 28_000; i++)
        {
            counts[ScanSchedule.DayOfMonthFor(Guid.NewGuid())]++;
        }

        Assert.All(counts[1..], count => Assert.InRange(count, 500, 2000));
    }

    [Fact]
    public void Sequential_ids_still_spread()
    {
        // Ids come from gen_random_uuid() and are random throughout, so this is not the
        // situation today. It is the one an operator creates by importing accounts with
        // ids they generated themselves, and it is why the hash reads all sixteen bytes
        // rather than the last four.
        var used = Enumerable.Range(1, 1000)
            .Select(n =>
            {
                Span<byte> bytes = stackalloc byte[16];
                BitConverter.TryWriteBytes(bytes[12..], n);
                return ScanSchedule.DayOfMonthFor(new Guid(bytes));
            })
            .ToHashSet();

        Assert.Equal(ScanSchedule.SpreadDays, used.Count);
    }

    [Fact]
    public void Due_agrees_with_the_day_it_computed()
    {
        var id = Guid.NewGuid();
        var day = ScanSchedule.DayOfMonthFor(id);

        Assert.True(ScanSchedule.IsDue(id, new DateOnly(2026, 9, day)));
        Assert.False(ScanSchedule.IsDue(id, new DateOnly(2026, 9, day == 28 ? 1 : day + 1)));

        // And the same day in a different month is still their day. The rhythm is monthly;
        // nothing about it depends on which month it is.
        Assert.True(ScanSchedule.IsDue(id, new DateOnly(2027, 2, day)));
    }
}
