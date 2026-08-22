// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dbr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ScanBroker"/> onto the table its migration created.
/// </summary>
/// <remarks>
/// The key is the scan and the broker, not the tenant as well. Two tenants cannot
/// collide on it — a scan id belongs to exactly one of them — so adding the tenant would
/// widen the key without excluding anything, while suggesting that a scan/broker pair
/// could legitimately appear twice.
/// </remarks>
internal sealed class ScanBrokerConfiguration : IEntityTypeConfiguration<ScanBroker>
{
    public void Configure(EntityTypeBuilder<ScanBroker> builder) =>
        builder.HasKey(narrowing => new { narrowing.ScanId, narrowing.BrokerId });
}
