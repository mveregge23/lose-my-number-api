// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;
using Dbr.Infrastructure.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get a way to send mail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing a caller passes here names a mail vendor</b>, for the same reason the queue's
/// registration names no bus: the relay is the operator's, and what the application depends
/// on is the ability to hand over a message. An instance that would rather send through a
/// hosted API replaces one registration and nothing above this line changes — which is what
/// keeps that from being a decision compiled in on everyone's behalf.
/// </para>
/// <para>
/// Settings are validated here rather than at first send. A relay hostname that was never
/// configured produces a demand that fails at the moment somebody is waiting on it, and it
/// fails for every company at once — startup is where that belongs.
/// </para>
/// </remarks>
public static class MailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SMTP sender and the return addresses demands are answered at.
    /// </summary>
    /// <exception cref="InvalidOperationException">Mail is not configured.</exception>
    public static IServiceCollection AddDbrMail(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new MailOptions();
        configuration.GetSection(MailOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IJobMailboxes, JobMailboxes>();
        services.AddSingleton<IMailSender, SmtpMailSender>();

        return services;
    }
}
