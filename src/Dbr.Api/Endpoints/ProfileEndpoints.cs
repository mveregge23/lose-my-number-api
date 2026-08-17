// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Api.Endpoints;

/// <summary>
/// The tenant's own identity: the fields a scan searches for and a removal request
/// quotes.
/// </summary>
/// <remarks>
/// <para>
/// <c>/profile</c> is singular and names no id. It is the account's own profile — the
/// <c>self</c> one — and there is exactly one, so a route that took an id would be
/// offering a choice that does not exist and inviting a request for somebody else's.
/// Managing a second identity, for a dependent or an estate, is a collection a level up
/// in friction and is not this.
/// </para>
/// <para>
/// Everything here is decrypted on the way out and encrypted on the way in, by the one
/// service holding a vault connection. These endpoints never see a wrapped key, never
/// touch a vault context, and could not join this data onto anything operational if they
/// tried — the connection behind them is not allowed into that schema.
/// </para>
/// <para>
/// <b>Addresses are a sub-resource, not part of the replace.</b> They are the field a
/// removal most often turns on, and they accumulate over a lifetime rather than being
/// restated: an old address is frequently the only reason a broker listing can be found
/// at all. Editing them one at a time is what stops a client that forgot to send one
/// from erasing it.
/// </para>
/// </remarks>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var profile = endpoints.MapGroup("/api/v1/profile").RequireAuthorization();

        profile.MapGet("/", GetAsync);
        profile.MapPut("/", ReplaceAsync);
        profile.MapPost("/addresses", AddAddressAsync);
        profile.MapDelete("/addresses/{addressId:guid}", RemoveAddressAsync);

        return endpoints;
    }

    /// <summary>
    /// The account's own identity, decrypted for the one account entitled to it.
    /// </summary>
    private static async Task<IResult> GetAsync(
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        var profile = await profiles.FindSelfAsync(cancellationToken);

        if (profile is null)
        {
            return NoProfile();
        }

        var fields = await profiles.ReadIdentityAsync(profile.Id, cancellationToken);

        if (fields is null)
        {
            // The profile row exists and its fields do not. Creating one writes the
            // encrypted half first precisely so this cannot happen that way round, which
            // leaves something having removed it — worth an alarming answer rather than
            // an empty profile that looks like a new account.
            return Results.Problem(
                "This profile's fields are missing. Nothing was changed; contact whoever operates "
                + "this instance.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(Response(profile, fields));
    }

    /// <summary>
    /// Replaces the names, date of birth and contact details, and sets the residency
    /// region.
    /// </summary>
    /// <remarks>
    /// Addresses are untouched: they have their own routes. A <c>PUT</c> that dropped
    /// them would make every partial client update an erasure.
    /// </remarks>
    private static async Task<IResult> ReplaceAsync(
        ReplaceProfileRequest request,
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        if (ProfileRequestValidation.Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var profile = await profiles.FindSelfAsync(cancellationToken);

        if (profile is null)
        {
            return NoProfile();
        }

        try
        {
            var replaced = await profiles.ReplaceDetailsAsync(
                profile.Id,
                ProfileRequestValidation.ToDetails(request),
                ProfileRequestValidation.ToResidencyRegion(request),
                cancellationToken);

            return replaced ? Results.NoContent() : NoProfile();
        }
        catch (ProfileChangedException)
        {
            return Conflict();
        }
    }

    /// <summary>Adds an address, current or historical.</summary>
    private static async Task<IResult> AddAddressAsync(
        AddAddressRequest request,
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        if (ProfileRequestValidation.Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var profile = await profiles.FindSelfAsync(cancellationToken);

        if (profile is null)
        {
            return NoProfile();
        }

        AddAddressResult result;

        try
        {
            result = await profiles.AddAddressAsync(
                profile.Id,
                ProfileRequestValidation.ToAddress(request),
                cancellationToken);
        }
        catch (ProfileChangedException)
        {
            return Conflict();
        }

        return result.Outcome switch
        {
            AddAddressOutcome.Added => Results.Created(
                $"/api/v1/profile/addresses/{result.Address!.Id}",
                Address(result.Address)),

            AddAddressOutcome.ProfileNotFound => NoProfile(),

            AddAddressOutcome.TooMany => Results.Problem(
                $"This profile already holds {ProfileLimits.MaxAddresses} addresses. Remove one "
                + "that is no longer worth searching for.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled address outcome {result.Outcome}."),
        };
    }

    /// <summary>Removes an address by id.</summary>
    /// <remarks>
    /// An address that is not there answers the same as a profile that is not there.
    /// These ids exist only inside one profile's encrypted fields, so there is nothing an
    /// id from somewhere else could refer to and nothing to be learned by asking.
    /// </remarks>
    private static async Task<IResult> RemoveAddressAsync(
        Guid addressId,
        IProfileService profiles,
        CancellationToken cancellationToken)
    {
        var profile = await profiles.FindSelfAsync(cancellationToken);

        if (profile is null)
        {
            return NoProfile();
        }

        try
        {
            return await profiles.RemoveAddressAsync(profile.Id, addressId, cancellationToken)
                ? Results.NoContent()
                : Results.Problem(
                    "No such address on this profile.",
                    statusCode: StatusCodes.Status404NotFound);
        }
        catch (ProfileChangedException)
        {
            return Conflict();
        }
    }

    /// <remarks>
    /// Signup creates the profile, so a current account always has one and this is
    /// unreachable for anything opened since. It stays because accounts opened before
    /// that did not get one, and because answering 404 is better than an empty profile
    /// that reads as new — the difference decides whether a client offers to create or
    /// to edit.
    /// </remarks>
    private static IResult NoProfile() =>
        Results.Problem(
            "This account has no profile yet.",
            statusCode: StatusCodes.Status404NotFound);

    /// <remarks>
    /// A profile's fields are encrypted as a whole and rewritten on every change, so two
    /// overlapping edits cannot be merged — the second would silently undo the first.
    /// The client re-reads and reapplies, which is a small cost next to an address
    /// disappearing without anything saying so.
    /// </remarks>
    private static IResult Conflict() =>
        Results.Problem(
            "This profile changed while you were editing it. Fetch it again and reapply the change.",
            statusCode: StatusCodes.Status409Conflict);

    private static object Response(PrivacyProfile profile, ProfileIdentityFields fields) =>
        new
        {
            id = profile.Id,
            relationshipType = profile.RelationshipType.ToString().ToLowerInvariant(),
            residencyRegion = profile.ResidencyRegion,
            attestedAt = profile.AttestedAt,
            attestationVersion = profile.AttestationVersion,
            names = fields.Names,
            dateOfBirth = fields.DateOfBirth,
            contacts = fields.Contacts.Select(contact => new
            {
                id = contact.Id,
                kind = contact.Kind.ToString().ToLowerInvariant(),
                value = contact.Value,
            }),
            addresses = fields.Addresses.Select(Address),
        };

    private static object Address(ProfileAddress address) =>
        new
        {
            id = address.Id,
            line1 = address.Line1,
            line2 = address.Line2,
            city = address.City,
            region = address.Region,
            postalCode = address.PostalCode,
            country = address.Country,
        };
}
