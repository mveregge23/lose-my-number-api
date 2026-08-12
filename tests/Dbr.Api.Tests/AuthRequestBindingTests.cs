// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using Dbr.Api.Endpoints;

namespace Dbr.Api.Tests;

/// <summary>
/// What a browser actually posts, bound the way the endpoints bind it.
/// </summary>
/// <remarks>
/// <para>
/// This seam is worth a test because it is invisible in both directions. WebAuthn puts
/// binary on the wire as base64url and spells one field <c>clientDataJSON</c>, neither
/// of which a default serialiser would guess — the library's models carry attributes
/// that handle both. If those ever stop lining up with the serialiser the endpoints
/// use, nothing fails to compile: requests simply arrive with empty or mangled byte
/// arrays, and every ceremony fails verification for reasons that look cryptographic.
/// </para>
/// <para>
/// The payloads below are written out as literal JSON rather than round-tripped
/// through the same serialiser that would read them. A round trip agrees with itself
/// no matter what it does; only a literal says what a browser sends.
/// </para>
/// </remarks>
public class AuthRequestBindingTests
{
    /// <summary>
    /// The options minimal APIs bind JSON bodies with, which is camelCase and not the
    /// default of <see cref="JsonSerializer"/>.
    /// </summary>
    private static readonly JsonSerializerOptions AsTheEndpointsBind = JsonSerializerOptions.Web;

    [Fact]
    public void A_registration_response_binds_from_what_the_browser_sends()
    {
        // "AQIDBA" is base64url for 01 02 03 04. Standard base64 would be "AQIDBA==",
        // and a serialiser expecting that reads this as something else entirely.
        const string Body = """
            {
              "ceremonyId": "8a1d5f8e-1c2b-4a3d-9e6f-0b1c2d3e4f50",
              "credential": {
                "id": "AQIDBA",
                "rawId": "AQIDBA",
                "type": "public-key",
                "response": {
                  "attestationObject": "BQYH",
                  "clientDataJSON": "CAkK"
                },
                "clientExtensionResults": {}
              }
            }
            """;

        var request = JsonSerializer.Deserialize<CompleteRegistrationRequest>(Body, AsTheEndpointsBind);

        Assert.NotNull(request);
        Assert.Equal(Guid.Parse("8a1d5f8e-1c2b-4a3d-9e6f-0b1c2d3e4f50"), request.CeremonyId);
        Assert.Equal<byte[]>([1, 2, 3, 4], request.Credential.RawId);
        Assert.Equal<byte[]>([5, 6, 7], request.Credential.Response.AttestationObject);
        Assert.Equal<byte[]>([8, 9, 10], request.Credential.Response.ClientDataJson);
    }

    [Fact]
    public void A_sign_in_response_binds_from_what_the_browser_sends()
    {
        const string Body = """
            {
              "ceremonyId": "8a1d5f8e-1c2b-4a3d-9e6f-0b1c2d3e4f50",
              "credential": {
                "id": "AQIDBA",
                "rawId": "AQIDBA",
                "type": "public-key",
                "response": {
                  "authenticatorData": "BQYH",
                  "clientDataJSON": "CAkK",
                  "signature": "CwwN",
                  "userHandle": "Dg8Q"
                },
                "clientExtensionResults": {}
              }
            }
            """;

        var request = JsonSerializer.Deserialize<CompleteLoginRequest>(Body, AsTheEndpointsBind);

        Assert.NotNull(request);
        Assert.Equal<byte[]>([1, 2, 3, 4], request.Credential.RawId);
        Assert.Equal<byte[]>([5, 6, 7], request.Credential.Response.AuthenticatorData);
        Assert.Equal<byte[]>([11, 12, 13], request.Credential.Response.Signature);

        // The account handle the authenticator stored at registration. Without it a
        // sign-in that named nobody has nothing to work out the account from.
        Assert.Equal<byte[]>([14, 15, 16], request.Credential.Response.UserHandle);
    }

    [Fact]
    public void A_signup_request_binds_the_address()
    {
        var request = JsonSerializer.Deserialize<BeginRegistrationRequest>(
            """{ "email": "someone@example.com" }""",
            AsTheEndpointsBind);

        Assert.NotNull(request);
        Assert.Equal("someone@example.com", request.Email);
    }
}
