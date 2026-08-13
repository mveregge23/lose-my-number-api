// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Infrastructure.Tenancy;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dbr.Api.Authentication;

/// <summary>
/// Puts the account named by a validated access token into the tenant context, which
/// is where <c>app.tenant_id</c> comes from on every connection the request opens.
/// </summary>
/// <remarks>
/// <para>
/// This is the join between authentication and the tenant boundary, and it is the only
/// place in the request pipeline that decides which account a request is acting for.
/// Everything downstream — the interceptor, the policies, the query filters — takes
/// what this establishes and asks no further questions.
/// </para>
/// <para>
/// It runs only for requests that already carry a valid token. An unauthenticated
/// request leaves the context unset, which is the safe direction: unset reaches the
/// database as no tenant, and the policies then match zero rows.
/// </para>
/// </remarks>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(subject, out var tenantId))
            {
                // The signature checked out, so this service issued it — and this
                // service only ever writes an account id here. Something is wrong that
                // guessing at will not fix, and continuing would run the request as
                // nobody, which reads as an empty account rather than as a failure.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                return;
            }

            tenantContext.SetTenant(tenantId);
        }

        await next(context);
    }
}

public static class TenantContextMiddlewareExtensions
{
    /// <summary>
    /// Adds the tenant context step. Must come after authentication has run, since it
    /// reads what authentication established.
    /// </summary>
    public static IApplicationBuilder UseDbrTenantContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<TenantContextMiddleware>();
    }
}
