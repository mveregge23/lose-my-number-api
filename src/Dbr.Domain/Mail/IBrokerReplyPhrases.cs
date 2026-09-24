// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Mail;

/// <summary>
/// How each company this build knows about talks, for reading its answers.
/// </summary>
/// <remarks>
/// A lookup rather than a field on the attempt, because phrases arrive with a deploy and an
/// attempt may be months old: a reply read today should be read against what the catalog
/// says now, not against whatever it said when the demand went out. It is the same reason
/// the mailbox itself is resolved per attempt rather than stored on one.
/// </remarks>
public interface IBrokerReplyPhrases
{
    /// <summary>
    /// What this company's answers are recognised by, or nothing when nobody has said.
    /// </summary>
    /// <remarks>
    /// Nothing is the ordinary answer and stays that way for most companies. It means every
    /// reply from that company is read by a person, which is the right default for a
    /// judgement nobody has written down yet.
    /// </remarks>
    ReplyPhrases For(Guid brokerId);
}

/// <summary>
/// The lookup for a build where nobody has written down how any company talks.
/// </summary>
/// <remarks>
/// A named type rather than a lambda so that a container dump says what it is: a deployment
/// whose replies all read as unclear should be able to see why by looking at what is
/// registered. The same arrangement, and the same reason, as the empty connector registry.
/// </remarks>
public sealed class NoDeclaredReplyPhrases : IBrokerReplyPhrases
{
    public ReplyPhrases For(Guid brokerId) => ReplyPhrases.None;
}
