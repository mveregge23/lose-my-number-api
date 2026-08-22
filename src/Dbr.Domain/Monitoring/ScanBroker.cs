// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// One broker a scan was deliberately narrowed to.
/// </summary>
/// <remarks>
/// <para>
/// A table rather than an array column on <see cref="Scan"/>, because these are
/// references to catalog rows and a foreign key is the only thing that keeps them
/// referring to something. An array of ids would happily hold a broker that was never in
/// the catalog, and the scan would present as finding nothing there.
/// </para>
/// <para>
/// A scan with no rows here is not a scan of no brokers — it is the unnarrowed case, the
/// whole catalog. That reading is stated here and on the column comment in the migration
/// because the opposite one is just as available and differs by whether anything gets
/// searched at all.
/// </para>
/// </remarks>
public class ScanBroker : ITenantScoped
{
    /// <summary>The account the scan belongs to.</summary>
    /// <remarks>
    /// Carried on this row rather than reached through the scan. It is what lets the same
    /// tenant boundary apply here as everywhere else — a join to find out whose row this
    /// is would be a filter the database could not enforce underneath.
    /// </remarks>
    public Guid TenantId { get; init; }

    public required Guid ScanId { get; init; }

    public required Guid BrokerId { get; init; }
}
