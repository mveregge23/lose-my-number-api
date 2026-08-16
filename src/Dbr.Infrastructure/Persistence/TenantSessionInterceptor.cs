// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Tenancy;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// The session setup for the core store: the role that serves ordinary traffic.
/// </summary>
/// <remarks>
/// This role holds no rights in the vault schema, so a query that reaches identifying
/// data through this context is refused by the database rather than answered. That is
/// the point of there being two roles at all.
/// </remarks>
public sealed class TenantSessionInterceptor(ITenantContext tenantContext)
    : RoleSessionInterceptor(ApplicationRole, tenantContext)
{
    /// <summary>
    /// The role the application acts as. Created by the tenant-boundary migration as
    /// NOLOGIN/NOSUPERUSER/NOBYPASSRLS, and reached by <c>SET ROLE</c> rather than by
    /// authenticating, so no second credential has to be provisioned or rotated.
    /// </summary>
    /// <remarks>
    /// Must match the role name in <c>20260812_0010__tenant_boundary.sql</c>. A mismatch
    /// is not a silent failure — <c>SET ROLE</c> to an unknown role throws on the first
    /// connection the process opens.
    /// </remarks>
    public const string ApplicationRole = "dbr_app";

    internal static readonly string SessionSetupSql = SessionSetupSqlFor(ApplicationRole);
}
