// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Profiles;

namespace Dbr.Infrastructure.Tests.Connectors;

/// <summary>
/// What the connector types turn into when something asks them for a string.
/// </summary>
/// <remarks>
/// A connector holds more of somebody than a search does: the identity it was released, the
/// listing it is acting on, and — when it stops — a partly-filled form carrying both. All
/// three have to stay out of a log line, and a record prints every member it has, so each of
/// these is a generated <c>ToString</c> away from not being covered.
/// </remarks>
public class ConnectorDisclosureTests
{
    private static readonly ProfileAddress Address =
        new(Guid.NewGuid(), "12 Rowan Lane", null, "Sacramento", "CA", "95814", "US");

    private static readonly ConnectorContext Context = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new ConnectorTarget(Guid.NewGuid(), "example-broker.test", RemovalMethod.Email),
        new ConnectorDemand(
            LegalRequestType.Delete,
            DeadlineSource.Statutory,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "CCPA",
            new Uri("https://oag.ca.gov/privacy/ccpa")),
        new ProfileIdentityFields(["Alex Whitfield"], [Address], [], new DateOnly(1985, 4, 17)),
        new Uri("https://example-broker.test/profile/alex-whitfield-sacramento-ca-41"),
        Encoding.UTF8.GetBytes("draft: name=Alex Whitfield; city=Sacramento"),
        1);

    [Fact]
    public void Interpolating_a_context_yields_none_of_the_identity_it_carries()
    {
        var interpolated = $"{Context}";

        Assert.DoesNotContain("Alex Whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Rowan Lane", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Sacramento", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("1985", interpolated, StringComparison.Ordinal);
        Assert.Contains("withheld", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A listing's address is the company's copy of somebody's identity, not a pointer to it.
    /// </summary>
    /// <remarks>
    /// The URL in this test is the ordinary shape of a people-search profile link, and it
    /// spells out the name, the city and the age without any of the identity fields being
    /// printed at all. Withholding the identity while printing the reference to it would
    /// leave the log entry just as revealing.
    /// </remarks>
    [Fact]
    public void Interpolating_a_context_does_not_print_the_listing_it_is_acting_on()
    {
        var interpolated = $"{Context}";

        Assert.DoesNotContain("alex-whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("/profile/", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A checkpoint is a partly-filled form, which is the identity in a shape nobody looks at.
    /// </summary>
    [Fact]
    public void Interpolating_a_context_does_not_print_what_a_stopped_attempt_saved()
    {
        var interpolated = $"{Context}";

        Assert.DoesNotContain("draft:", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The context is otherwise the useful thing to print, and stays that way.
    /// </summary>
    /// <remarks>
    /// A type that withholds everything gets worked around rather than used. What is left
    /// here — the attempt, the company, what is being demanded and by when — is what
    /// somebody following a failure through a log actually needs, and none of it is about a
    /// person.
    /// </remarks>
    [Fact]
    public void What_is_left_of_a_context_is_still_worth_logging()
    {
        var interpolated = $"{Context}";

        Assert.Contains(Context.JobId.ToString(), interpolated, StringComparison.Ordinal);
        Assert.Contains(Context.RemovalRequestId.ToString(), interpolated, StringComparison.Ordinal);
        Assert.Contains("example-broker.test", interpolated, StringComparison.Ordinal);
        Assert.Contains("Delete", interpolated, StringComparison.Ordinal);
        Assert.Contains("CCPA", interpolated, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count of what was released survives, which is the thing worth logging about it.
    /// </summary>
    /// <remarks>
    /// The identity withholds its own contents rather than hiding that it is there. A
    /// context that printed nothing at all about what it was given would make "this
    /// connector ran with no address on file" and "this connector was handed an address it
    /// never used" the same log line.
    /// </remarks>
    [Fact]
    public void What_is_left_still_says_how_much_of_an_identity_was_handed_over()
    {
        var interpolated = $"{Context}";

        Assert.Contains("Names = 1", interpolated, StringComparison.Ordinal);
        Assert.Contains("Addresses = 1", interpolated, StringComparison.Ordinal);
    }
}
