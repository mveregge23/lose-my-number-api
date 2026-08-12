// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Infrastructure.Persistence;
using Dbr.Infrastructure.Tenancy;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Opening an account with a passkey, and signing in with one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nobody says who they are before they prove it.</b> There is no step where a
/// caller submits an address and is told whether it has an account. Signing in starts
/// with no identifier at all: the browser offers whichever passkey it holds for this
/// site, and the account is discovered from the credential that answers. That is the
/// reason passkeys here are discoverable ones — requiring the authenticator to store
/// the account handle itself is what removes the need to ask for it.
/// </para>
/// <para>
/// The cost is real and worth stating: an authenticator with no room to store a
/// resident credential cannot be used, and a browser with no passkey for this site
/// can only say so. The alternative buys those back by answering "does this address
/// have an account here?" to anyone who asks — which, for a service whose users are
/// trying to reduce what is known about them, is the harm rather than a step towards
/// preventing it.
/// </para>
/// </remarks>
public sealed class PasskeyService(
    IFido2 fido2,
    PasskeyCeremonyStore ceremonies,
    PasskeyLookup passkeys,
    DbrDbContext context,
    TenantContext tenantContext,
    PasskeyOptions options)
{
    /// <summary>
    /// Issues the challenge that opens a new account, and mints the account's id.
    /// </summary>
    /// <remarks>
    /// The id is generated here rather than by the database because it has to exist
    /// before the row does: it is what the authenticator stores as the user handle,
    /// and the account is only written once the authenticator has answered. Nothing
    /// is persisted about the address yet — an abandoned ceremony leaves a row that
    /// expires, not an account nobody can sign in to.
    /// </remarks>
    public async Task<PasskeyChallenge<CredentialCreateOptions>> BeginSignupAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var tenantId = Guid.NewGuid();

        var createOptions = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                // The account id and nothing else. The authenticator keeps this, and
                // syncs it wherever the passkey syncs, so it must not be anything
                // that says who the person is.
                Id = tenantId.ToByteArray(bigEndian: true),

                // These two are shown in the passkey picker, so they have to be
                // recognisable to the person choosing — which the id deliberately is
                // not.
                Name = email,
                DisplayName = email,
            },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // The whole login design rests on this. A credential the
                // authenticator does not store cannot be offered without being asked
                // for by name, and asking for it by name means asking who the caller
                // is before they have proved anything.
                //
                // Only the modern field is set. Its predecessor, requireResidentKey,
                // is a boolean browsers now derive from this one, and the library
                // marks it obsolete for exactly that reason — setting both is an
                // invitation for the two to disagree.
                ResidentKey = ResidentKeyRequirement.Required,

                // The passkey alone is possession. Requiring the authenticator to
                // verify its holder — biometric, PIN — is what makes it two factors,
                // and it is what lets this be the only credential rather than one of
                // two prompts.
                UserVerification = UserVerificationRequirement.Required,
            },

            // Not requested, so authenticators report no make or model. Attestation
            // is only worth collecting to check it against a metadata service and
            // refuse authenticators that fail policy; this service has no such policy,
            // and collecting an identifier nothing consults is collection for its own
            // sake.
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var ceremonyId = await ceremonies.IssueAsync(
            PasskeyCeremonyPurpose.Registration,
            createOptions.ToJson(),
            options.CeremonyLifetime,
            cancellationToken).ConfigureAwait(false);

        return new PasskeyChallenge<CredentialCreateOptions>(ceremonyId, createOptions);
    }

    /// <summary>
    /// Verifies the authenticator's answer and, if it holds up, creates the account
    /// and its first passkey.
    /// </summary>
    public async Task<PasskeySignupResult> CompleteSignupAsync(
        Guid ceremonyId,
        AuthenticatorAttestationRawResponse attestation,
        CancellationToken cancellationToken)
    {
        var claimed = await ceremonies
            .ClaimAsync(ceremonyId, PasskeyCeremonyPurpose.Registration, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return PasskeySignupResult.Failed(PasskeySignupOutcome.CeremonyUnusable);
        }

        var original = CredentialCreateOptions.FromJson(claimed);
        var tenantId = new Guid(original.User.Id, bigEndian: true);

        RegisteredPublicKeyCredential registered;

        try
        {
            registered = await fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = attestation,
                    OriginalOptions = original,

                    // Asked through the same narrow lookup login uses, because at this
                    // point in a signup there is still no tenant and the table cannot
                    // be read any other way. The unique index is the real guarantee;
                    // this turns the collision into an answer instead of an exception.
                    IsCredentialIdUniqueToUserCallback = async (parameters, token) =>
                        await passkeys.FindAsync(parameters.CredentialId, token)
                            .ConfigureAwait(false) is null,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            return PasskeySignupResult.Failed(PasskeySignupOutcome.AttestationRejected);
        }

        // Everything from here writes, so the unit of work starts acting for the
        // account being created. The policy's WITH CHECK then means this can only ever
        // create the account it is already claiming to be — the guarantee is in the
        // database rather than in this method remembering to compare two values.
        tenantContext.SetTenant(tenantId);

        var now = DateTimeOffset.UtcNow;

        context.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId,
            Email = original.User.Name,
            CreatedAt = now,
        });

        context.Set<Passkey>().Add(new Passkey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CredentialId = registered.Id,
            PublicKey = registered.PublicKey,
            SignatureCount = registered.SignCount,
            IsBackupEligible = registered.IsBackupEligible,
            IsBackedUp = registered.IsBackedUp,
            CreatedAt = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The address was taken between the challenge being issued and answered,
            // or the person already had an account and did not remember. Nothing was
            // written: both rows go in one transaction.
            return PasskeySignupResult.Failed(PasskeySignupOutcome.AddressAlreadyRegistered);
        }

        return new PasskeySignupResult(PasskeySignupOutcome.Created, tenantId);
    }

    /// <summary>
    /// Issues a challenge for signing in, naming no account and no credential.
    /// </summary>
    /// <remarks>
    /// The empty allow-list is the point. It tells the browser to offer whatever
    /// passkey it holds for this relying party, which means this response is identical
    /// no matter who is asking or whether they have an account at all.
    /// </remarks>
    public async Task<PasskeyChallenge<AssertionOptions>> BeginLoginAsync(CancellationToken cancellationToken)
    {
        var assertionOptions = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        var ceremonyId = await ceremonies.IssueAsync(
            PasskeyCeremonyPurpose.Authentication,
            assertionOptions.ToJson(),
            options.CeremonyLifetime,
            cancellationToken).ConfigureAwait(false);

        return new PasskeyChallenge<AssertionOptions>(ceremonyId, assertionOptions);
    }

    /// <summary>
    /// Verifies an assertion and, if it holds up, reports whose account it was.
    /// </summary>
    public async Task<PasskeyLoginResult> CompleteLoginAsync(
        Guid ceremonyId,
        AuthenticatorAssertionRawResponse assertion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        var claimed = await ceremonies
            .ClaimAsync(ceremonyId, PasskeyCeremonyPurpose.Authentication, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return PasskeyLoginResult.Failed(PasskeyLoginOutcome.CeremonyUnusable);
        }

        var stored = await passkeys.FindAsync(assertion.RawId, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return PasskeyLoginResult.Failed(PasskeyLoginOutcome.AssertionRejected);
        }

        VerifyAssertionResult verified;

        try
        {
            verified = await fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertion,
                    OriginalOptions = AssertionOptions.FromJson(claimed),
                    StoredPublicKey = stored.PublicKey,
                    StoredSignatureCounter = (uint)stored.SignatureCount,

                    // The authenticator reports which account it believes this
                    // credential belongs to. Checking it against the account the
                    // credential is actually filed under closes the gap between the
                    // two halves of the lookup — a signature that verifies against one
                    // account's key must not be able to sign anyone else in.
                    IsUserHandleOwnerOfCredentialIdCallback = (parameters, _) =>
                        Task.FromResult(parameters.UserHandle.AsSpan().SequenceEqual(
                            stored.TenantId.ToByteArray(bigEndian: true))),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            return PasskeyLoginResult.Failed(PasskeyLoginOutcome.AssertionRejected);
        }

        await RecordUseAsync(stored.TenantId, assertion.RawId, verified, cancellationToken)
            .ConfigureAwait(false);

        return new PasskeyLoginResult(PasskeyLoginOutcome.Authenticated, stored.TenantId);
    }

    /// <summary>
    /// Writes back what the assertion revealed, now that the account is known and the
    /// ordinary boundary applies again.
    /// </summary>
    private async Task RecordUseAsync(
        Guid tenantId,
        byte[] credentialId,
        VerifyAssertionResult verified,
        CancellationToken cancellationToken)
    {
        tenantContext.SetTenant(tenantId);

        var credential = await context.Set<Passkey>()
            .SingleOrDefaultAsync(
                candidate => candidate.CredentialId == credentialId,
                cancellationToken)
            .ConfigureAwait(false);

        if (credential is null)
        {
            // Only reachable if the passkey was deleted between the lookup and here.
            // The assertion was valid, so there is nothing to report — the write is
            // simply moot.
            return;
        }

        // The counter is the whole reason to write on a read-shaped operation: it is
        // only meaningful compared against the last one, so failing to advance it
        // would disarm clone detection permanently.
        credential.SignatureCount = verified.SignCount;
        credential.IsBackedUp = verified.IsBackedUp;
        credential.LastUsedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
