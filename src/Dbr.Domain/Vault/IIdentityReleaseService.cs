// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Profiles;

namespace Dbr.Domain.Vault;

/// <summary>How an attempt to mint a grant ended.</summary>
public enum MintReleaseOutcome
{
    /// <summary>Minted, and redeemable until it expires.</summary>
    Minted,

    /// <summary>No such scan for this tenant.</summary>
    /// <remarks>
    /// One outcome for "no such scan" and "somebody else's scan", as everywhere else:
    /// telling them apart would confirm that an id belongs to another account.
    /// </remarks>
    ScanNotFound,

    /// <summary>The run is not in a state where anything should be searching.</summary>
    /// <remarks>
    /// A completed scan minting a fresh decryption right is the case worth refusing. Work
    /// that arrives late — a lane draining after the run was marked failed — would
    /// otherwise still be able to open somebody's identity.
    /// </remarks>
    ScanNotRunnable,

    /// <summary>No such broker in this instance's catalog.</summary>
    UnknownBroker,

    /// <summary>The broker is real, and this scan was narrowed to exclude it.</summary>
    /// <remarks>
    /// Separate from <see cref="UnknownBroker"/> because they are different bugs — one is
    /// a catalog that disagrees with the caller, the other is a fan-out that ignored the
    /// scope the tenant asked for. Refusing it matters more than it looks: a scan narrowed
    /// to two brokers is a statement about who gets told, and minting a grant for a third
    /// would decrypt an identity for a leg the tenant declined.
    /// </remarks>
    BrokerNotInScan,

    /// <summary>Nothing was asked for.</summary>
    /// <remarks>
    /// A grant covering no fields would decrypt nothing and still be a row somebody has
    /// to reason about. Refused so that "a release exists" and "something was released"
    /// stay the same statement.
    /// </remarks>
    NothingRequested,
}

/// <summary>How an attempt to redeem a grant ended.</summary>
/// <remarks>
/// Every refusal is one value from the redeemer's point of view on purpose. A token that
/// was never minted, one that expired, and one already spent are different facts, and
/// distinguishing them for whoever presented it turns a grant into an oracle about other
/// grants. What the difference is good for is the log, which is where it goes.
/// </remarks>
public enum RedeemReleaseOutcome
{
    /// <summary>Spent, and the fields it covered are in hand.</summary>
    Granted,

    /// <summary>Not a grant that can be spent now.</summary>
    Refused,
}

/// <param name="Token">
/// The secret to present. Returned exactly once, at minting — it is never stored and
/// cannot be recovered, so a caller that loses it mints another rather than looking this
/// one up.
/// </param>
public sealed record MintedRelease(Guid Id, string Token, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Names the type and withholds the secret.
    /// </summary>
    /// <remarks>
    /// A record prints every member it has, and this one is a bearer credential for
    /// somebody's identity. The same refusal the identity types carry, for the same
    /// reason: a log line, an exception message, or a queue envelope is one interpolation
    /// away.
    /// </remarks>
    public override string ToString() =>
        $"MintedRelease {{ Id = {Id}, ExpiresAt = {ExpiresAt:O}, [withheld] }}";
}

/// <param name="Release">The grant, or <see langword="null"/> when nothing was minted.</param>
public sealed record MintReleaseResult(MintReleaseOutcome Outcome, MintedRelease? Release)
{
    public static MintReleaseResult Minted(MintedRelease release) =>
        new(MintReleaseOutcome.Minted, release);

    public static MintReleaseResult Failed(MintReleaseOutcome outcome) => new(outcome, null);
}

/// <param name="Identity">
/// Only the groups the grant covered. Every other group arrives empty, which is what a
/// scoped release means: not a whole identity with parts blanked, but the parts that were
/// asked for and nothing decrypted beyond them.
/// </param>
/// <param name="ScanId">The run the grant was minted for.</param>
/// <param name="BrokerId">The company the leg is addressed to.</param>
public sealed record RedeemedRelease(
    Guid ScanId,
    Guid BrokerId,
    IReadOnlyList<IdentityField> Fields,
    ProfileIdentityFields Identity);

/// <param name="Release">The grant, or <see langword="null"/> when it was refused.</param>
public sealed record RedeemReleaseResult(RedeemReleaseOutcome Outcome, RedeemedRelease? Release)
{
    public static RedeemReleaseResult Granted(RedeemedRelease release) =>
        new(RedeemReleaseOutcome.Granted, release);

    public static RedeemReleaseResult Refused() => new(RedeemReleaseOutcome.Refused, null);
}

/// <summary>
/// Writing down that one piece of work may see part of one identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minting opens nothing, and that is why it is its own interface.</b> It reads a run,
/// checks the company is one that run may ask, and writes a row holding a digest — every
/// one of those is the core store, and none of them is the vault. Only spending a grant
/// decrypts anything.
/// </para>
/// <para>
/// The two lived on one interface until something needed to plan work without being able
/// to open it. Sharing forced whoever wanted the harmless half to take a dependency on the
/// dangerous one — which for the process that fans a scan out across broker lanes would
/// have meant a vault connection and a key-manager token in the process that also drives
/// browsers against third-party sites, in order to write a row of random bytes. Splitting
/// them makes least privilege something the container can express rather than something a
/// comment asks for.
/// </para>
/// <para>
/// It takes no tenant and acts for the one its scope was established for, like every other
/// service here.
/// </para>
/// </remarks>
public interface IIdentityReleaseMinter
{
    /// <summary>
    /// Mints a grant for one broker's leg of one scan.
    /// </summary>
    /// <param name="fields">
    /// What the search declared it needs. The grant covers these and redeeming it
    /// decrypts nothing else — a field absent here has no path out of the vault for this
    /// piece of work.
    /// </param>
    Task<MintReleaseResult> MintAsync(
        Guid scanId,
        Guid brokerId,
        IReadOnlyCollection<IdentityField> fields,
        CancellationToken cancellationToken);
}

/// <summary>
/// Turning a grant into plaintext, once.
/// </summary>
/// <remarks>
/// The half that actually decrypts, and therefore the half that only exists in a process
/// holding the keys. It takes a token and not a scan, a profile or an account, because it
/// is called on behalf of something acting for nobody — the grant is what establishes who
/// the answer is about.
/// </remarks>
public interface IIdentityReleaseRedeemer
{
    /// <summary>
    /// Spends a grant and decrypts what it covers.
    /// </summary>
    /// <remarks>
    /// Single-use: the row is marked spent in the same statement that claims it, so two
    /// callers presenting one token do not both get an identity. A grant is refused
    /// rather than explained — see <see cref="RedeemReleaseOutcome"/>.
    /// </remarks>
    Task<RedeemReleaseResult> RedeemAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Both halves, for the one process that may do both.
/// </summary>
/// <remarks>
/// The process holding the keys mints as well — it is where a grant is spent, and where
/// anything asking for one on behalf of a request would ask. Nothing else should resolve
/// this: a process that can do both has no way to prove it only did one.
/// </remarks>
public interface IIdentityReleaseService : IIdentityReleaseMinter, IIdentityReleaseRedeemer;
