// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using Dbr.Infrastructure.Identity;
using Fido2NetLib;

namespace Dbr.Api.Endpoints;

/// <summary>
/// Opening an account with a passkey, and signing in with one.
/// </summary>
/// <remarks>
/// <para>
/// Each of the two flows is two requests, because a WebAuthn ceremony is two round
/// trips: <c>/options</c> issues a challenge, and the route it hangs off completes it.
/// </para>
/// <para>
/// Signing up is the only thing here that creates anything. Adding a second passkey to
/// an account that already exists lives under <c>/account/passkeys</c> instead, because
/// that request has to prove which account it is for and these routes are reached
/// without proving anything.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var auth = endpoints.MapGroup("/api/v1/auth");

        auth.MapPost("/register/options", BeginRegistrationAsync);
        auth.MapPost("/register", CompleteRegistrationAsync);
        auth.MapPost("/login/options", BeginLoginAsync);
        auth.MapPost("/login", CompleteLoginAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapPost("/logout", LogoutAsync);

        return endpoints;
    }

    /// <summary>
    /// Starts a signup. Answers the same way whether or not the address is already
    /// registered — finding that out is what completing the ceremony is for.
    /// </summary>
    /// <remarks>
    /// The terms version comes back with the challenge because the client has to show
    /// that text and quote the version back when it finishes. Serving it here rather
    /// than from a route of its own keeps the pair together: whatever the client
    /// displays alongside this challenge is what the account will be attested under.
    /// </remarks>
    private static async Task<IResult> BeginRegistrationAsync(
        BeginRegistrationRequest request,
        PasskeyService passkeys,
        TermsOptions terms,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return Results.Problem(
                "An email address is required. It is what the passkey is labelled with in the "
                + "authenticator, and where the account is reached.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var ceremony = await passkeys.BeginSignupAsync(request.Email.Trim(), cancellationToken);

        return CeremonyResponse(ceremony.CeremonyId, ceremony.Options.ToJson(), terms.CurrentVersion);
    }

    /// <summary>
    /// Finishes a signup: the account, its first passkey, and the profile it exists to
    /// act on.
    /// </summary>
    private static async Task<IResult> CompleteRegistrationAsync(
        CompleteRegistrationRequest request,
        SignupService signup,
        SessionService sessions,
        TermsOptions terms,
        CancellationToken cancellationToken)
    {
        var result = await signup.CompleteAsync(
            request.CeremonyId,
            request.Credential,
            request.AcceptedTermsVersion,
            cancellationToken);

        return result.Outcome switch
        {
            // Signing up signs you in. The passkey has just been used to prove the
            // account belongs to whoever registered it, so asking them to prove it
            // again immediately would be ceremony rather than security.
            SignupOutcome.Created => Results.Ok(SessionResponse(
                result.TenantId,
                await sessions.StartAsync(result.TenantId, cancellationToken))),

            SignupOutcome.CeremonyUnusable => Results.Problem(
                "That registration has expired or was already completed. Start again.",
                statusCode: StatusCodes.Status400BadRequest),

            SignupOutcome.AttestationRejected => Results.Problem(
                "The authenticator's response could not be verified.",
                statusCode: StatusCodes.Status400BadRequest),

            SignupOutcome.AddressAlreadyRegistered => Results.Problem(
                "That address already has an account. Sign in with the passkey you registered.",
                statusCode: StatusCodes.Status409Conflict),

            // The registration itself is still good, so this says what to do rather than
            // just refusing: show the current terms and send the same ceremony back.
            SignupOutcome.TermsOutOfDate => Results.Problem(
                $"These terms are no longer the current ones. Show version {terms.CurrentVersion} "
                + "and accept that instead; the registration you started is still usable.",
                statusCode: StatusCodes.Status409Conflict),

            _ => throw new InvalidOperationException($"Unhandled signup outcome {result.Outcome}."),
        };
    }

    /// <summary>
    /// Starts a sign-in. Takes no body at all: naming an account here is exactly what
    /// this design avoids, so there is nothing to send.
    /// </summary>
    private static async Task<IResult> BeginLoginAsync(
        PasskeyService passkeys,
        CancellationToken cancellationToken)
    {
        var ceremony = await passkeys.BeginLoginAsync(cancellationToken);

        return CeremonyResponse(ceremony.CeremonyId, ceremony.Options.ToJson());
    }

    private static async Task<IResult> CompleteLoginAsync(
        CompleteLoginRequest request,
        PasskeyService passkeys,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteLoginAsync(
            request.CeremonyId,
            request.Credential,
            cancellationToken);

        // Every failure answers the same way. The distinction between "no such
        // credential" and "that signature is wrong" is only useful to somebody
        // checking whether a passkey they found belongs to an account here.
        return result.Outcome switch
        {
            PasskeyLoginOutcome.Authenticated => Results.Ok(SessionResponse(
                result.TenantId,
                await sessions.StartAsync(result.TenantId, cancellationToken))),

            // Said plainly, unlike the failures above it. Getting this far took a
            // signature from the account's own passkey, so this is its owner being
            // told why they cannot get in — which is not something an attacker learns
            // anything from.
            PasskeyLoginOutcome.AccountSuspended => Suspended(),

            _ => Unauthorized(),
        };
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, spending the one presented.
    /// </summary>
    /// <remarks>
    /// The old token stops working the moment this succeeds, so a client has to keep
    /// what it gets back. That is what makes a copy of a refresh token detectable: the
    /// second party to use one finds it already spent.
    /// </remarks>
    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized();
        }

        var result = await sessions.RefreshAsync(request.RefreshToken, cancellationToken);

        // A reused token and an unknown one answer identically. The client that just
        // had its session torn down for reuse is not told that is what happened —
        // being told would be most useful to whoever caused it.
        return result switch
        {
            { Outcome: SessionRefreshOutcome.Renewed, Session: { } session } =>
                Results.Ok(SessionResponse(tenantId: null, session)),

            { Outcome: SessionRefreshOutcome.AccountSuspended } => Suspended(),

            _ => Unauthorized(),
        };
    }

    /// <summary>
    /// Ends the session a refresh token belongs to.
    /// </summary>
    /// <remarks>
    /// Always answers the same way, whether or not the token was real — otherwise this
    /// becomes a way to ask whether a token found somewhere is still worth something.
    /// <para>
    /// Access tokens already issued keep working until they expire. Nothing checks a
    /// session on an ordinary request, which is what makes ordinary requests cheap;
    /// the price is that signing out ends the session but not the access token in
    /// flight, and the access token's lifetime is how long that lasts.
    /// </para>
    /// </remarks>
    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await sessions.SignOutAsync(request.RefreshToken, cancellationToken);
        }

        return Results.NoContent();
    }

    private static IResult Unauthorized() =>
        Results.Problem("Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Forbidden rather than unauthorized: the credential was accepted, and it is the
    /// account it belongs to that may not act.
    /// </summary>
    private static IResult Suspended() =>
        Results.Problem(
            "This account is suspended and cannot be used to sign in. Contact whoever operates "
            + "this instance.",
            statusCode: StatusCodes.Status403Forbidden);

    private static object SessionResponse(Guid? tenantId, IssuedSession session) =>
        new
        {
            tenantId,
            accessToken = session.AccessToken,
            accessTokenExpiresAt = session.AccessTokenExpiresAt,
            refreshToken = session.RefreshToken,
            refreshTokenExpiresAt = session.RefreshTokenExpiresAt,
        };

    /// <summary>
    /// Wraps a ceremony handle around options the library has already serialised.
    /// </summary>
    /// <remarks>
    /// Re-serialising the options object here would break it. WebAuthn puts raw bytes
    /// on the wire as base64url, and the library's own contracts know that where the
    /// default serialiser would emit standard base64 — which a browser rejects, on a
    /// path that needs an authenticator to reach.
    /// </remarks>
    private static IResult CeremonyResponse(
        Guid ceremonyId,
        string optionsJson,
        string? termsVersion = null)
    {
        var body = new JsonObject
        {
            ["ceremonyId"] = ceremonyId.ToString(),
            ["publicKey"] = JsonNode.Parse(optionsJson),
        };

        // Absent on a sign-in, where there is nothing to agree to and naming a version
        // would invite a client to collect an acceptance nobody asked for.
        if (termsVersion is not null)
        {
            body["termsVersion"] = termsVersion;
        }

        return Results.Text(body.ToJsonString(), "application/json");
    }
}

/// <param name="Email">Where the account is reached, and how the passkey is labelled.</param>
public sealed record BeginRegistrationRequest(string Email);

/// <param name="CeremonyId">The handle returned by <c>/register/options</c>.</param>
/// <param name="Credential">What <c>navigator.credentials.create()</c> produced.</param>
/// <param name="AcceptedTermsVersion">
/// The <c>termsVersion</c> from that same response, echoed back to say the person was
/// shown it and agreed. It becomes the attestation on the profile this creates, which is
/// why signup refuses a version that is not the current one rather than recording it.
/// </param>
public sealed record CompleteRegistrationRequest(
    Guid CeremonyId,
    AuthenticatorAttestationRawResponse Credential,
    string? AcceptedTermsVersion);

/// <param name="CeremonyId">The handle returned by <c>/login/options</c>.</param>
/// <param name="Credential">What <c>navigator.credentials.get()</c> produced.</param>
public sealed record CompleteLoginRequest(
    Guid CeremonyId,
    AuthenticatorAssertionRawResponse Credential);

/// <param name="RefreshToken">The refresh token from the most recent response.</param>
public sealed record RefreshRequest(string RefreshToken);
