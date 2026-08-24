// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.Endpoints;

namespace Dbr.Api.Tests;

/// <summary>
/// The rule that a scan cannot target anybody but the caller's own identities, asserted
/// as a property of the type rather than of any particular validation.
/// </summary>
/// <remarks>
/// <para>
/// §10.4's point is that a lookup primitive has to be closed off by what the API
/// <i>cannot express</i>. A rule enforced at runtime holds until one path forgets it and
/// fails silently when it does; a shape that has nowhere to put a name cannot forget.
/// </para>
/// <para>
/// So this does not test behaviour. It tests that the request carries identifiers and
/// nothing else, which is the thing a well-meaning future change would break — a "name
/// hint" to improve matching, an "address" to narrow a search, a free-text query box
/// somebody wants for a demo. Each of those is a reasonable-sounding pull request, and
/// each turns a removal tool into a people-search engine. This is what fails them.
/// </para>
/// </remarks>
public class ScanGuardrailTests
{
    /// <summary>
    /// The only shapes a scan request may carry: an optional id, and an optional list of
    /// them.
    /// </summary>
    private static readonly HashSet<Type> IdentifierShapes =
    [
        typeof(Guid),
        typeof(Guid?),
        typeof(IReadOnlyList<Guid>),
    ];

    [Fact]
    public void A_scan_request_carries_identifiers_and_nothing_else()
    {
        var offending = typeof(RequestScanRequest)
            .GetProperties()
            .Where(property => !IdentifierShapes.Contains(property.PropertyType))
            .Select(property => $"{property.Name} is {property.PropertyType.Name}")
            .ToList();

        Assert.True(
            offending.Count == 0,
            "A scan names the identity it searches for and never describes it. These properties are "
            + "not identifiers, which means POST /scans can now be told who to look for: "
            + string.Join("; ", offending));
    }

    [Fact]
    public void No_property_on_a_scan_request_is_free_text()
    {
        // Stated separately from the rule above, and deliberately redundant with it. The
        // general rule is the one that matters, but a string is the specific thing that
        // turns this endpoint into a search box, and a failure naming strings explains
        // itself faster than one naming an allow-list.
        var strings = typeof(RequestScanRequest)
            .GetProperties()
            .Where(property =>
                property.PropertyType == typeof(string)
                || property.PropertyType == typeof(IReadOnlyList<string>)
                || property.PropertyType == typeof(string[]))
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            strings.Count == 0,
            $"Free-text on a scan request: {string.Join(", ", strings)}. Whatever this was meant to "
            + "hold, the identity searched for has to stay a profile the tenant already created.");
    }

    [Fact]
    public void The_allow_list_itself_still_describes_the_request()
    {
        // Guards the guard. Someone tightening IdentifierShapes to an empty set, or
        // widening it to object, would leave the two tests above passing while checking
        // nothing — so this asserts the request really does still have properties, and
        // that the allow-list is the reason they pass rather than a coincidence.
        var properties = typeof(RequestScanRequest).GetProperties();

        Assert.NotEmpty(properties);
        Assert.All(properties, property => Assert.Contains(property.PropertyType, IdentifierShapes));
        Assert.DoesNotContain(typeof(object), IdentifierShapes);
        Assert.DoesNotContain(typeof(string), IdentifierShapes);
    }
}
