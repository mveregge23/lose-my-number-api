// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Consent;

/// <summary>
/// One thing a tenant can permit the system to do on their behalf.
/// </summary>
/// <remarks>
/// Three permissions rather than one checkbox, because they are genuinely different
/// asks. Looking somebody up is a search nobody else sees; opening a removal request
/// puts their name and address in front of a broker in a message sent as them. Somebody
/// who wants the first without the second has to be able to say so, and a single "I
/// agree" that covered both would make the difference unexpressible.
/// </remarks>
public enum ConsentScope
{
    /// <summary>Search brokers for this tenant's identities.</summary>
    Scan,

    /// <summary>Open removal requests for what a scan finds.</summary>
    AutoRemoval,

    /// <summary>Open a removal request again when removed data reappears.</summary>
    AutoResubmit,
}
