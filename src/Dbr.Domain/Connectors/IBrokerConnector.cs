// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Profiles;

namespace Dbr.Domain.Connectors;

/// <summary>Which review bar an implementation was held to.</summary>
/// <remarks>
/// The same distinction the search side draws, and it matters more here. A recipe is a
/// document a generic engine interprets — it can be linted, diffed and merged by somebody
/// who never reads C#, and the worst a bad one does is fail one broker's removals. A code
/// connector is a class running in the worker process, holding whatever identity that job
/// was released, driving a session against a site nobody here controls. That is reviewed
/// like any other change to the worker and lives in a curated list rather than being
/// discovered. Recording which one a connector is keeps the difference legible at
/// dispatch, where the decision to run it is actually made.
/// </remarks>
public enum ConnectorKind
{
    /// <summary>A declarative document, interpreted by a generic engine.</summary>
    Recipe,

    /// <summary>A hand-written class, allow-listed and compiled in.</summary>
    Code,
}

/// <summary>
/// What a connector needs before it can run, and what it is able to do.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiredFields"/> is a declaration, not a request: it is read before the
/// connector is invoked and it is what the release asks the vault for. A connector that
/// never names a date of birth therefore cannot cause one to be decrypted — not because
/// nothing asks for it at the wrong moment, but because there is no moment at which it
/// could. The same list bounds what the connector can put in front of a broker: a field
/// absent here has no path out of the vault for this piece of work at all.
/// </para>
/// <para>
/// For a recipe the list is derived from the document — whichever placeholders it
/// references — so it cannot disagree with what the recipe actually fills in. For a code
/// connector it is written out and reviewed alongside the class.
/// </para>
/// <para>
/// <see cref="Method"/> is here so the mismatch is catchable. A company's catalog entry
/// says how it accepts a demand, and resolving a form-driving connector for a company that
/// offers only a mailbox is a dispatch fault rather than a connector fault — it would
/// otherwise surface as a form connector failing to find a form, which reads as a broker
/// that redesigned its site and burns the retries meant for one.
/// </para>
/// </remarks>
/// <param name="RequiredFields">
/// The groups of an identity this connector is given, and nothing beyond them. Never
/// empty: a demand naming nobody is not a demand any company can act on.
/// </param>
public sealed record ConnectorCapabilities(
    ConnectorKind Kind,
    RemovalMethod Method,
    IReadOnlySet<IdentityField> RequiredFields);

/// <summary>
/// Makes one demand of one company on behalf of one identity.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of the search contract, and the side that came first. A search produces a
/// listing and so cannot be handed one; a connector is handed the demand and the listing it
/// is about, and acts on them. That asymmetry is why the two are separate interfaces rather
/// than one widened to cover both — a search pointed at a listing it had already found
/// would be a verification, and a connector asked to find something would be a search.
/// </para>
/// <para>
/// <b>It acts, and it says what it did.</b> Whether the listing is actually gone is not a
/// question a connector can answer: submitting a form and having a company honour it are
/// separated by however long the deadline is, and only a verification scan closes that gap.
/// So the outcomes here are about the attempt — an action taken, nothing found to act on, a
/// stop, a wait, a failure — and never about the result of it.
/// </para>
/// <para>
/// <b>Nothing here names a tenant.</b> A connector is given an identity to speak for and
/// the company to speak to, and has no way to ask whose identity it is. Whatever happens
/// to the session it opens, it cannot attribute the work to an account, and it cannot reach
/// back for anything it was not handed.
/// </para>
/// <para>
/// <b>Throwing is not an answer.</b> An implementation that cannot say what happened should
/// return <see cref="ConnectorResult.Failed"/> with a reason, which is what tells the
/// dispatcher whether trying again is worth anything. An exception escaping here is a bug
/// in the connector, and it is treated as one rather than as a company that was quiet
/// today — the difference matters more than on the search side, because a demand that
/// silently failed leaves somebody believing a company was asked to delete their data.
/// </para>
/// </remarks>
public interface IBrokerConnector
{
    /// <summary>What this connector needs, read before it is invoked.</summary>
    ConnectorCapabilities Capabilities { get; }

    /// <summary>Acts, once, and says what it did.</summary>
    Task<ConnectorResult> ExecuteAsync(ConnectorContext context, CancellationToken cancellationToken);
}
