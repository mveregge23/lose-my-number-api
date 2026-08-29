// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;

namespace Dbr.Infrastructure.InternalEdge;

/// <summary>
/// Spends a grant against the process that holds the keys.
/// </summary>
/// <remarks>
/// The worker's whole reach into the vault, and it is one method wide on purpose. There is
/// no way from here to ask which grants exist, to ask for a different one, or to ask for
/// more of an identity than the grant covered — a worker holds a token and gets back
/// whatever that token was minted for.
/// </remarks>
public interface IReleaseClient
{
    /// <summary>
    /// Spends a grant.
    /// </summary>
    /// <returns>
    /// What the grant covered, or <see langword="null"/> when it was refused. One answer for
    /// every refusal, because the edge gives one: a token that was never minted, one that
    /// expired and one already spent are the same outcome to a caller holding it.
    /// </returns>
    Task<ReleaseResponse?> RedeemAsync(string token, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReleaseClient"/>
/// <remarks>
/// The certificate and the address it trusts are attached once, where the client is
/// registered, rather than per call. A caller that could choose either could be pointed at
/// something else holding a valid grant token.
/// </remarks>
public sealed class InternalReleaseClient(HttpClient client) : IReleaseClient
{
    public async Task<ReleaseResponse?> RedeemAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var response = await client
            .PostAsJsonAsync("/internal/v1/vault/release", new ReleaseRequest(token), cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            return null;
        }

        // Anything else unexpected throws rather than answering null. A refused grant and a
        // listener that is down are different situations: one means re-plan the work, the
        // other means the work never ran, and collapsing them would let an outage look like
        // every grant having expired at once.
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<ReleaseResponse>(cancellationToken)
            .ConfigureAwait(false);
    }
}
