// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Infrastructure.Tests.Catalog;

/// <summary>
/// Turning a regime's count of days into a date.
/// </summary>
/// <remarks>
/// The dates here are written out rather than computed, because a test that recomputes
/// the thing it is checking passes for the same reason the code does. Monday 1 June 2026
/// is the anchor throughout, which makes every weekend crossing visible in the expected
/// value instead of hidden in arithmetic.
/// </remarks>
public class DeadlineCalculatorTests
{
    private static readonly DateTimeOffset MondayFirstOfJune =
        new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calendar_days_are_every_day()
    {
        // Forty-five calendar days from Monday 1 June is Thursday 16 July, weekends and
        // all — which is the point of the unit.
        var at = DeadlineCalculator.Add(MondayFirstOfJune, 45, DeadlineUnit.Calendar);

        Assert.Equal(new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero), at);
    }

    [Fact]
    public void Fifteen_business_days_is_three_weeks_not_a_fortnight()
    {
        // The case the whole unit column exists for. Fifteen business days from Monday
        // 1 June is Monday 22 June: three weekends crossed, six days longer than the
        // count read as calendar days would give.
        var at = DeadlineCalculator.Add(MondayFirstOfJune, 15, DeadlineUnit.Business);

        Assert.Equal(new DateTimeOffset(2026, 6, 22, 9, 0, 0, TimeSpan.Zero), at);
        Assert.Equal(DayOfWeek.Monday, at.DayOfWeek);

        // And it is emphatically not what the same count means as calendar days, which is
        // the mistake this replaced.
        Assert.NotEqual(DeadlineCalculator.Add(MondayFirstOfJune, 15, DeadlineUnit.Calendar), at);
    }

    [Theory]
    [InlineData(1, "2026-06-02")]
    [InlineData(4, "2026-06-05")]
    [InlineData(5, "2026-06-08")]
    [InlineData(10, "2026-06-15")]
    public void A_business_window_steps_over_the_weekend_it_would_have_closed_on(
        int days,
        string expected)
    {
        // Five business days from a Monday is the following Monday, not the Saturday.
        var at = DeadlineCalculator.Add(MondayFirstOfJune, days, DeadlineUnit.Business);

        Assert.Equal(DateOnly.Parse(expected, null), DateOnly.FromDateTime(at.UtcDateTime));
    }

    [Fact]
    public void A_business_window_never_closes_on_a_weekend()
    {
        // Whatever the count, the answer is a day somebody could actually act on. Worth
        // asserting over a range rather than at one length, since an off-by-one in the
        // loop would leave a Saturday reachable at some counts and not others.
        for (var days = 1; days <= 40; days++)
        {
            var at = DeadlineCalculator.Add(MondayFirstOfJune, days, DeadlineUnit.Business);

            Assert.False(
                at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                $"{days} business days landed on a {at.DayOfWeek}.");
        }
    }

    [Fact]
    public void A_clock_starting_on_a_weekend_still_counts_whole_business_days()
    {
        // A request arriving on a Saturday does not consume a business day for arriving.
        // One business day from Saturday is the following Monday.
        var saturday = new DateTimeOffset(2026, 6, 6, 9, 0, 0, TimeSpan.Zero);

        var at = DeadlineCalculator.Add(saturday, 1, DeadlineUnit.Business);

        Assert.Equal(new DateTimeOffset(2026, 6, 8, 9, 0, 0, TimeSpan.Zero), at);
    }

    [Fact]
    public void The_time_of_day_the_clock_started_is_kept()
    {
        // A deadline is a moment, not a date, and truncating to midnight would quietly
        // move every deadline earlier by up to a day.
        var at = DeadlineCalculator.Add(MondayFirstOfJune, 3, DeadlineUnit.Calendar);

        Assert.Equal(MondayFirstOfJune.TimeOfDay, at.TimeOfDay);
        Assert.Equal(MondayFirstOfJune.Offset, at.Offset);
    }

    [Fact]
    public void Zero_days_closes_the_window_where_it_opened()
    {
        Assert.Equal(
            MondayFirstOfJune,
            DeadlineCalculator.Add(MondayFirstOfJune, 0, DeadlineUnit.Business));
    }

    [Fact]
    public void A_negative_window_is_refused_rather_than_run_backwards()
    {
        // No regime grants negative days; a caller passing one has a bug, and counting
        // backwards would hand it a deadline in the past that looks deliberate.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeadlineCalculator.Add(MondayFirstOfJune, -1, DeadlineUnit.Calendar));
    }
}
