// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Catalog;
using Dbr.Domain.Connectors;
using Dbr.Domain.Mail;
using Dbr.Domain.Profiles;

namespace Dbr.Removals;

/// <summary>
/// Makes a demand of a company that offers a mailbox and no form.
/// </summary>
/// <remarks>
/// <para>
/// <b>One engine, many companies.</b> The wording comes from a reviewed template and the
/// address from a reviewed recipe, so what is written here is the part that is the same for
/// everybody: pick the words the demand's own legal basis calls for, write the identity into
/// them, and hand the result to the relay. That is what makes this a recipe connector rather
/// than a code one — nothing about any particular company is compiled in.
/// </para>
/// <para>
/// <b>It holds no identity between calls.</b> One instance serves every tenant's demands to
/// one company, exactly as the search engine does, and the moment something about a person
/// were parked on it that sharing would become a way to put one person's name in another
/// person's demand.
/// </para>
/// <para>
/// <b>What it declares is the union of what its templates write.</b> A demand's wording
/// depends on the act being invoked, which is a fact about the person's residency and is not
/// known when the grant is minted — the grant is scoped per company, before the demand is
/// looked at. So the declaration covers every template this connector could reach, which
/// over-releases whenever one template names a group another does not. The alternative is a
/// grant minted after the wording is chosen, which is a change to the dispatcher rather than
/// to this; keeping the templates writing the same groups is what keeps the cost at nothing,
/// and it is worth a reviewer's attention when one of them stops.
/// </para>
/// </remarks>
public sealed class TemplatedEmailConnector : IBrokerConnector
{
    private readonly EmailRecipe _recipe;
    private readonly IReadOnlyDictionary<DemandTemplateKey, DemandTemplate> _templates;
    private readonly IMailSender _sender;
    private readonly IJobMailboxes _mailboxes;

    public TemplatedEmailConnector(
        EmailRecipe recipe,
        IReadOnlyCollection<DemandTemplate> templates,
        IMailSender sender,
        IJobMailboxes mailboxes)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(templates);

        _recipe = recipe;
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _mailboxes = mailboxes ?? throw new ArgumentNullException(nameof(mailboxes));
        _templates = templates.ToDictionary(template => template.Key);

        var fields = new HashSet<IdentityField>();

        foreach (var template in templates)
        {
            fields.UnionWith(template.RequiredFields);
        }

        Capabilities = new ConnectorCapabilities(ConnectorKind.Recipe, RemovalMethod.Email, fields);
    }

    public ConnectorCapabilities Capabilities { get; }

    public async Task<ConnectorResult> ExecuteAsync(
        ConnectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var key = context.Demand.StatuteCode is { } statute
            ? DemandTemplateKey.For(statute, context.Demand.RequestType)
            : DemandTemplateKey.Courtesy(context.Demand.RequestType);

        if (!_templates.TryGetValue(key, out var template))
        {
            // Nothing to say, so nothing is said. Refusing here rather than falling back to
            // whatever wording is nearest is the point: a demand sent under the wrong act
            // claims an obligation in somebody's name that nothing established, and a
            // company that checks would be right to refuse it.
            return new ConnectorResult.Failed(
                ConnectorFailureReason.Unsupported,
                $"There is no reviewed wording for {Describe(key)}, so this demand has "
                + "nothing to say. It is catalog content that is missing, not something "
                + "another attempt would find.",
                Retryable: false);
        }

        var subject = template.Subject.RenderText(context.ReleasedIdentity);

        if (subject.Value is null)
        {
            return Incomplete(subject.Missing);
        }

        var body = template.Body.RenderText(context.ReleasedIdentity);

        if (body.Value is null)
        {
            return Incomplete(body.Missing);
        }

        var message = new OutboundMessage(
            _mailboxes.For(context.JobId),
            _recipe.AddressAt(context.Broker.Domain),
            subject.Value,
            body.Value);

        try
        {
            await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (MailDeliveryException exception)
        {
            // The contract forbids an exception escaping a connector, and this is where that
            // translation happens. Whether another attempt is worth anything is the relay's
            // answer rather than a guess: it distinguishes a refusal from a request to come
            // back later, and this carries that through rather than re-deriving it.
            return new ConnectorResult.Failed(
                ConnectorFailureReason.Transient,
                exception.Message,
                exception.Transient);
        }

        // The demand is in and the clock is running, which is a different answer from an
        // attempt that finished. Nothing here says the listing is gone — a company honouring
        // a deletion request and a company receiving one are separated by however long the
        // deadline is, and only a verification scan closes that gap.
        //
        // The relay's message id is deliberately not reported as a receipt. A receipt is a
        // confirmation the company issued, and a mail server's own identifier for a message
        // is this side's record rather than the company's acknowledgement — it is logged
        // where it is generated, which is where it means what it says.
        return new ConnectorResult.AwaitingBrokerResponse(context.Demand.DeadlineAt, Checkpoint: null);
    }

    /// <summary>
    /// The profile has nothing to put where the wording needs something.
    /// </summary>
    /// <remarks>
    /// Unsupported rather than a failure to retry, and the distinction is the person's
    /// rather than the system's: a demand that cannot name who is making it is one this
    /// profile cannot make of this company until somebody adds the missing field. Another
    /// attempt against the same profile writes the same gap.
    /// </remarks>
    private static ConnectorResult Incomplete(string? missing) =>
        new ConnectorResult.Failed(
            ConnectorFailureReason.Unsupported,
            $"The wording needs {missing}, and this profile has nothing on file for it. A "
            + "demand that cannot say who is making it is one a company cannot act on.",
            Retryable: false);

    private static string Describe(DemandTemplateKey key) =>
        key.StatuteCode is null
            ? $"a {key.RequestType} demand citing no statute"
            : $"a {key.RequestType} demand under {key.StatuteCode}";
}
