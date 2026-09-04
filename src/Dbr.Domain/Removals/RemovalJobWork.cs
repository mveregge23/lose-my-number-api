// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;

namespace Dbr.Domain.Removals;

/// <summary>
/// Make this demand of this company.
/// </summary>
/// <remarks>
/// <para>
/// The second thing to travel a broker's lane, and the one the lanes were built for. It
/// queues behind every other account's work for the same company, which is the whole point:
/// a company sees the pace its catalog row allows however many people are waiting, and
/// asking it what it holds and telling it to stop holding it share that budget rather than
/// each getting one.
/// </para>
/// <para>
/// <b>Nothing here is anybody's identity</b> — five ids and a count, all of them the same
/// class as a status.
/// </para>
/// <para>
/// <b>And nothing here is a grant, which is the difference from the scan side.</b> A scan's
/// work carries a release token because a search cannot ask a company anything without a
/// name to ask about. A removal needs one just as badly and there is no way to mint it: the
/// release is scoped to a scan by its own schema, and widening it to an attempt is its own
/// story. So this message carries the attempt and not the means to open it, and the handler
/// runs the connector with whatever it was released — which today is nothing. That is
/// recorded on the attempt rather than hidden: a connector handed no identity answers that
/// it cannot work, which is the correct answer and the honest one.
/// </para>
/// </remarks>
/// <param name="TenantId">
/// The account this attempt acts for. On the message because the handler needs a tenant
/// before it reads anything — a scope has to be acting for somebody before it can find the
/// attempt it is about to run.
/// </param>
/// <param name="AttemptNumber">
/// Which try this is, from one. The same number the attempt row carries, so a redelivered
/// message resolves to the attempt it was sent for rather than to whichever is latest.
/// </param>
public sealed record RemovalJobWork(
    Guid RemovalRequestId,
    Guid RemovalJobId,
    Guid TenantId,
    Guid BrokerId,
    Guid PrivacyProfileId,
    int AttemptNumber) : IBrokerScopedMessage;
