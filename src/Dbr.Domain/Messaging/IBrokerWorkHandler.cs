// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Messaging;

/// <summary>
/// Something that does one piece of a broker's work when its lane allows.
/// </summary>
/// <remarks>
/// <para>
/// The whole contract a handler needs, and deliberately nothing else: a piece of work and
/// a cancellation token. No message context, no acknowledgement, no retry policy, no
/// transport type of any kind. Asking a broker what it holds and telling it to stop are
/// the two things that will implement this, and neither should have to know what carried
/// the request to it.
/// </para>
/// <para>
/// <b>Throwing means the work did not happen.</b> Whatever is carrying the message decides
/// what follows — a retry, a dead letter, a log — and that decision belongs to the
/// transport rather than here. A handler that swallowed its own failure would report
/// success to a queue that would then never try again.
/// </para>
/// </remarks>
public interface IBrokerWorkHandler<in TWork>
    where TWork : IBrokerScopedMessage
{
    Task HandleAsync(TWork work, CancellationToken cancellationToken);
}
