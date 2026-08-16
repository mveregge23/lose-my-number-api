// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Data.Common;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dbr.Infrastructure.Persistence;

/// <summary>
/// Puts every connection into the tenant boundary as soon as it opens: acting as a role
/// that row-level security applies to, and carrying the current tenant.
/// </summary>
/// <remarks>
/// <para>
/// This is the application half of the boundary; the policies created by
/// <c>app.enable_tenant_rls</c> are the enforcing half. The database is what actually
/// refuses to return another tenant's rows — this only tells it who is asking.
/// </para>
/// <para>
/// <b>Which role is the interesting part.</b> There are two, and they can reach
/// different tables: one serves ordinary traffic and cannot see the vault schema at
/// all, the other sees nothing but the vault schema. A connection therefore carries
/// both who is asking and which half of the data they are entitled to ask about, and
/// the store a context talks to is settled when it is registered rather than per query.
/// </para>
/// <para>
/// <b>Why every open, unconditionally.</b> Connections are pooled, so one may come back
/// carrying whatever the previous user of it set. Rather than depend on the pool's
/// reset behaviour, this overwrites the setting on every open — including writing an
/// empty value when there is no tenant, which is the case that matters. A pooled
/// connection that silently kept the last request's tenant would be a cross-tenant read
/// that looks exactly like a correct one.
/// </para>
/// </remarks>
public abstract class RoleSessionInterceptor(string role, ITenantContext tenantContext)
    : DbConnectionInterceptor
{
    /// <summary>The setting every tenant policy reads.</summary>
    public const string TenantSetting = "app.tenant_id";

    /// <summary>
    /// <c>SET ROLE</c> takes an identifier, which cannot be parameterised — hence a
    /// role that is fixed by the subclass rather than anything caller-supplied. The
    /// tenant, which is caller-supplied, goes through a parameter.
    /// </summary>
    internal static string SessionSetupSqlFor(string role) =>
        $"SET ROLE {role}; SELECT set_config('{TenantSetting}', @tenant, false);";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateSessionSetupCommand(connection);
        command.ExecuteNonQuery();

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateSessionSetupCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The empty string, not NULL, when there is no tenant: <c>app.current_tenant_id()</c>
    /// maps blank to NULL, and NULL is what makes the policies match nothing.
    /// </summary>
    internal static string TenantSettingValue(ITenantContext tenantContext) =>
        tenantContext.TenantId?.ToString() ?? string.Empty;

    private DbCommand CreateSessionSetupCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SessionSetupSqlFor(role);

        var tenant = command.CreateParameter();
        tenant.ParameterName = "tenant";
        tenant.Value = TenantSettingValue(tenantContext);
        command.Parameters.Add(tenant);

        return command;
    }
}
