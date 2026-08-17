// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fido2NetLib;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// The API as a client meets it: paths, a bearer token, and whatever JSON came back.
/// </summary>
/// <remarks>
/// Shared so that a test about one feature does not carry its own copy of signing up.
/// The ceremony below is real — a software authenticator answering a real challenge —
/// and a second copy of it would be a second thing to keep correct for no benefit to
/// whichever test happens to need an account.
/// </remarks>
internal sealed class ApiClient(HttpClient client)
{
    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, string? accessToken) =>
        await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), accessToken);

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path,
        object payload,
        string? accessToken) =>
        await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) },
            accessToken);

    public async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(
        string path,
        object payload,
        string? accessToken) =>
        await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(payload) },
            accessToken);

    public async Task<(HttpStatusCode Status, JsonElement Body)> DeleteAsync(
        string path,
        string? accessToken) =>
        await SendAsync(new HttpRequestMessage(HttpMethod.Delete, path), accessToken);

    /// <summary>
    /// Opens an account and signs it in, the way a browser would: a challenge, a real
    /// signature over it, the terms it was offered accepted, and the session that comes
    /// back.
    /// </summary>
    /// <remarks>
    /// The version accepted is the one the challenge came with, rather than a constant
    /// repeated here — that is what a client does, and it means a test does not quietly
    /// keep passing a version the server stopped serving.
    /// </remarks>
    public async Task<JsonElement> SignUpAsync(string email, TestAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(authenticator);

        var (_, options) = await PostAsync("/api/v1/auth/register/options", new { email }, null);

        var (status, session) = await CompleteSignUpAsync(
            options,
            authenticator,
            options.GetProperty("termsVersion").GetString());

        Assert.Equal(HttpStatusCode.OK, status);

        return session;
    }

    /// <summary>
    /// Answers a registration challenge, accepting whichever terms version is given.
    /// </summary>
    /// <remarks>
    /// Split out so a test can finish a real ceremony while accepting the wrong terms —
    /// the one case where what a client sends and what it was offered differ.
    /// </remarks>
    public async Task<(HttpStatusCode Status, JsonElement Body)> CompleteSignUpAsync(
        JsonElement options,
        TestAuthenticator authenticator,
        string? acceptedTermsVersion)
    {
        ArgumentNullException.ThrowIfNull(authenticator);

        return await PostAsync(
            "/api/v1/auth/register",
            new
            {
                ceremonyId = options.GetProperty("ceremonyId").GetString(),
                credential = authenticator.Register(
                    CredentialCreateOptions.FromJson(options.GetProperty("publicKey").GetRawText()),
                    DbrApiFactory.Origin),
                acceptedTermsVersion,
            },
            null);
    }

    /// <summary>Starts a signup without finishing it.</summary>
    public async Task<JsonElement> BeginSignUpAsync(string email)
    {
        var (_, options) = await PostAsync("/api/v1/auth/register/options", new { email }, null);

        return options;
    }

    public async Task<JsonElement> SignInAsync(TestAuthenticator authenticator, byte[] userHandle)
    {
        ArgumentNullException.ThrowIfNull(authenticator);

        var (_, options) = await PostAsync("/api/v1/auth/login/options", new { }, null);

        var (status, session) = await PostAsync(
            "/api/v1/auth/login",
            new
            {
                ceremonyId = options.GetProperty("ceremonyId").GetString(),
                credential = authenticator.Assert(
                    AssertionOptions.FromJson(options.GetProperty("publicKey").GetRawText()),
                    DbrApiFactory.Origin,
                    userHandle),
            },
            null);

        Assert.Equal(HttpStatusCode.OK, status);

        return session;
    }

    /// <summary>The account a signup response belongs to.</summary>
    public static Guid TenantId(JsonElement session) =>
        Guid.Parse(session.GetProperty("tenantId").GetString()!);

    public static string AccessToken(JsonElement session) =>
        session.GetProperty("accessToken").GetString()!;

    /// <summary>The handle the authenticator stores, which is how sign-in names nobody.</summary>
    public static byte[] UserHandle(JsonElement signup) =>
        TenantId(signup).ToByteArray(bigEndian: true);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpRequestMessage request,
        string? accessToken)
    {
        using (request)
        {
            if (accessToken is not null)
            {
                request.Headers.Add("Authorization", $"Bearer {accessToken}");
            }

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Only what claims to be JSON is parsed. An unhandled exception answers with
            // the developer exception page, which is a document about a failure rather
            // than a failed request's document — a test asserting on the status would
            // otherwise fail while parsing it, and say nothing about what went wrong.
            var isJson = response.Content.Headers.ContentType?.MediaType?
                .Contains("json", StringComparison.OrdinalIgnoreCase) ?? false;

            return (
                response.StatusCode,
                body.Length == 0 || !isJson ? default : JsonDocument.Parse(body).RootElement.Clone());
        }
    }
}
