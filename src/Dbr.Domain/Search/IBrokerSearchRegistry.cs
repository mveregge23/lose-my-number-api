// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Domain.Search;

/// <summary>
/// Which search, if any, knows how to ask this company.
/// </summary>
/// <remarks>
/// <para>
/// The half of dispatch the search contract deliberately left out. A catalog row says who
/// a company is and how fast it may be spoken to; it does not say how to read its site,
/// and it should not — the pacing is this instance's business and the reading is a
/// contributed recipe or an allow-listed class. This is the one place the two meet.
/// </para>
/// <para>
/// <b>Resolution is by broker id and not by domain.</b> A domain is a catalog field
/// somebody corrects; a search bound to one would come unbound by the correction, silently,
/// and the company would simply stop being searched. The same reason lanes are named by id.
/// </para>
/// <para>
/// <b>Answering "nothing" is an ordinary answer.</b> Most of the catalog has no search
/// while they are still being written, and a scan covering forty companies and able to
/// search four is the honest state of things rather than a failure — so this returns
/// nothing rather than throwing, and the leg records that it was not searchable.
/// </para>
/// <para>
/// <b>Nothing here is async.</b> A recipe is compiled into the assembly beside the catalog
/// content it belongs to, and a code search is a registered class, so resolving one is a
/// lookup rather than I/O. A signature that promised otherwise would invite an
/// implementation that read a database on a path a scan hits once per company.
/// </para>
/// </remarks>
public interface IBrokerSearchRegistry
{
    /// <summary>
    /// The search for this company, or <see langword="null"/> when this build has none.
    /// </summary>
    IBrokerSearch? Find(Guid brokerId);
}
