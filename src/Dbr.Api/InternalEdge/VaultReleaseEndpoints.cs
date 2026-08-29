// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;
using Dbr.Domain.Vault;
using Dbr.Infrastructure.InternalEdge;

namespace Dbr.Api.InternalEdge;

/// <summary>
/// Spending a grant, from the one process that holds no keys.
/// </summary>
/// <remarks>
/// <para>
/// The only route on the internal listener today, and the reason the listener exists. A
/// worker that needs part of somebody's identity presents the grant it was minted, and gets
/// back exactly the groups that grant covered.
/// </para>
/// <para>
/// <b>Nothing here requires authorization, and that is not an omission.</b> Who may open a
/// connection at all was settled during the TLS handshake, against a certificate this
/// deployment issued; what this particular call may open is carried by the token in the
/// body, which names one leg of one scan and is spent by being used. A bearer token would
/// be a third credential answering neither question.
/// </para>
/// </remarks>
public static class VaultReleaseEndpoints
{
    public static IEndpointRouteBuilder MapVaultReleaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/internal/v1/vault/release", RedeemAsync);

        return endpoints;
    }

    /// <summary>Spends a grant and answers with what it covered.</summary>
    /// <remarks>
    /// Every refusal is one status with one sentence. A token that was never minted, one
    /// that expired and one already spent are different facts, and telling them apart for
    /// whoever presented it would turn a grant into an oracle about other grants — so the
    /// difference goes to the log and not to the caller.
    /// </remarks>
    private static async Task<IResult> RedeemAsync(
        ReleaseRequest request,
        IIdentityReleaseService releases,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return Results.Problem(
                "A release request carries the grant to spend. Without one there is nothing to "
                + "look up.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await releases.RedeemAsync(request.Token, cancellationToken);

        if (result.Outcome is not RedeemReleaseOutcome.Granted)
        {
            // 403 rather than 404: whether a grant exists is itself an answer, and this
            // route gives the same one either way.
            return Results.Problem(
                "This grant cannot be spent. It was never issued, its window has closed, or it "
                + "has already been used — mint another for the work that needs one.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(Released(result.Release!));
    }

    private static ReleaseResponse Released(RedeemedRelease release) =>
        new(
            release.ScanId,
            release.BrokerId,
            [.. release.Fields.Select(IdentityVocabulary.ToWire)],
            release.Identity.Names,
            [.. release.Identity.Addresses.Select(Address)],
            [.. release.Identity.Contacts.Select(Contact)],
            release.Identity.DateOfBirth);

    private static ReleasedAddress Address(ProfileAddress address) =>
        new(
            address.Id,
            address.Line1,
            address.Line2,
            address.City,
            address.Region,
            address.PostalCode,
            address.Country);

    // Lower-cased, which is how the public API already spells a contact's kind. One
    // spelling for one fact: a worker filling a broker's form and a client rendering a
    // profile should not be reading two different vocabularies for the same field.
    private static ReleasedContact Contact(ProfileContact contact) =>
        new(contact.Id, contact.Kind.ToString().ToLowerInvariant(), contact.Value);
}
