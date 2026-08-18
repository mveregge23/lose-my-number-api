// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Consent;

/// <summary>
/// Where one permission stands right now.
/// </summary>
/// <remarks>
/// A scope nobody has ever decided about is not missing from a list of these — it is
/// present and not granted, with nothing to say when or under what. A client rendering
/// three switches needs three answers, and leaving one out would make "never asked"
/// look like a gap in the response rather than an answer.
/// </remarks>
/// <param name="Since">
/// When the decision in force was made, or <see langword="null"/> if there has not been
/// one.
/// </param>
/// <param name="PolicyVersion">
/// Which consent text that decision was made under, or <see langword="null"/> if there
/// has not been one. Not necessarily the version this instance serves today — that is
/// the point of keeping it.
/// </param>
public sealed record ConsentGrant(
    ConsentScope Scope,
    bool Granted,
    DateTimeOffset? Since,
    string? PolicyVersion)
{
    /// <summary>A scope nobody has decided about yet.</summary>
    public static ConsentGrant Undecided(ConsentScope scope) => new(scope, false, null, null);
}

/// <summary>How an attempt to record a decision ended.</summary>
public enum RecordConsentOutcome
{
    /// <summary>Written down as a new decision.</summary>
    Recorded,

    /// <summary>
    /// Already what the tenant had decided, under this same policy version, so nothing
    /// was written.
    /// </summary>
    /// <remarks>
    /// A retry, a double-tapped switch, or a client saving a form it did not change. The
    /// history is a list of decisions somebody actually made, and filling it with rows
    /// that changed nothing would bury the ones that did.
    /// </remarks>
    Unchanged,

    /// <summary>
    /// The version the client says it displayed is not the one this instance serves.
    /// </summary>
    /// <remarks>
    /// Refused rather than stored. A record of what a client claimed answers nothing
    /// later; a record of what somebody was actually shown is the only kind worth
    /// keeping.
    /// </remarks>
    PolicyOutOfDate,
}

/// <param name="Grant">
/// Where the scope stands after this, or <see langword="null"/> when nothing was
/// recorded because the version did not match.
/// </param>
public sealed record RecordConsentResult(RecordConsentOutcome Outcome, ConsentGrant? Grant)
{
    public static RecordConsentResult Failed(RecordConsentOutcome outcome) => new(outcome, null);
}
