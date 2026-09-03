// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using Dbr.Domain.Catalog;
using Dbr.Domain.Profiles;

namespace Dbr.Domain.Connectors;

/// <summary>
/// The rules the two sides of a removal have to keep, checked where they meet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the constructors of the types themselves.</b> A connector's answer
/// is untrusted input twice over: it comes from code or a document somebody else
/// contributed, and that code acted against a company with no interest in being acted
/// against. Input like that gets checked once, at the boundary it arrives on, by something
/// that can be tested exhaustively — not by validation scattered across records where a
/// <see langword="with"/> expression copies straight past it and each type can only see its
/// own half of the rule anyway.
/// </para>
/// <para>
/// <b>A sentence rather than a boolean</b>, and never an exception. What follows a broken
/// contract is the caller's decision — this company's attempt fails while the rest of the
/// queue carries on, and somebody has to be able to read why from a log — so this says what
/// is wrong and leaves the response to whoever asked.
/// </para>
/// <para>
/// Nothing here needs a clock, and that is deliberate. A deadline in the past and a
/// verification date already elapsed are both worth noticing, and neither is a fact about
/// the contract: a message that sat in a queue over a weekend is not a connector that broke
/// a rule. Checking them here would make these functions depend on when they were called,
/// which is the property that makes them worth trusting.
/// </para>
/// </remarks>
public static partial class ConnectorContract
{
    /// <summary>
    /// Why this connector must not be given this context, or <see langword="null"/> when it
    /// may be.
    /// </summary>
    /// <remarks>
    /// Two of these are worth more than the rest. A context carrying a group the connector
    /// never declared means the release handed over more than was asked for, which is a
    /// fault in the release path rather than in the connector — and one that has already
    /// decrypted something by the time it is visible here. It catches every over-release
    /// that actually carries data, since a group that arrived empty released nothing to
    /// catch. The other is the method mismatch: a connector resolved for a company that
    /// accepts demands some other way would fail in a way that reads as the company having
    /// changed its site, and would spend the retries meant for that on a dispatch bug.
    /// </remarks>
    public static string? Refuse(ConnectorCapabilities capabilities, ConnectorContext context)
    {
        if (capabilities.RequiredFields.Count == 0)
        {
            return "This connector declares that it needs no part of an identity, so there is "
                + "nobody for it to make a demand on behalf of. A connector must name at "
                + "least one field.";
        }

        if (context.JobId == Guid.Empty)
        {
            return "This context names no job, so there is no idempotency key to present and "
                + "nothing for a reply to be matched back to.";
        }

        if (context.RemovalRequestId == Guid.Empty)
        {
            return "This context names no request, so the attempt could not be filed against "
                + "the demand it is an attempt at.";
        }

        if (context.Broker.BrokerId == Guid.Empty)
        {
            return "This context names no broker, so the attempt could not be filed against a "
                + "catalog entry.";
        }

        if (string.IsNullOrWhiteSpace(context.Broker.Domain))
        {
            return "This context names no domain, so there is no company to address.";
        }

        if (capabilities.Method != context.Broker.Method)
        {
            return $"This connector acts by {Spell(capabilities.Method)} and this company "
                + $"accepts demands by {Spell(context.Broker.Method)}. Running it anyway "
                + "would fail in a way that looks like the company changed, and would spend "
                + "this entry's retries on a dispatch fault.";
        }

        if (context.AttemptNumber < 1)
        {
            return $"Attempt numbers start at one, and this context is attempt "
                + $"{context.AttemptNumber}.";
        }

        var demand = RefuseDemand(context.Demand);

        if (demand is not null)
        {
            return demand;
        }

        foreach (var field in Released(context.ReleasedIdentity))
        {
            if (!capabilities.RequiredFields.Contains(field))
            {
                return $"This context carries {Spell(field)}, which this connector never "
                    + "declared that it needs. A release wider than the declaration is a "
                    + "fault in whatever built it, not something to work around here.";
            }
        }

        return null;
    }

    /// <summary>
    /// Why this result cannot be believed, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// Every rule here is about an outcome that means two things at once, or one that
    /// claims to have left something behind and did not. Nothing here judges whether an
    /// attempt went well — a demand a company refused is a legitimate answer and not a
    /// broken contract, and mixing the two would make a connector reporting bad news
    /// indistinguishable from one reporting nonsense.
    /// </remarks>
    public static string? Refuse(ConnectorCapabilities capabilities, ConnectorResult result)
    {
        switch (result)
        {
            case ConnectorResult.Failed failed when string.IsNullOrWhiteSpace(failed.Detail):
                return $"This connector failed with {failed.Reason} and said nothing about "
                    + "what happened, which leaves whoever reads the log with the category "
                    + "and none of the evidence.";

            case ConnectorResult.Failed { Reason: ConnectorFailureReason.Rejected, Retryable: true }:
                return "This connector reports that the company refused the demand and asks "
                    + "for another attempt. A refusal is an answer somebody is entitled to "
                    + "give, and sending the same demand again is ignoring it rather than "
                    + "persisting.";

            case ConnectorResult.RequiresHumanInput ask:
                return RefuseAsk(ask);

            case ConnectorResult.Success success
                when success.ReceiptRef is not null && string.IsNullOrWhiteSpace(success.ReceiptRef):
                return "This connector reports a receipt that is blank. A company that issued "
                    + "no confirmation is reported as having issued none, which is an answer; "
                    + "an empty receipt is the same answer wearing the shape of evidence.";

            default:
                return null;
        }
    }

