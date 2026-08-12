// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// A passkey, in software: it holds a key pair and answers challenges the way a real
/// authenticator would.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than mocked because a mock would decide the answer to the only
/// question worth asking. The registration and login paths are almost entirely
/// cryptographic verification — is this signature over this challenge by this key —
/// and a fake that returns "verified" tests the code around the check while stepping
/// over the check. This produces real ECDSA signatures over real authenticator data,
/// so the library either accepts them or does not.
/// </para>
/// <para>
/// It is also the only way to exercise these paths at all. Every alternative involves
/// a browser and a physical authenticator, neither of which exists in CI.
/// </para>
/// </remarks>
internal sealed class TestAuthenticator : IDisposable
{
    // Flags in the authenticator data byte, per the WebAuthn specification.
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte BackupEligible = 0x08;
    private const byte BackedUp = 0x10;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private uint _signCount;

    /// <summary>
    /// This authenticator's handle for its one credential. Random and 32 bytes, which
    /// is what makes it unguessable — the property the pre-authentication lookup
    /// leans on.
    /// </summary>
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// How many assertions it has produced, which every assertion reports and the
    /// server compares against the last one it saw.
    /// </summary>
    /// <remarks>
    /// Settable so a test can wind it back — which is the one thing a genuine
    /// authenticator cannot do, and therefore what a copy of one looks like.
    /// </remarks>
    public uint SignCount
    {
        get => _signCount;
        set => _signCount = value;
    }

    public void Dispose() => _key.Dispose();

    /// <summary>Answers a registration challenge.</summary>
    public AuthenticatorAttestationRawResponse Register(CredentialCreateOptions options, string origin)
    {
        var clientDataJson = ClientData("webauthn.create", options.Challenge, origin);

        var authenticatorData = AuthenticatorData(
            options.Rp.Id,
            AttestedCredentialData,
            [
                // The AAGUID, all zeros: what a real authenticator reports when
                // attestation was not requested, which is the case here.
                .. new byte[16],
                .. BigEndian((ushort)CredentialId.Length),
                .. CredentialId,
                .. CoseKey(),
            ]);

        // Attestation format "none": no statement to make, because none was asked for.
        var attestationObject = new CborWriter();
        attestationObject.WriteStartMap(3);
        attestationObject.WriteTextString("fmt");
        attestationObject.WriteTextString("none");
        attestationObject.WriteTextString("attStmt");
        attestationObject.WriteStartMap(0);
        attestationObject.WriteEndMap();
        attestationObject.WriteTextString("authData");
        attestationObject.WriteByteString(authenticatorData);
        attestationObject.WriteEndMap();

        return new AuthenticatorAttestationRawResponse
        {
            Id = Base64Url.EncodeToString(CredentialId),
            RawId = CredentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = attestationObject.Encode(),
                ClientDataJson = clientDataJson,
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    /// <summary>Answers a sign-in challenge.</summary>
    /// <param name="userHandle">
    /// The account handle this authenticator stored at registration. A discoverable
    /// credential returns it unasked, which is what lets the server work out whose
    /// account this is without having been told.
    /// </param>
    public AuthenticatorAssertionRawResponse Assert(
        AssertionOptions options,
        string origin,
        byte[] userHandle) =>
        Assert(options, origin, userHandle, _key);

    /// <summary>
    /// Answers with a signature from a different key than the one registered — the
    /// shape of a forged assertion.
    /// </summary>
    public AuthenticatorAssertionRawResponse AssertWithTheWrongKey(
        AssertionOptions options,
        string origin,
        byte[] userHandle)
    {
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return Assert(options, origin, userHandle, impostor);
    }

    private AuthenticatorAssertionRawResponse Assert(
        AssertionOptions options,
        string origin,
        byte[] userHandle,
        ECDsa signingKey)
    {
        _signCount++;

        var clientDataJson = ClientData("webauthn.get", options.Challenge, origin);
        var authenticatorData = AuthenticatorData(options.RpId, additionalFlags: 0, attestedCredentialData: []);

        return new AuthenticatorAssertionRawResponse
        {
            Id = Base64Url.EncodeToString(CredentialId),
            RawId = CredentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                ClientDataJson = clientDataJson,
                UserHandle = userHandle,

                // Over the authenticator data and a hash of the client data, which is
                // what binds the signature to this challenge and this origin rather
                // than to a message the authenticator merely saw.
                Signature = signingKey.SignData(
                    [.. authenticatorData, .. SHA256.HashData(clientDataJson)],
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence),
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    private static byte[] ClientData(string type, byte[] challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type,
            challenge = Base64Url.EncodeToString(challenge),
            origin,
            crossOrigin = false,
        });

    private static byte[] BigEndian(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);

        return bytes;
    }

    private byte[] AuthenticatorData(
        string? relyingPartyId,
        byte additionalFlags,
        byte[] attestedCredentialData)
    {
        // A ceremony with no relying party would be one no authenticator could bind a
        // signature to, so this failing loudly here is better than hashing "".
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingPartyId);

        var signCount = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(signCount, _signCount);

        return
        [
            // Binds the assertion to one relying party: a signature made for another
            // site hashes a different domain here and stops matching.
            .. SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId)),
            (byte)(UserPresent | UserVerified | BackupEligible | BackedUp | additionalFlags),
            .. signCount,
            .. attestedCredentialData,
        ];
    }

    /// <summary>The public key, COSE-encoded, as an authenticator would report it.</summary>
    private byte[] CoseKey()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        var writer = new CborWriter();
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(2);        // Key type: elliptic curve, two coordinates.
        writer.WriteInt32(3);
        writer.WriteInt32(-7);       // Algorithm: ES256.
        writer.WriteInt32(-1);
        writer.WriteInt32(1);        // Curve: P-256.
        writer.WriteInt32(-2);
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3);
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();

        return writer.Encode();
    }
}
