// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Catalog;

/// <summary>
/// The one spelling of each catalog enum: what a column holds, and what a client sends
/// and reads back.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these strings appears in a check constraint in the schema, in the value
/// conversion that reads the column, and in the JSON on the wire. They are the same
/// strings in all three deliberately — a client filtering by a value it was just handed
/// should be sending the value the column is indexed on rather than a translation of it.
/// One list is what keeps those from being three lists that happen to agree.
/// </para>
/// <para>
/// Spelled out rather than derived from the member names. Lower-casing
/// <c>OptOutSale</c> gives <c>optoutsale</c>, which no column accepts and no client
/// sends, and a conversion clever enough to put the underscores back would be one more
/// rule to keep in step with the constraint.
/// </para>
/// <para>
/// Parsing answers <see langword="null"/> for anything unrecognised rather than throwing.
/// An unknown value arrives from a client far more often than from the database, and a
/// caller that needs it to be fatal can say so at its own call site, in terms of its own
/// situation.
/// </para>
/// </remarks>
public static class CatalogVocabulary
{
    public static string ToWire(RemovalMethod method) => method switch
    {
        RemovalMethod.WebForm => "webform",
        RemovalMethod.Email => "email",
        RemovalMethod.Api => "api",
        RemovalMethod.Postal => "postal",
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unmapped removal method. Adding one means a migration widening the check "
            + "constraint on broker.removal_method as well."),
    };

    public static RemovalMethod? ParseRemovalMethod(string? value) => value switch
    {
        "webform" => RemovalMethod.WebForm,
        "email" => RemovalMethod.Email,
        "api" => RemovalMethod.Api,
        "postal" => RemovalMethod.Postal,
        _ => null,
    };

    public static string ToWire(EmailContactMode mode) => mode switch
    {
        EmailContactMode.AliasPreferred => "alias_preferred",
        EmailContactMode.TenantRealRequired => "tenant_real_required",
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            "Unmapped contact mode. Adding one means a migration widening the check "
            + "constraint on broker.email_contact_mode as well."),
    };

    public static EmailContactMode? ParseEmailContactMode(string? value) => value switch
    {
        "alias_preferred" => EmailContactMode.AliasPreferred,
        "tenant_real_required" => EmailContactMode.TenantRealRequired,
        _ => null,
    };

    public static string ToWire(LegalRequestType type) => type switch
    {
        LegalRequestType.Delete => "delete",
        LegalRequestType.OptOutSale => "opt_out_sale",
        LegalRequestType.OptOutTargetedAds => "opt_out_targeted_ads",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "Unmapped request type. Adding one means a migration widening the check "
            + "constraint on legal_basis.request_type as well."),
    };

    public static LegalRequestType? ParseLegalRequestType(string? value) => value switch
    {
        "delete" => LegalRequestType.Delete,
        "opt_out_sale" => LegalRequestType.OptOutSale,
        "opt_out_targeted_ads" => LegalRequestType.OptOutTargetedAds,
        _ => null,
    };

    public static string ToWire(VerificationLevel level) => level switch
    {
        VerificationLevel.None => "none",
        VerificationLevel.Basic => "basic",
        VerificationLevel.Enhanced => "enhanced",
        _ => throw new ArgumentOutOfRangeException(
            nameof(level),
            level,
            "Unmapped verification level. Adding one means a migration widening the check "
            + "constraint on legal_basis.verification_level as well."),
    };

    public static VerificationLevel? ParseVerificationLevel(string? value) => value switch
    {
        "none" => VerificationLevel.None,
        "basic" => VerificationLevel.Basic,
        "enhanced" => VerificationLevel.Enhanced,
        _ => null,
    };
}
