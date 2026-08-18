// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Consent;

/// <summary>
/// Which version of the consent text a decision is currently recorded against.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the terms accepted at signup, and deliberately so. They are two
/// documents that move independently: rewording what the system may do on somebody's
/// behalf should not invalidate the attestation that the identity in their profile is
/// their own, and vice versa. Sharing one setting would tie every change in either
/// document to the other.
/// </para>
/// <para>
/// As with the terms, there is no default. An instance that cannot say which consent
/// text it serves cannot record a meaningful agreement to it, and a built-in value would
/// stamp every decision with a version naming text that does not exist.
/// </para>
/// </remarks>
public sealed class ConsentPolicyOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Consent";

    /// <summary>
    /// The longest a version may be. Generous for a date or a semantic version, short
    /// enough that the policy itself cannot be pasted in here.
    /// </summary>
    public const int MaxVersionLength = 64;

    /// <summary>
    /// The version a consent decision is recorded against, such as <c>2026-06-01</c>.
    /// </summary>
    /// <remarks>
    /// The format is the operator's to choose — a date, a semantic version, a commit —
    /// because nothing here parses it. It is compared for equality against what the
    /// client says it displayed, and stored verbatim.
    /// </remarks>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>
    /// Fails startup on a value no consent decision could work with, rather than at the
    /// first attempt to change one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PolicyVersion))
        {
            throw new InvalidOperationException(
                $"{SectionName}:PolicyVersion is required — it is the version of the consent text "
                + "every grant and revocation is recorded against. Set it to whatever names the "
                + "text this instance actually serves.");
        }

        if (PolicyVersion.Length > MaxVersionLength)
        {
            throw new InvalidOperationException(
                $"{SectionName}:PolicyVersion is {PolicyVersion.Length} characters, longer than the "
                + $"{MaxVersionLength} allowed. This names the policy; it is not the policy.");
        }

        // A client has to send this value back exactly, so surrounding whitespace would
        // be a setting that works everywhere except where somebody typed the version
        // they could see.
        if (PolicyVersion != PolicyVersion.Trim())
        {
            throw new InvalidOperationException(
                $"{SectionName}:PolicyVersion has whitespace around it ('{PolicyVersion}'). A "
                + "consent decision compares what the client accepted against this exactly, so the "
                + "spaces would have to be typed as well.");
        }
    }
}
