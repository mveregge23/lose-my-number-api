// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Connectors;

/// <summary>
/// A connector, and the name the attempt is recorded under.
/// </summary>
/// <remarks>
/// The name is returned alongside rather than asked of the connector, because it is the
/// registry that knows it: one generic engine runs the recipes for hundreds of companies,
/// so "which connector ran" is a fact about the registration and not about the class. An
/// engine reporting its own name would record every recipe-driven attempt identically, and
/// the question somebody asks after a company starts failing is which entry was being used.
/// </remarks>
/// <param name="ConnectorId">
/// The registry key or recipe reference, in the shape the attempt is stored as: lower-case,
/// starting with a letter or digit, at most sixty-four characters. Checked where it is
/// resolved rather than where it is written, so a registration that could never be recorded
/// is caught before the work runs instead of at the insert after it.
/// </param>
public sealed record ConnectorRegistration(string ConnectorId, IBrokerConnector Connector);

/// <summary>
/// Which connector, if any, knows how to ask this company.
/// </summary>
/// <remarks>
/// <para>
/// The half of dispatch the connector contract deliberately leaves out. A catalog row says
/// who a company is, how it accepts a demand and how fast it may be spoken to; it does not
/// say how to drive its form or what to write in its mailbox, and it should not — the
/// pacing is this instance's business and the acting is a contributed recipe or an
/// allow-listed class. This is the one place the two meet.
/// </para>
/// <para>
/// <b>Resolution is by broker id and not by domain.</b> A domain is a catalog field
/// somebody corrects; a connector bound to one would come unbound by the correction,
/// silently, and the company would simply stop being asked. The same reason lanes are named
/// by id.
/// </para>
/// <para>
/// <b>Answering "nothing" is an ordinary answer.</b> Most of the catalog has no connector
/// while they are still being written, and a company that can be found but not yet asked is
/// the honest state of things rather than a failure — so this returns nothing rather than
/// throwing, and the attempt records that there was nothing to run.
/// </para>
/// <para>
/// <b>Nothing here is async.</b> A recipe is compiled into the assembly beside the catalog
/// content it belongs to, and a code connector is a registered class, so resolving one is a
/// lookup rather than I/O. A signature that promised otherwise would invite an
/// implementation that read a database on the busiest path the dispatcher has.
/// </para>
/// </remarks>
public interface IBrokerConnectorRegistry
{
    /// <summary>
    /// The connector for this company, or <see langword="null"/> when this build has none.
    /// </summary>
    ConnectorRegistration? Find(Guid brokerId);
}
