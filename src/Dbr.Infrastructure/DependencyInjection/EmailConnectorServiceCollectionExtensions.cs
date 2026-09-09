// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;
using Dbr.Domain.Mail;
using Dbr.Removals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to be able to ask companies that offer only a mailbox.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <see cref="RemovalDispatchServiceCollectionExtensions.AddDbrConnectorRegistry"/>,
/// which registers an empty registry so that a build with no connectors leaves demands queued
/// rather than sending them somewhere wrong. This is the first thing that fills it.
/// </para>
/// <para>
/// <b>The content is read here and a bad document stops the process.</b> Startup is the only
/// place it can usefully fail. Wording discovered to be malformed when an attempt runs is a
/// company already being sent something built out of it — and the alternative to stopping is
/// a worker that quietly asks fewer companies than its catalog says it can, on behalf of
/// people who were told their removal was in progress.
/// </para>
/// </remarks>
public static class EmailConnectorServiceCollectionExtensions
{
    /// <exception cref="InvalidOperationException">
    /// A mailbox or a piece of wording cannot be used as written.
    /// </exception>
    public static IServiceCollection AddDbrEmailConnectors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var read = DemandCatalogReader.Read();

        if (read.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The demand mailboxes and wording compiled into this build cannot be used as "
                + "written:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, read.Problems.Select(problem => "  " + problem)));
        }

        // One engine per company, shared across every tenant's demands — it holds a recipe,
        // the wording and a relay, and nothing about a person. TryAdd, so a build that has
        // registered connectors of its own keeps them, which is how a test puts one in.
        services.TryAddSingleton<IBrokerConnectorRegistry>(provider =>
            new EmailConnectorRegistry(
                read.Recipes,
                recipe => new TemplatedEmailConnector(
                    recipe,
                    read.Templates,
                    provider.GetRequiredService<IMailSender>(),
                    provider.GetRequiredService<IJobMailboxes>())));

        return services;
    }
}
