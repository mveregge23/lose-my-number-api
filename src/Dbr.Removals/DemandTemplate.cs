// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Profiles;
using Dbr.Domain.Recipes;

namespace Dbr.Removals;

/// <summary>
/// Which demand a template is the wording for.
/// </summary>
/// <remarks>
/// <para>
/// A statute and a request type together, because neither alone identifies a demand. The
/// same act grants a right to deletion and a right to opt out of a sale, and they are
/// different sentences making different claims — sending one where the other belongs asks a
/// company for something the person did not ask for.
/// </para>
/// <para>
/// <see cref="StatuteCode"/> is absent for a demand made on no statute at all. That is not
/// a gap in the catalog: a company outside every regime this instance knows about can still
/// be asked to delete somebody's data, and the wording that asks is a request rather than
/// an instruction. Keeping it as a key here rather than as a flag elsewhere is what makes
/// the two impossible to confuse — the courtesy wording cannot be reached by a demand that
/// cites an act, and the statutory wording cannot be sent without one.
/// </para>
/// </remarks>
public readonly record struct DemandTemplateKey(string? StatuteCode, LegalRequestType RequestType)
{
    /// <summary>The wording for a demand that cites this act.</summary>
    public static DemandTemplateKey For(string statuteCode, LegalRequestType requestType) =>
        new(statuteCode.ToUpperInvariant(), requestType);

    /// <summary>The wording for a demand that cites nothing.</summary>
    public static DemandTemplateKey Courtesy(LegalRequestType requestType) => new(null, requestType);
}

/// <summary>
/// The words one kind of demand is made in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content, not code.</b> The design's standing rule is that legal judgement lives in
/// reviewed content and never as a literal in application code, and a demand's wording is
/// the most load-bearing content there is: it is the sentence that tells a company what it
/// is obliged to do and on what authority. A connector that composed those sentences in C#
/// would be one where changing what this service claims on somebody's behalf is a code
/// review rather than a counsel review.
/// </para>
/// <para>
/// <b>The subject is a template too, and it is the one that leaves the envelope.</b> A
/// subject line crosses every relay between here and the company in the clear and shows up
/// in a notification on somebody's phone, so it is written to say what the message is
/// without saying who it is about — which is a property of the wording, and so belongs
/// where the wording is reviewed rather than being assembled here.
/// </para>
/// </remarks>
/// <param name="Subject">The one-line summary, which should name no person.</param>
/// <param name="Body">The demand itself.</param>
public sealed record DemandTemplate(
    DemandTemplateKey Key,
    RecipeTemplate Subject,
    RecipeTemplate Body)
{
    /// <summary>
    /// The groups of an identity this wording writes, and therefore the groups a demand made
    /// with it causes to be released.
    /// </summary>
    public IReadOnlySet<IdentityField> RequiredFields { get; } =
        new HashSet<IdentityField>(Subject.RequiredFields.Concat(Body.RequiredFields));
}
