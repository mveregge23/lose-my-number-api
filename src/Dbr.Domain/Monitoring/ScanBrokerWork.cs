// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;

namespace Dbr.Domain.Monitoring;

/// <summary>
/// Ask this company what it holds about the identity this grant opens.
/// </summary>
/// <remarks>
/// <para>
/// The first thing to travel a broker's lane, and it is addressed to a company rather than
/// to an account: every tenant's leg for one broker queues behind every other tenant's, so
/// the company sees the pace its catalog row allows however many people are waiting.
/// </para>
/// <para>
/// <b>Nothing here is anybody's identity.</b> Four ids and a secret — and the ids are the
/// Internal tier, the same class as a status. What the work is actually about is behind
/// the grant, which is a token this message carries and cannot itself open: the process
/// that receives it has no keys, and has to present the token to a process that does.
/// </para>
/// <para>
/// <b>A grant in a queue is why grants expire quickly.</b> This message sits in its lane
/// until the company may next be spoken to, and for that whole time it is a decryption
/// right in a broker. The window is sized to the work rather than to the depth of the
/// queue, which means a leg that waited too long finds its grant refused — recorded as the
/// leg being over rather than retried, since the token is single-use and another attempt
/// at the same one would be refused for the same reason.
/// </para>
/// </remarks>
/// <param name="TenantId">
/// The account this leg acts for. On the message rather than resolved from the grant,
/// because the tenant is what the handler needs before it asks for anything — a scope has
/// to be acting for somebody before it can read the run or write a finding, and the grant
/// only resolves once, on the far side of the edge.
/// </param>
/// <param name="ReleaseToken">
/// The grant to present. Minted for this leg and this leg only, covering exactly the groups
/// of the identity the search declared it needs.
/// </param>
public sealed record ScanBrokerWork(
    Guid ScanId,
    Guid TenantId,
    Guid BrokerId,
    Guid PrivacyProfileId,
    string ReleaseToken,
    int AttemptNumber) : IBrokerScopedMessage
{
    /// <summary>
    /// Names the ids and withholds the grant.
    /// </summary>
    /// <remarks>
    /// A record prints every member it has, and one of these is a bearer credential for
    /// part of somebody's identity. The same refusal the identity types and the release
    /// request carry: a queue envelope, a retry log or an exception message is one
    /// interpolation away.
    /// </remarks>
    public override string ToString() =>
        $"ScanBrokerWork {{ ScanId = {ScanId}, TenantId = {TenantId}, BrokerId = {BrokerId}, "
        + $"PrivacyProfileId = {PrivacyProfileId}, AttemptNumber = {AttemptNumber}, "
        + "[withheld] }";
}
