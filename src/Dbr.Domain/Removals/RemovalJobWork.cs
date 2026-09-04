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
/// <b>A grant in a queue is why grants expire quickly.</b> This message sits in its lane
/// until the company may next be spoken to, and for that whole time it is a decryption
/// right in a broker. The window is sized to the work rather than to the depth of the
/// queue, so an attempt that waited too long finds its grant refused — recorded as the
/// attempt being over rather than retried, since the token is single-use and another go at
/// the same one would be refused for the same reason.
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
/// <param name="ReleaseToken">
/// The grant to present. Minted for this attempt and this attempt only, covering exactly
/// the groups the connector declared it needs.
/// </param>
public sealed record RemovalJobWork(
    Guid RemovalRequestId,
    Guid RemovalJobId,
    Guid TenantId,
    Guid BrokerId,
    Guid PrivacyProfileId,
    int AttemptNumber,
    string ReleaseToken) : IBrokerScopedMessage
{
    /// <summary>
    /// Names the ids and withholds the grant.
    /// </summary>
    /// <remarks>
    /// A record prints every member it has, and one of these is a bearer credential for part
    /// of somebody's identity. The same refusal the identity types and the scan side's work
    /// carry: a queue envelope, a retry log or an exception message is one interpolation
    /// away.
    /// </remarks>
    public override string ToString() =>
        $"RemovalJobWork {{ RemovalRequestId = {RemovalRequestId}, "
        + $"RemovalJobId = {RemovalJobId}, TenantId = {TenantId}, BrokerId = {BrokerId}, "
        + $"PrivacyProfileId = {PrivacyProfileId}, AttemptNumber = {AttemptNumber}, "
        + "[withheld] }";
}
