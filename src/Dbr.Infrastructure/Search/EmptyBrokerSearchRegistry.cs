// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Search;

namespace Dbr.Infrastructure.Search;

/// <summary>
/// A build that knows how to search nobody.
/// </summary>
/// <remarks>
/// <para>
/// The honest state of things until searches are written, and it is deliberately a
/// registration rather than an absent one. A dispatcher resolving nothing would have to
/// decide what an unresolvable dependency means; a dispatcher resolving this gets the same
/// answer it will get for most of the catalog for a long time — no search for that
/// company — and every scan runs end to end, plans its legs and settles, recording exactly
/// which companies could not be asked.
/// </para>
/// <para>
/// What replaces it does not change a line of the dispatcher or the handler. That is the
/// point of the registry being one method wide: the first real one arrives as a
/// registration.
/// </para>
/// </remarks>
public sealed class EmptyBrokerSearchRegistry : IBrokerSearchRegistry
{
    public IBrokerSearch? Find(Guid brokerId) => null;
}
