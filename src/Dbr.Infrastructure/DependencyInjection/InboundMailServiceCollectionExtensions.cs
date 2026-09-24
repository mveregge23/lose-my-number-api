// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Domain.Removals;
using Dbr.Infrastructure.Mail;
using Dbr.Infrastructure.Removals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What the worker calls to be able to read the answers to its demands.
/// </summary>
/// <remarks>
/// <para>
/// Registered in the worker and not in the API, for the reason every other half of the
/// removal pipeline is: the API accepts a request and puts it in a lane, and everything
/// after that — including hearing back — happens in the process that does the work.
/// </para>
/// <para>
/// <b>Which adapter is a matter of which provider is configured</b>, and deliberately not
/// of a deployment mode. A mode bundles assumptions that come apart in practice: somebody
/// will run this for other people while reading an ordinary mailbox, and somebody else
/// will self-host with a domain of their own. One setting naming the way replies arrive
/// says the thing that is actually true, and is the same shape the outbound relay is
/// already chosen by.
/// </para>
/// <para>
/// <b>Validated at startup, which is where a mailbox nobody configured belongs.</b> The
/// alternative is a process that starts cleanly, polls nothing, files nothing, and leaves
/// every demand reading as unanswered — a state indistinguishable from companies that
/// have not replied, which is the exact confusion this capability exists to end.
/// </para>
/// </remarks>
public static class InboundMailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the source replies are read from, the directory that resolves them, and
    /// the filer that writes them down.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c>: filing a reply is an ordinary write inside the
    /// tenant boundary, and resolving one is a narrow read through the scheduler role
    /// against the same database.
    /// </remarks>
    /// <returns>Whether this deployment reads replies at all.</returns>
    /// <exception cref="InvalidOperationException">
    /// The settings cannot work as given, or the core connection string is absent.
    /// </exception>
    public static bool AddDbrInboundMail(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new InboundMailOptions();
        configuration.GetSection(InboundMailOptions.SectionName).Bind(options);
        options.Validate();

        if (!options.Enabled)
        {
            return false;
        }

        var connectionString = configuration.GetConnectionString(
            InfrastructureServiceCollectionExtensions.CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No core database connection string, so an arriving reply cannot be tied to "
                + "the attempt it answers. See AddDbrPersistence for the setting; this reads "
                + "the same one, through a role that may do nothing but name the attempt a "
                + "reply belongs to.");
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IInboundMailSource, ImapMailSource>();
        services.AddSingleton<IAnsweredDemandDirectory>(
            new AnsweredDemandDirectory(connectionString));

        services.AddScoped<IReplyFiler, ReplyFiler>();

        return true;
    }
}
