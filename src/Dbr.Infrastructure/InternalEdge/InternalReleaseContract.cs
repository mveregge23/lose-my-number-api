// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.InternalEdge;

/// <summary>
/// What a worker presents to spend a grant.
/// </summary>
/// <remarks>
/// One field, and it is a bearer credential for part of somebody's identity — so this
/// withholds it from <see cref="ToString"/> like the identity types do. A record prints
/// every member it has, which puts a token one interpolation away from a log line.
/// </remarks>
public sealed record ReleaseRequest(string Token)
{
    public override string ToString() => "ReleaseRequest { [withheld] }";
}

/// <summary>One address, as it crosses the internal edge.</summary>
public sealed record ReleasedAddress(
    Guid Id,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string? PostalCode,
    string Country)
{
    /// <inheritdoc cref="ReleaseResponse.ToString"/>
    public override string ToString() => $"ReleasedAddress {{ Id = {Id}, [withheld] }}";
}

/// <summary>One contact point, as it crosses the internal edge.</summary>
/// <param name="Kind">
/// <c>email</c> or <c>phone</c>, spelled the way the public API already spells it — one
/// vocabulary for the same fact, rather than a second one that has to be kept in step.
/// </param>
public sealed record ReleasedContact(Guid Id, string Kind, string Value)
{
    /// <inheritdoc cref="ReleaseResponse.ToString"/>
    public override string ToString() => $"ReleasedContact {{ Id = {Id}, Kind = {Kind}, [withheld] }}";
}

/// <summary>
/// A spent grant, and the part of an identity it opened.
/// </summary>
/// <remarks>
/// <para>
/// The most sensitive thing this system puts on a wire, and it goes over one connection
/// only: a listener the public edge does not share, to a caller that proved which machine
/// it is before the request line was read.
/// </para>
/// <para>
/// <see cref="Fields"/> is what the grant covered, echoed back rather than inferred. A
/// group that comes back empty is either one the grant did not name or one the profile has
/// nothing in, and those are the same answer for matching purposes — but a worker deciding
/// what it can attempt is entitled to know which fields it was actually given.
/// </para>
/// </remarks>
public sealed record ReleaseResponse(
    Guid ScanId,
    Guid BrokerId,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> Names,
    IReadOnlyList<ReleasedAddress> Addresses,
    IReadOnlyList<ReleasedContact> Contacts,
    DateOnly? DateOfBirth)
{
    /// <summary>
    /// Names the type, counts what it holds, and prints none of it.
    /// </summary>
    /// <remarks>
    /// The same refusal the vault-side identity types carry. This one matters more than
    /// most: it is handled by the process that also runs third-party page scripts, which
    /// is the last place a name should end up in a log line.
    /// </remarks>
    public override string ToString() =>
        $"ReleaseResponse {{ ScanId = {ScanId}, BrokerId = {BrokerId}, "
        + $"Fields = {Fields.Count}, Names = {Names.Count}, Addresses = {Addresses.Count}, "
        + $"Contacts = {Contacts.Count}, [withheld] }}";
}

/// <summary>One listing a leg is asking to have recorded.</summary>
/// <remarks>
/// The candidate a search produced, unscored. Which of them is worth showing anybody is
/// decided on the far side of this edge, by the process that keeps the data — a worker
/// applying the floor would be a worker that could choose not to.
/// </remarks>
/// <param name="Matches">
/// Which groups of the identity the listing agreed with, and how closely, spelled the way the
/// domain spells them so one vocabulary crosses the edge rather than two.
/// </param>
public sealed record ReportedListingPayload(string SourceRef, IReadOnlyList<MatchPayload> Matches)
{
    /// <summary>
    /// Names the type and withholds the address.
    /// </summary>
    /// <remarks>
    /// A broker's profile URL routinely carries the name and the city of the person it is
    /// about, so this is identifying data wearing the shape of a pointer — held to the same
    /// rule as the identity types, and for the same reason: a record prints every member it
    /// has, and this one is handled by the process that also runs third-party page scripts.
    /// </remarks>
    public override string ToString() =>
        $"ReportedListingPayload {{ Matches = {Matches.Count}, [withheld] }}";
}

/// <param name="Field">One of <c>names</c>, <c>addresses</c>, <c>contacts</c>, <c>date_of_birth</c>.</param>
/// <param name="Strength">One of <c>exact</c>, <c>partial</c>, <c>conflicting</c>.</param>
public sealed record MatchPayload(string Field, string Strength);

/// <summary>What a worker presents to have a leg's findings recorded.</summary>
/// <remarks>
/// The same grant that opened the identity, spent a second time on its other permission. One
/// token and one list, and the token is withheld from <see cref="ToString"/> for the reason
/// every credential here is.
/// </remarks>
public sealed record ReportFindingsRequest(string Token, IReadOnlyList<ReportedListingPayload> Listings)
{
    public override string ToString() =>
        $"ReportFindingsRequest {{ Listings = {Listings.Count}, [withheld] }}";
}

/// <param name="Recorded">Listings that cleared the bar and became findings.</param>
/// <param name="BelowFloor">
/// Listings that did not, as a count. What fell below is not written anywhere, so this is the
/// only thing that survives it — and it is what tells an operator a bar is set wrong.
/// </param>
public sealed record ReportFindingsResponse(int Recorded, int BelowFloor);
