// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Identity;
using Dbr.Domain.Profiles;
using Dbr.Infrastructure.Persistence;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Opening an account: the credential that gets in, and the profile the account is for.
/// </summary>
/// <remarks>
/// <para>
/// <b>An account and its own profile are one thing, not two.</b> Every account here
/// exists in order to get somebody's data removed, and the identity it does that for is
/// its own — so the profile is not something a new account is invited to add afterwards.
/// The person is asked for nothing extra to get it: the terms they accept to open the
/// account are the attestation, which is exactly the claim being made and the only one
/// the common case needs.
/// </para>
/// <para>
/// <b>Separate from <see cref="PasskeyService"/> so that credentials stay free of the
/// vault.</b> Registering an authenticator needs a database and nothing else; a profile
/// needs the key manager as well. Folding one into the other would put a key-manager
/// dependency on every path that touches a credential — including adding a second
/// passkey to an account that already has one, which has no business near a wrapping
/// key.
/// </para>
/// <para>
/// <b>The two writes cannot share a transaction, so a failed one is undone instead.</b>
/// A transaction would have to be opened before the ceremony is verified, and the
/// account it is for is not known until it has been — connections take their tenant when
/// they open, so one opened beforehand carries no tenant for its whole life and every
/// write inside it is refused by the boundary. What happens instead is that a profile
/// that cannot be created takes the account back down with it: the address is left free
/// and the person can start again, rather than holding an account that can never have
/// the profile every feature reads from.
/// </para>
/// </remarks>
public sealed class SignupService(
    PasskeyService passkeys,
    IProfileService profiles,
    DbrDbContext context,
    TermsOptions terms)
{
    /// <summary>
    /// Verifies the authenticator's answer and, if it holds up and the terms are
    /// current, creates the account, its first passkey and its own profile.
    /// </summary>
    /// <param name="acceptedTermsVersion">
    /// The version of the terms the client displayed and the person accepted. Compared
    /// against what this instance serves rather than taken at face value, then recorded
    /// as the profile's attestation — a record of what somebody was shown is worth
    /// keeping, and a record of what a client claimed is not.
    /// </param>
    public async Task<SignupResult> CompleteAsync(
        Guid ceremonyId,
        AuthenticatorAttestationRawResponse attestation,
        string? acceptedTermsVersion,
        CancellationToken cancellationToken)
    {
        // Checked before the ceremony is claimed, because claiming it spends it.
        // Somebody whose terms were replaced while they were reading them can be shown
        // the new text and finish the ceremony they already started, instead of being
        // sent back to their authenticator for a second one.
        if (!string.Equals(acceptedTermsVersion?.Trim(), terms.CurrentVersion, StringComparison.Ordinal))
        {
            return SignupResult.Failed(SignupOutcome.TermsOutOfDate);
        }

        var registered = await passkeys
            .CompleteSignupAsync(ceremonyId, attestation, cancellationToken)
            .ConfigureAwait(false);

        if (registered.Outcome != PasskeySignupOutcome.Created)
        {
            return SignupResult.Failed(Map(registered.Outcome));
        }

        try
        {
            // Acting as the account the registration above established. Empty because
            // signup asks for nothing beyond an address: what the profile does is make
            // filling it in an edit rather than a decision about whether to have one.
            await profiles.CreateAsync(
                ProfileRelationship.Self,
                residencyRegion: null,
                terms.CurrentVersion,
                ProfileIdentityFields.Empty,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveAccountAsync(registered.TenantId).ConfigureAwait(false);

            throw;
        }

        return new SignupResult(SignupOutcome.Created, registered.TenantId);
    }

    /// <summary>
    /// Takes back an account whose profile could not be created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passkeys go with it, by cascade. Whatever the profile service managed to write to
    /// the vault before failing does not: it is a row of ciphertext under a key nothing
    /// references, which is the failure the profile service is ordered to leave behind
    /// and the one that costs nothing to leave.
    /// </para>
    /// <para>
    /// Deliberately not passed the request's cancellation token. The most likely reason
    /// to be here with a cancelled one is a client that gave up mid-signup, and
    /// abandoning the cleanup because the caller stopped listening is what would leave
    /// the account behind.
    /// </para>
    /// </remarks>
    private async Task RemoveAccountAsync(Guid tenantId)
    {
        // Through the boundary rather than around it: this deletes the account the unit
        // of work is already acting as, so a mistake in the id deletes nothing rather
        // than somebody else.
        await context.Set<Tenant>()
            .Where(account => account.Id == tenantId)
            .ExecuteDeleteAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static SignupOutcome Map(PasskeySignupOutcome outcome) => outcome switch
    {
        PasskeySignupOutcome.CeremonyUnusable => SignupOutcome.CeremonyUnusable,
        PasskeySignupOutcome.AttestationRejected => SignupOutcome.AttestationRejected,
        PasskeySignupOutcome.AddressAlreadyRegistered => SignupOutcome.AddressAlreadyRegistered,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome),
            outcome,
            "Unmapped registration outcome. A new way for a ceremony to fail needs a way for "
            + "signup to report it, rather than being reported as one of the others."),
    };
}
