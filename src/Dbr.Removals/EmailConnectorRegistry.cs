// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Connectors;

namespace Dbr.Removals;

/// <summary>
/// Which companies this build knows how to ask by mail, and what runs for each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built once, from documents read once</b>, for the reason the search registry is: these
/// arrive with a deploy, and a deploy restarts the worker. Reading them per demand would mean
/// a file changing under a running process, which is how two attempts at one demand come to
/// be worded differently.
/// </para>
/// <para>
/// <b>The connector id names the engine and the company it was configured for.</b> One engine
/// runs the wording for every company with a mailbox, so an engine reporting only its own name
/// would record every mail-driven attempt identically — and the question somebody asks after a
/// company starts refusing is which entry was being used.
/// </para>
/// <para>
/// It answers nothing for a company it has no recipe for, which is most of the catalog and is
/// not a failure: a company that can be found but not yet asked is the honest state of things,
/// and the demand stays queued rather than being recorded as attempted.
/// </para>
/// </remarks>
public sealed class EmailConnectorRegistry : IBrokerConnectorRegistry
{
    /// <summary>What every registration from this engine is named after.</summary>
    public const string EnginePrefix = "email";

    private readonly IReadOnlyDictionary<Guid, ConnectorRegistration> _connectors;

    public EmailConnectorRegistry(
        IEnumerable<EmailRecipe> recipes,
        Func<EmailRecipe, IBrokerConnector> engine)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(engine);

        _connectors = recipes.ToDictionary(
            recipe => recipe.BrokerId,
            recipe => new ConnectorRegistration(
                $"{EnginePrefix}.{recipe.Mailbox.ToLowerInvariant()}",
                engine(recipe)));
    }

    /// <summary>How many companies this build can ask by mail.</summary>
    /// <remarks>
    /// For the line a composition root logs at startup. "Asking 4 of 412 companies by mail" is
    /// the difference between nothing being sent because nobody is listed and nothing being
    /// sent because almost nobody can be asked, and that is worth saying once a process starts
    /// rather than reconstructing from attempt rows afterwards.
    /// </remarks>
    public int Count => _connectors.Count;

    public ConnectorRegistration? Find(Guid brokerId) =>
        _connectors.TryGetValue(brokerId, out var registration) ? registration : null;
}
