// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Domain.Removals;

/// <summary>What happened to a reply that was offered for filing.</summary>
public enum ReplyFiling
{
    /// <summary>It was written against the attempt it answers.</summary>
    Filed,

    /// <summary>It had already been written, and nothing changed.</summary>
    AlreadyFiled,
}

/// <summary>
/// Writes a reply against the attempt it answers, acting for that attempt's account.
/// </summary>
/// <remarks>
/// Separate from the directory that resolved it, because they act as different roles and
/// the split is the point: finding out whose reply this is reaches across accounts and is
/// one statement wide, and writing it happens inside the boundary as the account itself.
/// </remarks>
public interface IReplyFiler
{
    /// <summary>Files one reply against one attempt.</summary>
    /// <remarks>
    /// Tolerating a message seen twice is part of the contract rather than a caller's
    /// problem: a source offers a reply until it is acknowledged, so a process that files
    /// one and stops before acknowledging will be offered it again, and filing it twice
    /// would make one answer look like two.
    /// </remarks>
    Task<ReplyFiling> FileAsync(
        InboundMessage message,
        AnsweredDemandMatch match,
        CancellationToken cancellationToken);
}
