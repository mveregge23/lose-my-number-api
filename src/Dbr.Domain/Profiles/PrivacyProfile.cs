// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Tenancy;

namespace Dbr.Domain.Profiles;

/// <summary>
/// One identity a tenant manages: the part of it that is safe to read on any ordinary
/// request.
/// </summary>
/// <remarks>
/// The identifying fields — names, addresses, contact details, date of birth — are not
/// here. They live encrypted in the vault store, under this row's id, and are reachable
/// only through the profile service. What remains is what scheduling and jurisdiction
/// resolution need in order to route work without ever asking for a decryption.
/// </remarks>
public class PrivacyProfile : ITenantScoped
{
    public Guid Id { get; init; }

    /// <summary>The account managing this identity.</summary>
    public Guid TenantId { get; init; }

    public required ProfileRelationship RelationshipType { get; init; }

    /// <summary>
    /// A coarse region code — <c>US-CA</c>, <c>EU</c> — or <see langword="null"/> when
    /// the tenant has not said.
    /// </summary>
    /// <remarks>
    /// Deliberately not encrypted and deliberately not precise. Working out which
    /// regimes govern a removal happens on every request, and routing that through a
    /// decryption in order to read two letters would put a standing need for vault
    /// access on the busiest path in the system. The database constrains the format so
    /// this cannot drift into being an address.
    /// </remarks>
    public string? ResidencyRegion { get; set; }

    /// <summary>Which attestation text was agreed to, and when.</summary>
    /// <remarks>
    /// A version rather than a boolean, the same way a consent grant records the policy
    /// it was granted under: when the wording changes, what somebody actually agreed to
    /// is still answerable.
    /// </remarks>
    public DateTimeOffset AttestedAt { get; init; }

    public required string AttestationVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
