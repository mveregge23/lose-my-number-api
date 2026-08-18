// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Consent;

namespace Dbr.Api.Endpoints;

/// <summary>
/// What <c>POST /api/v1/profile/consent</c> takes: one decision about one permission.
/// </summary>
/// <remarks>
/// One scope per request rather than all three at once. A client that sent the full set
/// on every change would be restating two decisions it was not asked about, and a
/// dropped field would read as a revocation — which is the direction that costs somebody
/// something.
/// </remarks>
/// <param name="Scope"><c>scan</c>, <c>auto_removal</c> or <c>auto_resubmit</c>.</param>
/// <param name="Granted">
/// <see langword="true"/> to permit it, <see langword="false"/> to withdraw. Required:
/// an omitted value would default to withdrawing, and a missing field is not a decision.
/// </param>
/// <param name="PolicyVersion">
/// The version of the consent text the client displayed. Echoed back rather than assumed
/// so that a decision made against wording this instance has replaced is refused instead
/// of recorded under the wrong name.
/// </param>
public sealed record RecordConsentRequest(string? Scope, bool? Granted, string? PolicyVersion);

/// <summary>Checks what arrives on the consent route.</summary>
public static class ConsentRequestValidation
{
    /// <summary>The problem with this request, or <see langword="null"/> if it is fine.</summary>
    public static string? Validate(RecordConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ParseScope(request.Scope) is null)
        {
            return "A consent scope must be 'scan', 'auto_removal' or 'auto_resubmit'.";
        }

        if (request.Granted is null)
        {
            return "A consent decision must say whether it grants or withdraws.";
        }

        return null;
    }

    /// <summary>
    /// The scope named, or <see langword="null"/> if it is not one.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than parsed off the enum member names: the wire spelling is
    /// <c>auto_removal</c> and the member is <c>AutoRemoval</c>, and a case-insensitive
    /// parse would accept neither reliably nor refuse the other.
    /// </remarks>
    public static ConsentScope? ParseScope(string? scope) => scope switch
    {
        "scan" => ConsentScope.Scan,
        "auto_removal" => ConsentScope.AutoRemoval,
        "auto_resubmit" => ConsentScope.AutoResubmit,
        _ => null,
    };

    /// <summary>The scope as a client spells it.</summary>
    public static string ToWire(ConsentScope scope) => scope switch
    {
        ConsentScope.Scan => "scan",
        ConsentScope.AutoRemoval => "auto_removal",
        ConsentScope.AutoResubmit => "auto_resubmit",
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope),
            scope,
            "Unmapped consent scope. A scope the API cannot name is one a client cannot set."),
    };
}
