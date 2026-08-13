// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Claims;
using Dbr.Api.Authentication;
using Dbr.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dbr.Api.Tests;

/// <summary>
/// The join between a validated token and the tenant boundary.
/// </summary>
/// <remarks>
/// Everything downstream trusts what this establishes without asking again — the
/// interceptor writes it onto the connection, the policies compare against it, the
/// query filters mirror it. It is the only place in the pipeline that decides which
/// account a request acts for, which is why it is worth testing on its own rather than
/// only through something that happens to use it.
/// </remarks>
public class TenantContextMiddlewareTests
{
    [Fact]
    public async Task A_validated_token_establishes_the_account_it_names()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();

        var context = ContextFor(Authenticated(tenantId.ToString()));

        await InvokeAsync(context, tenantContext, out var reachedTheEndpoint);

        Assert.True(reachedTheEndpoint());
        Assert.Equal(tenantId, tenantContext.TenantId);
    }

    [Fact]
    public async Task An_unauthenticated_request_acts_for_nobody()
    {
        // Left unset rather than defaulted to anything. Unset reaches the database as
        // no tenant, and the policies then match zero rows — so a route that forgot to
        // require a token returns nothing instead of everything.
        var tenantContext = new TenantContext();

        var context = ContextFor(new ClaimsPrincipal(new ClaimsIdentity()));

        await InvokeAsync(context, tenantContext, out var reachedTheEndpoint);

        Assert.True(reachedTheEndpoint());
        Assert.Null(tenantContext.TenantId);
    }

    [Fact]
    public async Task A_token_whose_subject_is_not_an_account_is_refused()
    {
        // The signature checked out, so this service issued it, and this service only
        // ever writes an account id there. Carrying on would run the request as
        // nobody — which reads downstream as an account with no rows rather than as
        // something being wrong.
        var tenantContext = new TenantContext();

        var context = ContextFor(Authenticated("not-a-guid"));

        await InvokeAsync(context, tenantContext, out var reachedTheEndpoint);

        Assert.False(reachedTheEndpoint());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Null(tenantContext.TenantId);
    }

    private static ClaimsPrincipal Authenticated(string subject) =>
        new(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, subject)], "TestScheme"));

    private static DefaultHttpContext ContextFor(ClaimsPrincipal user) => new() { User = user };

    private static Task InvokeAsync(
        HttpContext context,
        TenantContext tenantContext,
        out Func<bool> reachedTheEndpoint)
    {
        var reached = false;
        reachedTheEndpoint = () => reached;

        var middleware = new TenantContextMiddleware(_ =>
        {
            reached = true;

            return Task.CompletedTask;
        });

        return middleware.InvokeAsync(context, tenantContext);
    }
}
