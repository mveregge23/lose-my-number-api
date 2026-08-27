// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Messaging;
using MassTransit;

namespace Dbr.Infrastructure.Messaging.MassTransitBus;

/// <summary>
/// The one place a handler meets the library that delivered its message.
/// </summary>
/// <remarks>
/// Everything a consumer would otherwise take from <see cref="ConsumeContext{T}"/> stops
/// here: the handler gets the work and a cancellation token. That is what keeps the two
/// kinds of broker work — asking a company what it holds, and telling it to stop — free of
/// any knowledge of what carried the request, and it is why replacing the transport is a
/// new file in this folder rather than a change to either of them.
/// </remarks>
public sealed class BrokerWorkConsumer<TWork>(IBrokerWorkHandler<TWork> handler) : IConsumer<TWork>
    where TWork : class, IBrokerScopedMessage
{
    public async Task Consume(ConsumeContext<TWork> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Not caught. A handler that failed did not do the work, and what follows — a
        // retry, a dead letter — is the transport's decision to make. Swallowing it here
        // would acknowledge a message that was never handled.
        await handler.HandleAsync(context.Message, context.CancellationToken).ConfigureAwait(false);
    }
}
