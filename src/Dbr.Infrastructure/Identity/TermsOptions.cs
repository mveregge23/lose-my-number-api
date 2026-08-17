// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Infrastructure.Identity;

/// <summary>
/// Which version of the terms an account is currently opened under.
/// </summary>
/// <remarks>
/// <para>
/// This is a label for a document, not the document. What it buys is that the
/// attestation recorded against a profile answers a real question later — which text
/// did this person agree to — instead of recording that somebody agreed to something.
/// The same pattern a consent grant uses for the policy it was granted under.
/// </para>
/// <para>
/// There is no default. An instance that has not said which terms it serves cannot
/// record a meaningful acceptance of them, and a built-in value would quietly stamp
/// every account with a version naming text that does not exist.
/// </para>
/// </remarks>
public sealed class TermsOptions
{
    /// <summary>The configuration section these are read from.</summary>
    public const string SectionName = "Terms";

    /// <summary>
    /// The longest a version may be. Generous for a date or a semantic version, short
    /// enough that the terms themselves cannot be pasted in here.
    /// </summary>
    public const int MaxVersionLength = 64;

    /// <summary>
    /// The version signup requires acceptance of, such as <c>2026-06-01</c>.
    /// </summary>
    /// <remarks>
    /// The format is the operator's to choose — a date, a semantic version, a commit —
    /// because nothing here parses it. It is compared for equality against what the
    /// client says it displayed, and stored verbatim.
    /// </remarks>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// Fails startup on a value no signup could work with, rather than at the first
    /// attempt to open an account.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings cannot work as given.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CurrentVersion))
        {
            throw new InvalidOperationException(
                $"{SectionName}:CurrentVersion is required — it is the version of the terms an "
                + "account is opened under, and it is recorded against the profile signup creates. "
                + "Set it to whatever names the text this instance actually serves.");
        }

        if (CurrentVersion.Length > MaxVersionLength)
        {
            throw new InvalidOperationException(
                $"{SectionName}:CurrentVersion is {CurrentVersion.Length} characters, longer than "
                + $"the {MaxVersionLength} allowed. This names the terms; it is not the terms.");
        }

        // A client has to send this value back exactly, so surrounding whitespace would
        // be a setting that works everywhere except where somebody typed the version
        // they could see.
        if (CurrentVersion != CurrentVersion.Trim())
        {
            throw new InvalidOperationException(
                $"{SectionName}:CurrentVersion has whitespace around it ('{CurrentVersion}'). "
                + "Signup compares what the client accepted against this exactly, so the spaces "
                + "would have to be typed as well.");
        }
    }
}
