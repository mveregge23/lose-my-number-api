// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Profiles;

namespace Dbr.Integration.Tests.Fixtures;

/// <summary>
/// A connector that answers whatever a test told it to.
/// </summary>
/// <remarks>
/// The one thing that has to be a stand-in. Everything else about an attempt — the demand,
/// the catalog row, the lane, the tenant boundary, the two rows that move — is real,
/// because those are the parts whose behaviour is worth asserting. What a company's website
/// does when it is sent a deletion request is not something a test can have an opinion
/// about, so the connector is where the seam goes.
/// </remarks>
public sealed class StubBrokerConnector(
    ConnectorCapabilities capabilities,
    Func<ConnectorContext, ConnectorResult> answer)
    : IBrokerConnector
{
    /// <summary>The context it was last given, so a test can assert what it was handed.</summary>
    public ConnectorContext? LastContext { get; private set; }

    public ConnectorCapabilities Capabilities { get; } = capabilities;

    public Task<ConnectorResult> ExecuteAsync(
        ConnectorContext context,
        CancellationToken cancellationToken)
    {
        LastContext = context;

        return Task.FromResult(answer(context));
    }

    /// <summary>A mailbox connector that needs a name and answers however a test says.</summary>
    public static StubBrokerConnector Answering(ConnectorResult result) =>
        Answering(_ => result);

    /// <summary>A mailbox connector whose answer depends on what it was handed.</summary>
    public static StubBrokerConnector Answering(Func<ConnectorContext, ConnectorResult> answer) =>
        new(
            new ConnectorCapabilities(
                ConnectorKind.Recipe,
                RemovalMethod.Email,
                new HashSet<IdentityField> { IdentityField.Names }),
            answer);

    /// <summary>A connector that acts by a means the company does not accept.</summary>
    public static StubBrokerConnector ForMethod(RemovalMethod method) =>
        new(
            new ConnectorCapabilities(
                ConnectorKind.Recipe,
                method,
                new HashSet<IdentityField> { IdentityField.Names }),
            _ => new ConnectorResult.AlreadyClear());

    /// <summary>
    /// A connector that declares it needs no part of an identity.
    /// </summary>
    /// <remarks>
    /// The contract refuses one of these when it is handed a context, but the dispatcher
    /// mints before any context exists — so this is what reaches the release path and is
    /// turned away there.
    /// </remarks>
    public static StubBrokerConnector NeedingNothing() =>
        new(
            new ConnectorCapabilities(
                ConnectorKind.Recipe,
                RemovalMethod.Email,
                new HashSet<IdentityField>()),
            _ => new ConnectorResult.AlreadyClear());

    /// <summary>A connector that throws rather than answering.</summary>
    public static StubBrokerConnector Throwing() =>
        Answering(_ => throw new InvalidOperationException("the connector is broken"));
}

/// <summary>A registry holding whatever a test put in it.</summary>
public sealed class StubBrokerConnectorRegistry : IBrokerConnectorRegistry
{
    private readonly Dictionary<Guid, ConnectorRegistration> _connectors = [];

    public ConnectorRegistration? Find(Guid brokerId) =>
        _connectors.TryGetValue(brokerId, out var registration) ? registration : null;

    public StubBrokerConnectorRegistry With(
        Guid brokerId,
        IBrokerConnector connector,
        string connectorId = "stub-connector")
    {
        _connectors[brokerId] = new ConnectorRegistration(connectorId, connector);

        return this;
    }
}
