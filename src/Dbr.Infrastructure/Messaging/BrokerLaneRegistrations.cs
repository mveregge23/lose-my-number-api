// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;

namespace Dbr.Infrastructure.Messaging;

/// <param name="Work">The message type this lane carries.</param>
/// <param name="Handler">What runs it.</param>
public sealed record BrokerWorkRegistration(Type Work, Type Handler);

/// <summary>
/// What runs in every broker's lane.
/// </summary>
/// <remarks>
/// <para>
/// Named in terms of the work and the handler, and never in terms of whatever library
/// carries the message. This is the surface a composition root touches, so it is the one
/// that would otherwise spread a vendor's types through every process that wants to do
/// something with a broker — which is exactly what §1 asks to be kept behind one
/// interface.
/// </para>
/// <para>
/// Handlers are listed rather than discovered by scanning. A handler that ended up in a
/// per-broker lane by accident is being paced by a rule that has nothing to do with it,
/// and one that missed the lane talks to a broker at whatever speed it likes. Both
/// failures are invisible until a company complains.
/// </para>
/// </remarks>
public sealed class BrokerLaneRegistrations
{
    private readonly List<BrokerWorkRegistration> _work = [];

    /// <summary>Everything registered to run in the lanes.</summary>
    public IReadOnlyList<BrokerWorkRegistration> Work => _work;

    /// <summary>Runs <typeparamref name="THandler"/> against this kind of work.</summary>
    public BrokerLaneRegistrations Handle<TWork, THandler>()
        where TWork : class, IBrokerScopedMessage
        where THandler : class, IBrokerWorkHandler<TWork>
    {
        _work.Add(new BrokerWorkRegistration(typeof(TWork), typeof(THandler)));

        return this;
    }
}
