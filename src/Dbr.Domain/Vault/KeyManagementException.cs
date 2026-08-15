// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Vault;

/// <summary>
/// The key manager could not do what was asked.
/// </summary>
/// <remarks>
/// An exception rather than a result type, because every one of these is a failure of
/// infrastructure rather than an outcome a caller chooses between. A key that will not
/// unwrap is not a decision point — it means data that cannot be read, and the only
/// honest response is to stop rather than to continue with nothing.
/// </remarks>
public sealed class KeyManagementException(string message) : Exception(message);
