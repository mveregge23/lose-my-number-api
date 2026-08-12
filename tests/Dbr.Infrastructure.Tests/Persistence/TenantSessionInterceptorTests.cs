// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;

namespace Dbr.Infrastructure.Tests.Persistence;

/// <summary>
/// The shape of what the interceptor sends. That it takes effect against a real
/// Postgres — the isolation itself — is a database test, and belongs to the harness
/// in DBR-085.
/// </summary>
public class TenantSessionInterceptorTests
{
    [Fact]
    public void No_tenant_is_sent_as_blank_rather_than_omitted()
    {
        // The fail-closed path, and the one worth pinning down. app.current_tenant_id()
        // maps blank to NULL, and NULL makes every policy match zero rows. Sending
        // nothing at all would instead leave whatever the previous user of this pooled
        // connection set — someone else's tenant, and a cross-tenant read that looks
        // exactly like a correct one.
        Assert.Equal(
            string.Empty,
            TenantSessionInterceptor.TenantSettingValue(new TenantContext()));
    }

    [Fact]
    public void A_tenant_is_sent_in_the_form_the_uuid_cast_accepts()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var context = new TenantContext();
        context.SetTenant(tenantId);

        Assert.Equal("11111111-1111-1111-1111-111111111111",
            TenantSessionInterceptor.TenantSettingValue(context));
    }

    [Fact]
    public void The_session_setup_assumes_the_role_that_rls_applies_to()
    {
        // Without this the connection keeps acting as the owning role, which in this
        // stack is a superuser holding BYPASSRLS — policies would exist and isolate
        // nothing. Verified against a real Postgres before the design was settled.
        Assert.Contains(
            $"SET ROLE {TenantSessionInterceptor.ApplicationRole}",
            TenantSessionInterceptor.SessionSetupSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_tenant_travels_as_a_parameter_never_as_text()
    {
        // The role is a compile-time constant because SET ROLE takes an identifier.
        // The tenant is the caller-supplied half, so it must not reach the statement
        // by concatenation.
        Assert.Contains("@tenant", TenantSessionInterceptor.SessionSetupSql, StringComparison.Ordinal);
        Assert.DoesNotContain("' ||", TenantSessionInterceptor.SessionSetupSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_setting_written_is_the_one_the_policies_read()
    {
        Assert.Equal("app.tenant_id", TenantSessionInterceptor.TenantSetting);
        Assert.Contains(
            $"set_config('{TenantSessionInterceptor.TenantSetting}'",
            TenantSessionInterceptor.SessionSetupSql,
            StringComparison.Ordinal);
    }
}
