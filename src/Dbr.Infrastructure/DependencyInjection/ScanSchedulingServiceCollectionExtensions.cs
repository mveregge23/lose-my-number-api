// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get recurring scans planned.
/// </summary>
/// <remarks>
/// Separate from <c>AddDbrMonitoring</c>, and not called by the API. Enumerating accounts
/// is the one thing in this system that reaches past the tenant boundary, and the process
/// serving requests has no business being able to do it — a registration it never uses is
/// still a capability sitting in the container.
/// </remarks>
public static class ScanSchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the account directory and the per-account runner.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and <c>AddDbrConsent</c> — the second because a
    /// scheduled scan checks permission the same way a requested one does, which is the
    /// case that check exists for.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The core connection string is absent, or the schedule cannot work as configured.
    /// </exception>
    public static IServiceCollection AddDbrScanScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ScanScheduleOptions();
        configuration.GetSection(ScanScheduleOptions.SectionName).Bind(options);
        options.Validate();

        var connectionString = configuration.GetConnectionString(
            InfrastructureServiceCollectionExtensions.CoreConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No core database connection string, so recurring scans cannot be planned. See "
                + "AddDbrPersistence for the setting; this reads the same one, through a role that "
                + "may do nothing but list account ids.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IAccountDirectory>(new AccountDirectory(connectionString));
        services.AddScoped<IScheduledScanRunner, ScheduledScanRunner>();

        return services;
    }
}
