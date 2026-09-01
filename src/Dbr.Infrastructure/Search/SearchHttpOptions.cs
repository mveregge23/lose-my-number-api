// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Search;

/// <summary>
/// How this instance introduces itself to a company it is reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first outbound traffic this system has ever produced, so the first time it has to
/// say who it is.</b> The legitimate-crawler convention is a User-Agent that names the
/// software and links somewhere explaining what it is doing — which costs nothing and is the
/// difference between a company's operator finding an explanation and finding an unidentified
/// scraper. This service exists to make requests of companies on somebody's behalf; arriving
/// anonymously would be at odds with the whole point of it.
/// </para>
/// <para>
/// <b>Overridable, because a self-hosted instance is not this one.</b> Somebody running their
/// own copy should be able to point the link at themselves, and a hosted deployment reached
/// through the default would be one whose operator cannot be found.
/// </para>
/// </remarks>
public sealed class SearchHttpOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Search";

    /// <summary>The default introduction, naming the project and where to read about it.</summary>
    public const string DefaultUserAgent =
        "LoseMyNumber/1.0 (+https://github.com/mveregge23/lose-my-number-api)";

    /// <summary>What this instance calls itself when it reads a company's site.</summary>
    public string UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>
    /// How long one request is given.
    /// </summary>
    /// <remarks>
    /// Short, and short on purpose. A leg holds a decryption grant while it runs, and every
    /// second of a hanging request is a second that grant is open — so a company that has
    /// stopped answering should produce a transient failure quickly rather than pin a lane at
    /// its concurrency limit waiting.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 20;

    /// <exception cref="InvalidOperationException">The settings cannot be used as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UserAgent))
        {
            throw new InvalidOperationException(
                $"{SectionName}:UserAgent is empty. This service reads other people's sites on "
                + "somebody's behalf, and arriving without saying what it is would be both rude "
                + "and the fastest way to be blocked.");
        }

        if (TimeoutSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeoutSeconds must be between 1 and 120, and is "
                + $"{TimeoutSeconds}. A leg holds a decryption grant while it waits, so a long "
                + "timeout is a long-lived open grant as well as a stalled lane.");
        }
    }
}
