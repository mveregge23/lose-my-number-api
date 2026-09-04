// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;

namespace Dbr.Domain.Removals;

/// <summary>
/// How a demand is carried out, worked out from what the company accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The catalog decides, not the tenant.</b> A company offering only a mailbox cannot be
/// sent a form however much more convenient that would be, so the strategy is derived from
/// the catalog row rather than accepted from the request. That is what keeps adding a
/// broker a catalog change rather than an API change.
/// </para>
/// <para>
/// <b><see cref="RemovalStrategy.SemiAutomated"/> is never produced here, and that is a gap
/// rather than a decision.</b> It means a form that needs a person partway through, and
/// nothing in the catalog says which forms those are — the only field describing a company's
/// intake is how it accepts a demand, not whether a script can finish one. So every form
/// broker starts out automated and a connector that reaches a step it cannot pass is what
/// discovers otherwise, which moves the request rather than the strategy. Recording a
/// strategy the catalog cannot actually support would be a guess written into a column
/// somebody later reads as a fact.
/// </para>
/// </remarks>
public static class RemovalStrategies
{
    /// <summary>
    /// The strategy for a company that accepts demands this way, or <see langword="null"/>
    /// when this instance has no way to make one.
    /// </summary>
    /// <remarks>
    /// Null for post, and refused rather than substituted. The nearest available strategy
    /// is a message to an opt-out mailbox, and a company that publishes a postal address
    /// precisely because it does not take email is the one company that would not receive
    /// it — so the demand would sit looking sent, its deadline running, with nothing at the
    /// other end. Nothing here prints letters, and saying so is better than acting as
    /// though a mailbox were found.
    /// </remarks>
    public static RemovalStrategy? ForMethod(RemovalMethod method) => method switch
    {
        RemovalMethod.WebForm => RemovalStrategy.Automated,

        // An API is a form with a stabler shape. It is driven by a connector the same way
        // and needs no person, which is the whole of what the strategy records.
        RemovalMethod.Api => RemovalStrategy.Automated,

        RemovalMethod.Email => RemovalStrategy.ManualEmail,

        RemovalMethod.Postal => null,

        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unmapped removal method. Adding one means deciding how a demand against such a "
            + "company is actually carried out, which is a connector rather than a mapping."),
    };
}
