// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Api.Endpoints;

/// <summary>
/// What <c>POST /api/v1/scans</c> takes — and, more to the point, all it can take.
/// </summary>
/// <remarks>
/// <para>
/// Two optional ids and nothing else. There is no field here for a name, an address or a
/// date of birth, and that absence is the feature: a scan is structurally "ask a broker
/// what it knows about identity X", and anything that let X be free text would turn a
/// removal tool into a people-search engine. §10.4 makes the point that this has to be
/// closed off by what the API cannot express rather than by a rule enforced at runtime,
/// and this record is where that is true or not.
/// </para>
/// <para>
/// Both fields are optional and mean different things when omitted. No
/// <c>profileId</c> is the tenant's own identity, which is the common case and should not
/// need saying. No <c>brokerIds</c> is the whole catalog, not an empty scan.
/// </para>
/// </remarks>
public sealed record RequestScanRequest(Guid? ProfileId, IReadOnlyList<Guid>? BrokerIds);

/// <summary>Checks what arrives on the scan routes.</summary>
/// <remarks>
/// There is very little to check, which is the intended outcome of the shape above: the
/// ids either name something of the tenant's or they do not, and only the database can
/// answer that. What is left is the one bound this layer can enforce on its own.
/// </remarks>
public static class ScanRequestValidation
{
    /// <summary>
    /// The most brokers one scan may be narrowed to.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a considered figure. A request naming more brokers than the
    /// catalog could plausibly hold is a malformed client or someone probing, and either
    /// way it should be refused before it becomes a query with that many parameters in
    /// it. Narrowing to more than this is also indistinguishable from not narrowing at
    /// all, which the request already expresses by leaving the list out.
    /// </remarks>
    public const int MaxBrokerIds = 1000;

    /// <summary>The problem with this request, or <see langword="null"/> if it is fine.</summary>
    public static string? Validate(RequestScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var brokerIds = request.BrokerIds ?? [];

        if (brokerIds.Count > MaxBrokerIds)
        {
            return $"A scan can be narrowed to at most {MaxBrokerIds} brokers. Leave the list "
                + "out to scan the whole catalog.";
        }

        if (brokerIds.Contains(Guid.Empty))
        {
            // An all-zero id is never a broker, and it is what a client sends when it
            // meant to send nothing. Saying so beats reporting it as an unknown broker.
            return "A broker id cannot be empty. Leave the list out to scan the whole catalog.";
        }

        if (request.ProfileId == Guid.Empty)
        {
            return "A profile id cannot be empty. Leave it out to scan for your own identity.";
        }

        return null;
    }
}
