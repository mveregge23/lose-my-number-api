// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Profiles;

/// <summary>
/// A profile was written by somebody else between this change reading it and saving it.
/// </summary>
/// <remarks>
/// <para>
/// Every edit to an identity is a read-modify-write: the fields are encrypted as a whole
/// under a key that is replaced on each save, so changing one address means decrypting
/// everything, altering the part that changed, and writing it all back. Two edits
/// overlapping would therefore not merge — the second would write what it read, and
/// whatever the first added would be gone with no error anywhere.
/// </para>
/// <para>
/// Losing an address that way is worse than it sounds: an address somebody lived at
/// years ago is often the only reason a broker listing can be found at all, and nothing
/// about the profile afterwards would suggest one was ever there. So the second write is
/// refused instead, and the caller re-reads.
/// </para>
/// </remarks>
public sealed class ProfileChangedException : Exception
{
    public ProfileChangedException()
        : base("This profile was changed by another request. Read it again and reapply the change.")
    {
    }

    public ProfileChangedException(string message)
        : base(message)
    {
    }

    public ProfileChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
