// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Api.Endpoints;

/// <summary>
/// The account, as its owner sees it.
/// </summary>
/// <remarks>
/// The first route that requires a token, and the one that demonstrates what a token
/// is for: nothing here names an account. The query asks for the tenant and gets
/// exactly one, because the request is already acting for that account and the
/// database will not return another.
/// </remarks>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/v1/account", GetAccountAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> GetAccountAsync(
        DbrDbContext context,
        CancellationToken cancellationToken)
    {
        // No Where clause, and none is missing. The query filter narrows this to the
        // current tenant, and underneath it the row-level security policy does the
        // same thing again — so the interesting case is not what this returns but that
        // there is no version of this query that returns somebody else's account.
        var account = await context.Set<Tenant>().SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            // The token was valid and named an account that is not there — deleted
            // while the token was still in flight. The token is the stale thing, so
            // the answer is about the token rather than a missing page.
            return Results.Problem("Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            id = account.Id,
            email = account.Email,
            createdAt = account.CreatedAt,
            status = account.Status.ToString().ToLowerInvariant(),
        });
    }
}
