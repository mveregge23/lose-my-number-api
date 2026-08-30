// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Vault;
using Dbr.Infrastructure.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dbr.Infrastructure.DependencyInjection;

/// <summary>
/// What a composition root calls to be able to hand out scoped access to an identity.
/// </summary>
/// <remarks>
/// Two entry points, because the two halves of a release need different privileges.
/// <see cref="AddDbrReleaseMinting"/> writes down that a piece of work may see part of an
/// identity and can open nothing; <see cref="AddDbrIdentityReleases"/> adds the half that
/// decrypts, and only a process already holding the keys should call it. That dependency
/// is enforced by what the service needs rather than by a comment — <c>AddDbrVault</c> is
/// what supplies the profile service behind it, and a composition root without it fails to
/// build the container.
/// </remarks>
public static class IdentityReleaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers minting and redeeming against the <c>IdentityRelease</c> configuration
    /// section.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The lifetime cannot work. Failing at startup rather than at the moment a grant is
    /// minted: a zero lifetime refuses every redemption and looks like a broken worker,
    /// and an over-long one is standing access nobody notices.
    /// </exception>
    public static IServiceCollection AddDbrIdentityReleases(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbrReleaseMinting(configuration);

        services.AddScoped<IdentityReleaseLookup>();

        // Both halves resolve to the one class that can do both. A process holding the keys
        // mints as well — it is where a grant is spent, and where anything asking for one
        // on behalf of a request would ask — so there is no honest way for it to claim it
        // only minted.
        services.AddScoped<IIdentityReleaseService, IdentityReleaseService>();
        services.AddScoped<IIdentityReleaseRedeemer>(
            provider => provider.GetRequiredService<IIdentityReleaseService>());

        return services;
    }

    /// <summary>
    /// Registers minting on its own, for a process that plans work and cannot open it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The point of it is what it does not register.</b> Minting reads a run, checks
    /// that a company is one that run may ask, and writes a row holding a digest —
    /// core-store work throughout. So the process that fans a scan out across broker lanes
    /// can plan the work without a vault connection or a key-manager token, which matters
    /// because that process is also the one driving browsers against sites with no
    /// interest in being read.
    /// </para>
    /// <para>
    /// Requires <c>AddDbrPersistence</c>, and nothing else.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The grant lifetime cannot work.</exception>
    public static IServiceCollection AddDbrReleaseMinting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new IdentityReleaseOptions();
        configuration.GetSection(IdentityReleaseOptions.SectionName).Bind(options);
        options.Validate();

        // First call wins, so a process that registers both halves reads the section once
        // and cannot end up with two lifetimes depending on which order it called them in.
        services.TryAddSingleton(options);

        // How long a grant lives is the whole of what makes it short-lived, so this path
        // needs a clock it can be tested against. Registered only if nothing else has — a
        // host that brought its own, as the worker does, should keep it rather than have
        // one call quietly replace what the rest of the process reads the time from.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<IIdentityReleaseMinter, IdentityReleaseMinter>();

        return services;
    }
}
