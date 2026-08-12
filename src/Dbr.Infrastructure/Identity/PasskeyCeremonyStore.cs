// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Issues WebAuthn challenges and hands each one back exactly once.
/// </summary>
public sealed class PasskeyCeremonyStore(DbrDbContext context)
{
    /// <summary>
    /// Records an issued set of options and returns the handle the client quotes back.
    /// </summary>
    public async Task<Guid> IssueAsync(
        PasskeyCeremonyPurpose purpose,
        string options,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Expired rows are useless to everyone, so clearing them is folded into the
        // act that creates more of them rather than deferred to a scheduler that does
        // not exist yet. It is one indexed delete against a table that only ever holds
        // a few minutes of traffic.
        await context.Set<PasskeyCeremony>()
            .Where(ceremony => ceremony.ExpiresAt < now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var issued = new PasskeyCeremony
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            Options = options,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

        context.Set<PasskeyCeremony>().Add(issued);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return issued.Id;
    }

    /// <summary>
    /// Spends a ceremony, returning the options it was issued with, or
    /// <see langword="null"/> if it does not exist, has expired, has already been
    /// spent, or was issued for something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement, not a read followed by a write. Two concurrent attempts to
    /// finish the same ceremony would both pass a separate "is it still unspent?"
    /// check before either recorded an answer, which is precisely the replay this is
    /// here to prevent. As a single conditional <c>UPDATE</c> the loser matches no
    /// row and is told the ceremony is gone.
    /// </para>
    /// <para>
    /// The four ways to fail are deliberately indistinguishable to the caller. Which
    /// one it was is only ever interesting to an attacker: it is the difference
    /// between "that handle is wrong" and "that handle was right and you were a
    /// second late".
    /// </para>
    /// </remarks>
    public Task<string?> ClaimAsync(
        Guid ceremonyId,
        PasskeyCeremonyPurpose purpose,
        CancellationToken cancellationToken) =>
        context.ExecuteCommandAsync(
            """
            UPDATE passkey_ceremony
            SET consumed_at = now()
            WHERE id = @id
              AND purpose = @purpose
              AND consumed_at IS NULL
              AND expires_at > now()
            RETURNING options
            """,
            async (command, token) =>
            {
                command
                    .WithParameter("id", ceremonyId)
                    .WithParameter("purpose", purpose.ToString().ToLowerInvariant());

                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            cancellationToken);
}
