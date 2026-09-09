// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Dbr.Removals;

/// <summary>
/// Where one company takes an opt-out, and nothing else about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A local part, not an address.</b> The domain comes from the company's catalog row and
/// is joined to this in code, which is the same rule the search recipes are held to and the
/// same reason: a document that could name a whole address could send somebody's name and
/// home address to any mailbox on the internet, and it would arrive for review looking like
/// a changed string. A reviewer reading <c>privacy</c> here knows the message goes to the
/// company the row names, whatever else the document says.
/// </para>
/// <para>
/// There is nothing here about what to write. The wording is a property of the law being
/// invoked rather than of the company being asked, so it lives with the legal basis and is
/// reviewed at that bar — a company gets the same demand whoever it is, and what changes
/// between them is only where it is sent.
/// </para>
/// </remarks>
/// <param name="BrokerId">
/// The catalog's identity for the company. By id and not by domain, because a domain is a
/// field somebody corrects — and a recipe bound to one would come unbound by the
/// correction, silently, leaving the company simply never asked.
/// </param>
/// <param name="Mailbox">
/// The local part of the opt-out mailbox: the <c>privacy</c> in
/// <c>privacy@example.com</c>.
/// </param>
public sealed record EmailRecipe(Guid BrokerId, string Mailbox)
{
    /// <summary>The mailbox this demand is addressed to at the company's own domain.</summary>
    public string AddressAt(string domain) => $"{Mailbox}@{domain}";
}
