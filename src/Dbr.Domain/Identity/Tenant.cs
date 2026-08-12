// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Identity;

/// <summary>
/// An account, and the owner every other tenant-scoped row belongs to.
/// </summary>
/// <remarks>
/// Deliberately thin. Everything identifying a person — names, addresses, dates of
/// birth, contact details — lives envelope-encrypted in the vault, not here. What is
/// left is the operational shell: an address to reach the account at, when it was
/// opened, and whether it is allowed to act.
/// <para>
/// It does not implement <c>ITenantScoped</c>, because it has no <c>TenantId</c> to
/// scope by: the tenant this row belongs to is the one it is. Its query filter is
/// applied over <see cref="Id"/> instead, and its table's row-level security policy
/// is created over the same column.
/// </para>
/// </remarks>
public class Tenant
{
    /// <summary>
    /// The value that ends up in <c>app.tenant_id</c> on every connection acting for
    /// this account, and in the <c>tenant_id</c> of every row it owns.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Where the account is reached. Unique case-insensitively — mail providers treat
    /// addresses that way, so two differing only in capitalisation are one person.
    /// </summary>
    public required string Email { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public bool MfaEnabled { get; set; }

    /// <summary>
    /// Whether the account may act. Checked before authentication and independently of
    /// billing, so it applies in every deployment mode.
    /// </summary>
    public TenantStatus Status { get; set; } = TenantStatus.Active;
}
