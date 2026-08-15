// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dbr.Domain.Vault;

namespace Dbr.Infrastructure.Vault;

/// <summary>
/// Key management over OpenBao's Transit engine.
/// </summary>
/// <remarks>
/// <para>
/// Transit is a key manager that performs cryptography rather than handing keys out:
/// the wrapping key is created inside it, is never exported, and can be destroyed
/// there. That is the property the whole envelope scheme rests on — a database dump is
/// worthless without this service, because nothing in the database can be unwrapped
/// without asking it.
/// </para>
/// <para>
/// One key per tenant, named after the tenant. It costs nothing — Transit keys are
/// cheap — and it is what turns account deletion into a single irreversible operation
/// rather than a hunt for every copy of a row.
/// </para>
/// </remarks>
public sealed class OpenBaoKeyManagementProvider(HttpClient client, OpenBaoOptions options)
    : IKeyManagementProvider
{
    /// <summary>
    /// The name a tenant's wrapping key goes by.
    /// </summary>
    /// <remarks>
    /// Public because it is not only this class's business: a policy narrowing what
    /// this service may do has to name the same keys, and a policy written against a
    /// pattern that has drifted from the code grants either too much or nothing at
    /// all.
    /// </remarks>
    public static string KeyNameFor(Guid tenantId) => $"tenant-{tenantId:D}";

    public async Task EnsureTenantKeyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = KeyNameFor(tenantId);

        // Creating a key that exists is a no-op in Transit rather than a conflict,
        // which is what lets this be called without first asking whether it is needed.
        await SendAsync(
            HttpMethod.Post,
            $"/v1/{options.TransitMount}/keys/{key}",
            new { type = "aes256-gcm96" },
            $"create the wrapping key for tenant {tenantId}",
            cancellationToken).ConfigureAwait(false);

        // Deletion is off by default, and a key that cannot be deleted is an account
        // that cannot really be erased. The risk this opens — a token that can destroy
        // a tenant's data outright — is answered by narrowing the token, not by
        // leaving erasure impossible.
        await SendAsync(
            HttpMethod.Post,
            $"/v1/{options.TransitMount}/keys/{key}/config",
            new { deletion_allowed = true },
            $"allow deletion of the wrapping key for tenant {tenantId}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneratedDataKey> GenerateDataKeyAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // One call returns both halves: the key to use now, and the wrapped form to
        // keep. Asking for them separately would mean a moment where a key exists and
        // nothing can unwrap it.
        var data = await SendAsync(
            HttpMethod.Post,
            $"/v1/{options.TransitMount}/datakey/plaintext/{KeyNameFor(tenantId)}",
            payload: null,
            $"generate a data key for tenant {tenantId}",
            cancellationToken).ConfigureAwait(false);

        return new GeneratedDataKey(
            new DataKey(Convert.FromBase64String(Read(data, "plaintext", tenantId))),
            Read(data, "ciphertext", tenantId));
    }

    public async Task<DataKey> UnwrapDataKeyAsync(
        Guid tenantId,
        string wrappedKey,
        CancellationToken cancellationToken)
    {
        var data = await SendAsync(
            HttpMethod.Post,
            $"/v1/{options.TransitMount}/decrypt/{KeyNameFor(tenantId)}",
            new { ciphertext = wrappedKey },
            $"unwrap a data key for tenant {tenantId}",
            cancellationToken).ConfigureAwait(false);

        return new DataKey(Convert.FromBase64String(Read(data, "plaintext", tenantId)));
    }

    public async Task DestroyTenantKeyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await SendAsync(
            HttpMethod.Delete,
            $"/v1/{options.TransitMount}/keys/{KeyNameFor(tenantId)}",
            payload: null,
            $"destroy the wrapping key for tenant {tenantId}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>data</c> object from a Transit response, or an exception naming what was
    /// being attempted.
    /// </summary>
    /// <remarks>
    /// The failure message says what could not be done and for which tenant, and
    /// nothing about what the server said. Transit echoes some of its input in errors,
    /// and this is the one code path where the input is key material.
    /// </remarks>
    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? payload,
        string attempt,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new KeyManagementException(
                $"The key manager refused to {attempt} ({(int)response.StatusCode} "
                + $"{response.StatusCode}).");
        }

        // Several Transit operations answer with no body at all — creating a key that
        // already exists, deleting one. Those have nothing to read and nothing to
        // check beyond the status.
        if (response.StatusCode == HttpStatusCode.NoContent
            || response.Content.Headers.ContentLength is null or 0)
        {
            return default;
        }

        var body = await response.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        return body.TryGetProperty("data", out var data) ? data : default;
    }

    private static string Read(JsonElement data, string property, Guid tenantId) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
            ? value.GetString() ?? throw Missing(property, tenantId)
            : throw Missing(property, tenantId);

    private static KeyManagementException Missing(string property, Guid tenantId) =>
        new($"The key manager's answer for tenant {tenantId} had no '{property}'. The service "
            + "responded, so this is a version or mount mismatch rather than a failure to reach it.");
}
