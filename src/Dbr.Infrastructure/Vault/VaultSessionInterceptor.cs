// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// The session setup for the vault store: the role that reaches identifying data, and
/// nothing else.
/// </summary>
/// <remarks>
/// It has no rights in the core schema, which is what makes "never joined into general
/// query paths" something the database enforces rather than something reviewers have to
/// notice. A query issued through this context cannot bring an account, a scan or a job
/// alongside the encrypted fields, whichever way it is written.
/// </remarks>
public sealed class VaultSessionInterceptor(ITenantContext tenantContext)
    : RoleSessionInterceptor(VaultRole, tenantContext)
{
    /// <summary>
    /// Must match the role name in <c>20260815_0910__vault_role.sql</c>. Reached with
    /// <c>SET ROLE</c> like its counterpart, so the two stores still need one credential
    /// between them for as long as they share a database.
    /// </summary>
    public const string VaultRole = "dbr_vault";

    internal static readonly string SessionSetupSql = SessionSetupSqlFor(VaultRole);
}
