// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using Dbr.Domain.Profiles;

namespace Dbr.Api.Endpoints;

/// <summary>
/// What <c>PUT /api/v1/profile</c> takes: everything about the identity except its
/// addresses.
/// </summary>
/// <remarks>
/// Replace semantics, so an omitted list is an empty one rather than a list left alone —
/// a client sending <c>{}</c> is asking for a profile with no names and no contacts. It
/// is stated here because the alternative reading is just as plausible and the two
/// differ by somebody's data.
/// </remarks>
public sealed record ReplaceProfileRequest(
    IReadOnlyList<string>? Names,
    DateOnly? DateOfBirth,
    IReadOnlyList<ProfileContactRequest>? Contacts,
    string? ResidencyRegion);

/// <param name="Kind"><c>email</c> or <c>phone</c>.</param>
public sealed record ProfileContactRequest(string? Kind, string? Value);

/// <summary>What <c>POST /api/v1/profile/addresses</c> takes.</summary>
/// <remarks>
/// No id: it is assigned where the address is stored. A client that could choose one
/// could collide with an existing one, and nothing in an encrypted column would notice.
/// </remarks>
public sealed record AddAddressRequest(
    string? Line1,
    string? Line2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country);

/// <summary>
/// Checks and normalizes what arrives on the profile routes.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the endpoints, and public, because this is the only layer that can
/// refuse any of it. The columns behind these values are encrypted blobs — there is no
/// <c>varchar(200)</c>, no check constraint and no unique index waiting underneath, so a
/// rule that is not applied here is not applied anywhere.
/// </para>
/// <para>
/// One message per request rather than a field-by-field report, matching the rest of the
/// API. Each message names the specific thing to fix.
/// </para>
/// </remarks>
public static partial class ProfileRequestValidation
{
    /// <summary>The problem with this request, or <see langword="null"/> if it is fine.</summary>
    public static string? Validate(ReplaceProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var names = request.Names ?? [];

        if (names.Count > ProfileLimits.MaxNames)
        {
            return $"A profile holds at most {ProfileLimits.MaxNames} names.";
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "A name cannot be blank. Leave it out instead.";
            }

            if (name.Trim().Length > ProfileLimits.MaxNameLength)
            {
                return $"A name is limited to {ProfileLimits.MaxNameLength} characters.";
            }
        }

        var contacts = request.Contacts ?? [];

        if (contacts.Count > ProfileLimits.MaxContacts)
        {
            return $"A profile holds at most {ProfileLimits.MaxContacts} contact details.";
        }

        foreach (var contact in contacts)
        {
            if (ParseKind(contact.Kind) is null)
            {
                return "A contact's kind must be 'email' or 'phone'.";
            }

            if (string.IsNullOrWhiteSpace(contact.Value))
            {
                return "A contact cannot be blank. Leave it out instead.";
            }

            if (contact.Value.Trim().Length > ProfileLimits.MaxContactValueLength)
            {
                return $"A contact is limited to {ProfileLimits.MaxContactValueLength} characters.";
            }
        }

        if (request.DateOfBirth is { } dateOfBirth)
        {
            if (dateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
            {
                return "A date of birth cannot be in the future.";
            }

            if (dateOfBirth < ProfileLimits.EarliestDateOfBirth)
            {
                return $"A date of birth cannot be before {ProfileLimits.EarliestDateOfBirth:yyyy-MM-dd}.";
            }
        }

        if (NormalizeRegion(request.ResidencyRegion) is { } region && !RegionCode().IsMatch(region))
        {
            // The database has the same rule as a check constraint. Refusing it here is
            // what turns a 500 into an answer that says which field and what shape.
            return "A residency region is a coarse code such as 'US-CA' or 'EU', not an address.";
        }

        return null;
    }

    /// <summary>The problem with this request, or <see langword="null"/> if it is fine.</summary>
    public static string? Validate(AddAddressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Line1))
        {
            return "An address needs a street line.";
        }

        if (string.IsNullOrWhiteSpace(request.City))
        {
            return "An address needs a city.";
        }

        if (NormalizeCountry(request.Country) is not { } country || !CountryCode().IsMatch(country))
        {
            // Two letters, because the catalog resolves which regimes apply from country
            // and region codes rather than from prose. "United States" and "USA" and
            // "us" are the same place and would not compare equal to any of them.
            return "An address needs a two-letter country code, such as 'US'.";
        }

        return TooLong(request.Line1, ProfileLimits.MaxAddressLineLength, "A street line")
            ?? TooLong(request.Line2, ProfileLimits.MaxAddressLineLength, "A second address line")
            ?? TooLong(request.City, ProfileLimits.MaxCityLength, "A city")
            ?? TooLong(request.Region, ProfileLimits.MaxRegionLength, "A state or region")
            ?? TooLong(request.PostalCode, ProfileLimits.MaxPostalCodeLength, "A postal code");
    }

    /// <summary>
    /// The request as the profile service takes it: trimmed, with the ids the storage
    /// layer will keep.
    /// </summary>
    /// <remarks>
    /// Contact ids are minted per write rather than carried across a replace. Nothing
    /// refers to a contact by id — the whole set is replaced at once — so preserving them
    /// would be a promise that is not being kept anywhere.
    /// </remarks>
    public static ProfileDetails ToDetails(ReplaceProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ProfileDetails(
            [.. (request.Names ?? []).Select(name => name.Trim())],
            request.DateOfBirth,
            [
                .. (request.Contacts ?? []).Select(contact => new ProfileContact(
                    Guid.NewGuid(),
                    ParseKind(contact.Kind)!.Value,
                    contact.Value!.Trim())),
            ]);
    }

    /// <summary>The region as stored: upper-cased, or <see langword="null"/> if blank.</summary>
    public static string? ToResidencyRegion(ReplaceProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return NormalizeRegion(request.ResidencyRegion);
    }

    /// <summary>
    /// The request as the profile service takes it. The id is a placeholder — the
    /// service assigns the one that is stored.
    /// </summary>
    public static ProfileAddress ToAddress(AddAddressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ProfileAddress(
            Guid.Empty,
            request.Line1!.Trim(),
            Blank(request.Line2) ? null : request.Line2!.Trim(),
            request.City!.Trim(),
            Blank(request.Region) ? null : request.Region!.Trim(),
            Blank(request.PostalCode) ? null : request.PostalCode!.Trim(),
            NormalizeCountry(request.Country)!);
    }

    private static ProfileContactKind? ParseKind(string? kind) =>
        Enum.TryParse<ProfileContactKind>(kind, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : null;

    private static string? NormalizeRegion(string? region) =>
        Blank(region) ? null : region!.Trim().ToUpperInvariant();

    private static string? NormalizeCountry(string? country) =>
        Blank(country) ? null : country!.Trim().ToUpperInvariant();

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? TooLong(string? value, int limit, string what) =>
        value is not null && value.Trim().Length > limit
            ? $"{what} is limited to {limit} characters."
            : null;

    /// <summary>Matches the check constraint on <c>privacy_profile.residency_region</c>.</summary>
    [GeneratedRegex("^[A-Z]{2}(-[A-Z0-9]{1,3})?$")]
    private static partial Regex RegionCode();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryCode();
}
