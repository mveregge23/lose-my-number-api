// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Infrastructure.Tests.Observability;

/// <summary>
/// What an identity turns into when something asks it for a string.
/// </summary>
/// <remarks>
/// The innermost of the redaction layers and the only one that works everywhere. The
/// logging pipeline can clean up a value that arrives as a log property; nothing can
/// clean up one that was already turned into text before the call — an interpolated
/// string, a concatenation, an exception message. Refusing at the type is what covers
/// those, and it is a generated <c>ToString</c> away from not being covered at all.
/// </remarks>
public class IdentityToStringTests
{
    private static readonly ProfileAddress Address =
        new(Guid.Empty, "12 Rowan Lane", "Flat 4", "Sacramento", "CA", "95814", "US");

    private static readonly ProfileContact Contact =
        new(Guid.Empty, ProfileContactKind.Email, "alex@example.test");

    public static TheoryData<string, string> Interpolations() => new()
    {
        { $"{Address}", "an address" },
        { $"{Contact}", "a contact" },
        { $"{new ProfileDetails(["Alex Whitfield"], new DateOnly(1985, 4, 17), [Contact])}", "details" },
        { $"{new ProfileIdentityFields(["Alex Whitfield"], [Address], [Contact], new DateOnly(1985, 4, 17))}", "fields" },
    };

    [Theory]
    [MemberData(nameof(Interpolations))]
    public void Interpolating_an_identity_yields_none_of_it(string interpolated, string what)
    {
        Assert.DoesNotContain("Rowan Lane", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Sacramento", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("95814", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex Whitfield", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("alex@example.test", interpolated, StringComparison.Ordinal);
        Assert.DoesNotContain("1985", interpolated, StringComparison.Ordinal);

        Assert.True(interpolated.Contains("withheld", StringComparison.Ordinal), what);
    }

    [Fact]
    public void What_is_left_is_still_enough_to_follow_a_bug_with()
    {
        // Withholding everything including the id would make these unusable in the one
        // place somebody reaches for ToString on purpose, and an unusable type gets
        // worked around rather than fixed.
        Assert.Contains(Guid.Empty.ToString(), Address.ToString(), StringComparison.Ordinal);
        Assert.Contains("ProfileAddress", Address.ToString(), StringComparison.Ordinal);

        // The kind is an enum. It says an address exists and nothing about whose, which
        // is the line everything else here is drawn along too.
        Assert.Contains("Email", Contact.ToString(), StringComparison.Ordinal);
    }
}
