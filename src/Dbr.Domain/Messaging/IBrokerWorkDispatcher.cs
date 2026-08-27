// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Messaging;

/// <summary>
/// Puts work into a broker's lane.
/// </summary>
/// <remarks>
/// <para>
/// One method, and it takes the work rather than a destination: the lane is derived from
/// the message's own <see cref="IBrokerScopedMessage.BrokerId"/>. A caller that could name
/// a queue could name the wrong one, and pacing that can be addressed around is not
/// pacing — the same reasoning that keeps a tenant out of the service signatures elsewhere
/// in this codebase.
/// </para>
/// <para>
/// This exists so that nothing outside one folder knows which library moves the message.
/// §1 asks for exactly that: where a vendor's software is used it sits behind an interface
/// the core never bypasses, so replacing it is a registration change rather than a
/// rewrite. It is not hypothetical here — the library this is first implemented over
/// changed its licence one major version after the one pinned.
/// </para>
/// </remarks>
public interface IBrokerWorkDispatcher
{
    /// <summary>Queues one piece of work in its broker's lane.</summary>
    Task DispatchAsync<TWork>(TWork work, CancellationToken cancellationToken)
        where TWork : class, IBrokerScopedMessage;
}
