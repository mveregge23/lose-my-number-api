// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Starting a session, keeping it alive, and ending it.
/// </summary>
/// <remarks>
/// <para>
/// A session is two tokens doing different jobs. The access token is signed and
/// checked without touching the database, which is what makes it cheap on every
/// request and impossible to take back. The refresh token is a random secret whose
/// digest is a row here, which makes it revocable — and it is what every guarantee
/// about ending a session actually rests on.
/// </para>
/// <para>
/// <b>Rotation is not housekeeping.</b> Every exchange spends the presented token and
/// issues a new one, so a refresh token is valid exactly once. That turns a stolen
/// token from a permanent key into a race: whichever party uses it first invalidates
/// it for the other, and the loser's next attempt is a spent token being presented
/// again — which is a signal that this code acts on rather than a curiosity.
/// </para>
/// </remarks>
public sealed class SessionService(
    DbrDbContext context,
    RefreshTokenLookup refreshTokens,
    TenantContext tenantContext,
    SessionTokenOptions options)
{
    /// <summary>
    /// How many random bytes each refresh token carries.
    /// </summary>
    /// <remarks>
    /// 256 bits, so guessing one is not a threat that has to be defended against
    /// anywhere else — which is what lets the digest be stored with a plain hash and
    /// looked up by equality.
    /// </remarks>
    private const int RefreshTokenBytes = 32;

    /// <summary>
    /// Issues the first pair of tokens for an account that has just proved who it is.
    /// </summary>
    /// <remarks>
    /// Called only after a credential has been checked. It establishes the tenant for
    /// this unit of work if signing in has not already — and because the tenant
    /// context is write-once, being handed a different account than the one already
    /// established throws rather than quietly issuing somebody else's session.
    /// </remarks>
    public async Task<IssuedSession> StartAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.SetTenant(tenantId);

        var now = DateTimeOffset.UtcNow;

        return await IssueAsync(
            tenantId,
            sessionId: Guid.NewGuid(),
            sessionStartedAt: now,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, spending the one presented.
    /// </summary>
    public async Task<SessionRefreshResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var stored = await refreshTokens
            .FindAsync(Digest(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return SessionRefreshResult.Failed(SessionRefreshOutcome.Rejected);
        }

        // The token resolved, so the account is known and the ordinary boundary
        // applies from here on.
        tenantContext.SetTenant(stored.TenantId);

        if (stored.UsedAt is not null)
        {
            // Two parties hold this token: the client that spent it and whoever else
            // is presenting it now. There is no way to tell which one is asking, and
            // one of them is not entitled to the session, so neither keeps it.
            await RevokeSessionAsync(stored.SessionId, cancellationToken).ConfigureAwait(false);

            return SessionRefreshResult.Failed(SessionRefreshOutcome.ReusedAndRevoked);
        }

        var now = DateTimeOffset.UtcNow;

        if (stored.RevokedAt is not null
            || stored.ExpiresAt <= now
            || stored.SessionStartedAt + options.SessionLifetime <= now)
        {
            return SessionRefreshResult.Failed(SessionRefreshOutcome.Rejected);
        }

        IssuedSession? issued = null;

        // Spending the old token and issuing its successor are one transaction. Apart,
        // a failure between them leaves a session whose only refresh token is spent —
        // signed out by an error rather than by anyone's decision.
        await using (var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Spending is a condition of the update rather than a decision made above
            // it. Two requests carrying the same token would both pass the checks
            // above — that is exactly the race rotation exists to resolve — so the
            // database decides which one wins.
            if (await TrySpendAsync(stored.Id, cancellationToken).ConfigureAwait(false))
            {
                issued = await IssueAsync(
                    stored.TenantId,
                    stored.SessionId,
                    stored.SessionStartedAt,
                    now,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (issued is null)
        {
            // Somebody else spent the token between the read and the update, which is
            // the same situation as presenting an already-spent one and gets the same
            // answer. Outside the transaction above, which was rolled back.
            await RevokeSessionAsync(stored.SessionId, cancellationToken).ConfigureAwait(false);

            return SessionRefreshResult.Failed(SessionRefreshOutcome.ReusedAndRevoked);
        }

        return new SessionRefreshResult(SessionRefreshOutcome.Renewed, issued);
    }

    /// <summary>
    /// Ends the session a refresh token belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole session, not just the token presented. Signing out means the sign-in
    /// is over, and an earlier token from the same chain that was never spent would
    /// otherwise still work.
    /// </para>
    /// <para>
    /// Returns nothing, including when the token is unknown. A caller cannot be told
    /// whether what they presented was real without that becoming a way to ask.
    /// </para>
    /// </remarks>
    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var stored = await refreshTokens
            .FindAsync(Digest(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return;
        }

        tenantContext.SetTenant(stored.TenantId);

        await RevokeSessionAsync(stored.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Digest(string refreshToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

    private async Task<IssuedSession> IssueAsync(
        Guid tenantId,
        Guid sessionId,
        DateTimeOffset sessionStartedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var refreshToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

        // The refresh token never outlives the session it belongs to. Without this a
        // token issued near the cap would advertise an expiry the session will not
        // honour, and the client would be told it has thirty days when it has one.
        var refreshExpiresAt = Min(
            now + options.RefreshTokenLifetime,
            sessionStartedAt + options.SessionLifetime);

        context.Set<RefreshToken>().Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TokenHash = Digest(refreshToken),
            SessionId = sessionId,
            SessionStartedAt = sessionStartedAt,
            CreatedAt = now,
            ExpiresAt = refreshExpiresAt,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var accessExpiresAt = now + options.AccessTokenLifetime;

        return new IssuedSession(
            CreateAccessToken(tenantId, now, accessExpiresAt),
            accessExpiresAt,
            refreshToken,
            refreshExpiresAt);
    }

    private string CreateAccessToken(Guid tenantId, DateTimeOffset issuedAt, DateTimeOffset expiresAt) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,

            // The subject is the account, and the account is the tenant — the same
            // value that becomes app.tenant_id on every connection this request opens.
            // Nothing else goes in here: a token is readable by anyone holding it, so
            // a claim carrying an address or a name would be handing that to every
            // party the token passes through.
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = tenantId.ToString(),
            },

            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                SecurityAlgorithms.HmacSha256),
        });

    /// <summary>
    /// Marks a token spent, and reports whether this call was the one that did it.
    /// </summary>
    private async Task<bool> TrySpendAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var spent = await context.Set<RefreshToken>()
            .Where(token => token.Id == tokenId && token.UsedAt == null && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(token => token.UsedAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        return spent == 1;
    }

    private Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        context.Set<RefreshToken>()
            .Where(token => token.SessionId == sessionId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(token => token.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken);

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
