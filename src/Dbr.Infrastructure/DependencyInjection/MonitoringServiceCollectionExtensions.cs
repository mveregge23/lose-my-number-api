// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Monitoring;
using Dbr.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to get scans and exposures.
/// </summary>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>Registers the scan and exposure services.</summary>
    /// <remarks>
    /// Requires <c>AddDbrPersistence</c> and <c>AddDbrConsent</c>. Deliberately not the
    /// vault or the key manager: deciding whether a scan may be queued, and for which
    /// profile, reads only core-store columns, so this path never acquires the ability to
    /// decrypt anything.
    /// </remarks>
    public static IServiceCollection AddDbrMonitoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IExposureService, ExposureService>();

        return services;
    }
}
