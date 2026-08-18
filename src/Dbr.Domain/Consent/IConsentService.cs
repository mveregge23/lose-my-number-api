// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Consent;

/// <summary>
/// What the tenant has permitted, and the one place that decides it.
/// </summary>
/// <remarks>
/// <para>
/// Two audiences. A client reads and writes the three permissions through
/// <see cref="ReadAsync"/> and <see cref="RecordAsync"/>; everything that dispatches
/// work asks <see cref="IsGrantedAsync"/> first. Keeping both on one interface is what
/// stops the second from growing its own idea of what "granted" means — the reading
/// somebody sees in their settings and the reading a dispatcher acts on have to be the
/// same reading, or the switch is decorative.
/// </para>
/// <para>
/// Like the profile service, every method acts for the tenant the current scope was
/// established for and none of them takes a tenant. A caller that could name one could
/// name the wrong one, and consent is exactly the check that must not depend on an
/// argument being right.
/// </para>
/// </remarks>
public interface IConsentService
{
    /// <summary>
    /// Where all three permissions stand, including the ones never decided about.
    /// </summary>
    Task<IReadOnlyList<ConsentGrant>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records a decision about one permission.
    /// </summary>
    /// <param name="acceptedPolicyVersion">
    /// The version of the consent text the client displayed. Compared against what this
    /// instance serves rather than taken at face value, then stored — the same treatment
    /// the terms get at signup, for the same reason.
    /// </param>
    Task<RecordConsentResult> RecordAsync(
        ConsentScope scope,
        bool granted,
        string? acceptedPolicyVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the tenant currently permits <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// A scope never decided about is not granted. Nothing is done on somebody's behalf
    /// because they have not said no to it yet.
    /// </remarks>
    Task<bool> IsGrantedAsync(ConsentScope scope, CancellationToken cancellationToken);
}
