// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Broker"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// No query filter, and that absence is the interesting part. The convention that
/// applies one does so for entities that carry a tenant; this one does not, so it is
/// left alone and every tenant reads every row. A filter here would empty the catalog
/// for everybody without anything failing.
/// </remarks>
internal sealed class BrokerConfiguration : IEntityTypeConfiguration<Broker>
{
    public void Configure(EntityTypeBuilder<Broker> builder)
    {
        builder.HasKey(broker => broker.Id);

        // Lower-cased on the way in and parsed back on the way out, matching the check
        // constraint on the column. The C# spelling happens to lower-case cleanly for
        // this one, so nothing has to be listed by hand.
        builder.Property(broker => broker.RemovalMethod)
            .HasConversion(
                method => method.ToString().ToLowerInvariant(),
                stored => Enum.Parse<RemovalMethod>(stored, ignoreCase: true));

        // This one cannot use the same trick: lower-casing AliasPreferred gives
        // 'aliaspreferred', and the constraint expects 'alias_preferred'. Spelled out
        // rather than derived, and the constraint rejects the write if the two ever
        // disagree.
        builder.Property(broker => broker.EmailContactMode)
            .HasConversion(
                mode => ToStorage(mode),
                stored => FromStorage(stored));

        // The unique index on domain and the partial index on active are not declared.
        // EF generates no DDL here, so a declaration would only be a claim about the
        // database — and the partial one is a claim EF cannot express correctly anyway.
    }

    private static string ToStorage(EmailContactMode mode) => mode switch
    {
        EmailContactMode.AliasPreferred => "alias_preferred",
        EmailContactMode.TenantRealRequired => "tenant_real_required",
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode),
            mode,
            "Unmapped contact mode. Adding one means a migration widening the check "
            + "constraint on broker.email_contact_mode as well."),
    };

    private static EmailContactMode FromStorage(string stored) => stored switch
    {
        "alias_preferred" => EmailContactMode.AliasPreferred,
        "tenant_real_required" => EmailContactMode.TenantRealRequired,
        _ => throw new InvalidOperationException(
            $"broker.email_contact_mode holds '{stored}', which this build has no value "
            + "for. Either a migration widened the constraint ahead of the code, or a row "
            + "was written by hand."),
    };
}