    /// <summary>
    /// Why this registration cannot be used, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// The name has to survive being written down, and where it is written enforces a shape.
    /// Checking it at resolution rather than at the insert is what makes the failure land
    /// before the demand goes out: a registration that cannot be recorded would otherwise be
    /// discovered after a company had already been asked, leaving an attempt that happened
    /// and no row saying so.
    /// </remarks>
    public static string? Refuse(ConnectorRegistration registration)
    {
        if (!ConnectorId().IsMatch(registration.ConnectorId))
        {
            return $"\"{registration.ConnectorId}\" cannot be recorded as the connector that "
                + "ran. A name is lower-case, starts with a letter or a digit, carries only "
                + "dots, dashes and underscores after that, and is at most sixty-four "
                + "characters.";
        }

        return null;
    }

    private static string? RefuseDemand(ConnectorDemand demand)
    {
        var cited = demand.StatuteCode is not null || demand.StatuteCitation is not null;

        switch (demand.Source)
        {
            case DeadlineSource.Statutory when demand.StatuteCode is null:
                return "This demand says a statute set its deadline and does not name one. A "
                    + "company cannot check an obligation it has not been told the name of.";

            case DeadlineSource.Statutory when demand.StatuteCitation is null:
                return $"This demand cites {demand.StatuteCode} and gives nowhere to read it. "
                    + "A citation somebody cannot follow is an assertion rather than a "
                    + "reference.";

            case DeadlineSource.OperationalDefault when cited:
                return "This demand carries a statute and says its deadline came from the "
                    + "company's own target. One of the two is wrong, and sending it would "
                    + "claim an obligation in somebody's name that nothing established.";
        }

        if (demand.StatuteCitation is { } citation
            && (!citation.IsAbsoluteUri
                || (citation.Scheme != Uri.UriSchemeHttp && citation.Scheme != Uri.UriSchemeHttps)))
        {
            return "This demand's citation is not somewhere a company can read the statute. "
                + "It goes into a message somebody else is meant to follow, so it has to be "
                + "an absolute web address.";
        }

        return null;
    }

    private static string? RefuseAsk(ConnectorResult.RequiresHumanInput result)
    {
        if (string.IsNullOrWhiteSpace(result.Ask.Instructions))
        {
            return "This connector stopped to ask for something and did not say what. Nobody "
                + "can clear an ask they cannot read, so the demand would sit parked while "
                + "looking like it was progressing.";
        }

        if (result.Checkpoint.Length == 0)
        {
            return "This connector stopped to ask for something and saved nothing to resume "
                + "from. A stop that cannot be picked back up is a failure, and reporting it "
                + "as a pause hides that the work has to start over.";
        }

        if (result.Ask.ChallengeRef is { } challenge
            && (!challenge.IsAbsoluteUri
                || (challenge.Scheme != Uri.UriSchemeHttp && challenge.Scheme != Uri.UriSchemeHttps)))
        {
            return "This ask points at a challenge that is not a web address. It is rendered "
                + "to whoever has to clear it, so anything else is a link this system would "
                + "be putting in front of somebody without knowing what it is.";
        }

        return null;
    }

    /// <summary>
    /// The groups this identity actually carries something in.
    /// </summary>
    /// <remarks>
    /// Emptiness is the test, because emptiness is all there is to go on: a profile with no
    /// contacts on file and a release that withheld them arrive here identically. They are
    /// the same thing for this purpose — neither one handed anything over.
    /// </remarks>
    private static IEnumerable<IdentityField> Released(ProfileIdentityFields identity)
    {
        if (identity.Names.Count > 0)
        {
            yield return IdentityField.Names;
        }

        if (identity.Addresses.Count > 0)
        {
            yield return IdentityField.Addresses;
        }

        if (identity.Contacts.Count > 0)
        {
            yield return IdentityField.Contacts;
        }

        if (identity.DateOfBirth is not null)
        {
            yield return IdentityField.DateOfBirth;
        }
    }

    /// <summary>How a field is named in a sentence somebody reads.</summary>
    private static string Spell(IdentityField field) => field switch
    {
        IdentityField.Names => "names",
        IdentityField.Addresses => "addresses",
        IdentityField.Contacts => "contacts",
        IdentityField.DateOfBirth => "a date of birth",
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field,
            "Unmapped identity field. Adding a group to an identity means deciding how a "
            + "release names it as well."),
    };

    /// <summary>How a method is named in a sentence somebody reads.</summary>
    private static string Spell(RemovalMethod method) => method switch
    {
        RemovalMethod.WebForm => "web form",
        RemovalMethod.Email => "email",
        RemovalMethod.Api => "API",
        RemovalMethod.Postal => "post",
        _ => throw new ArgumentOutOfRangeException(
            nameof(method),
            method,
            "Unmapped removal method. Adding one means deciding how a refusal names it as "
            + "well."),
    };

    /// <summary>
    /// The shape an attempt's connector name is stored in.
    /// </summary>
    /// <remarks>
    /// Kept in step with the check constraint on the column by hand, which is the weaker of
    /// the two arrangements available and the one that fails in the right direction: this is
    /// the stricter gate in practice because it runs first, and a drift makes a registration
    /// unusable rather than making a bad name storable.
    /// </remarks>
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ConnectorId();
}
