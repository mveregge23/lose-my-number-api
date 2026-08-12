// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Tenancy;

namespace Dbr.Infrastructure.Tests.Tenancy;

public class TenantContextTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Starts_with_no_tenant()
    {
        // The safe direction to be incomplete in: nothing populates this until
        // there is a validated JWT to populate it from, and until then queries see
        // nothing rather than everything.
        Assert.Null(new TenantContext().TenantId);
    }

    [Fact]
    public void Remembers_the_tenant_it_was_given()
    {
        var context = new TenantContext();

        context.SetTenant(Alice);

        Assert.Equal(Alice, context.TenantId);
    }

    [Fact]
    public void Setting_the_same_tenant_twice_is_allowed()
    {
        var context = new TenantContext();

        context.SetTenant(Alice);
        context.SetTenant(Alice);

        Assert.Equal(Alice, context.TenantId);
    }

    [Fact]
    public void Refuses_to_switch_tenant_mid_scope()
    {
        // A connection may already be pinned to Alice by the interceptor, so allowing
        // this would produce a unit of work spanning two tenants — the exact thing the
        // boundary exists to make impossible.
        var context = new TenantContext();
        context.SetTenant(Alice);

        var exception = Assert.Throws<InvalidOperationException>(() => context.SetTenant(Bob));

        Assert.Contains(Alice.ToString(), exception.Message);
        Assert.Contains(Bob.ToString(), exception.Message);
    }

    [Fact]
    public void Refuses_the_empty_guid()
    {
        // Guid.Empty is what an unparsed or defaulted claim looks like. Accepting it
        // would create a real-looking tenant that no row can ever belong to, which
        // reads as "isolation is working" while actually meaning "misconfigured".
        Assert.Throws<ArgumentException>(() => new TenantContext().SetTenant(Guid.Empty));
    }
}
