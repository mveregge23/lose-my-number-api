// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using Dbr.Domain.Identity;
using Dbr.Domain.Profiles;

namespace Dbr.Infrastructure.Observability;

/// <summary>
/// What counts as identifying in a log event, and what goes in its place.
/// </summary>
/// <remarks>
/// <para>
/// One definition, used by both halves of the redaction: the enricher that rewrites
/// properties and the destructuring policy that refuses to unpack an identity in the
/// first place. Two lists would drift, and the direction they drift in is the one where
/// something stops being redacted.
/// </para>
/// <para>
/// <b>This is a backstop, not the rule.</b> The rule is that log events carry ids and
/// enums — a tenant id, a broker id, a status — and never the fields behind them. What
/// is here exists because that rule is enforced by whoever writes the call, and a rule
/// enforced only by attention eventually is not.
/// </para>
/// </remarks>
public static partial class PiiRedaction
{
    /// <summary>What a redacted value is replaced with.</summary>
    /// <remarks>
    /// Deliberately not an empty string and not the property being dropped. A reader
    /// needs to see that something was there and was withheld — a missing property looks
    /// like a call site that forgot to include it, and the two want different fixes.
    /// </remarks>
    public const string Marker = "[redacted]";

    /// <summary>
    /// Property names whose value is identifying whatever it turns out to hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched case-insensitively, and as a suffix as well as a whole name, so
    /// <c>Email</c> also covers <c>TenantEmail</c> and <c>ContactEmail</c>. Suffix rather
    /// than substring: <c>EmailVerified</c> is a boolean and redacting it would teach
    /// people that the redactor is noise to work around.
    /// </para>
    /// <para>
    /// A deny list rather than an allow list, which is the weaker of the two and is
    /// chosen deliberately. Every framework event carries properties nobody here named —
    /// request paths, source contexts, action names, EF command text — and an allow list
    /// would either drop all of them or grow into a list of everything, which is a deny
    /// list with extra steps and a worse failure mode.
    /// </para>
    /// <para>
    /// <b>The generic words are deliberately not here.</b> <c>Name</c>, <c>City</c>,
    /// <c>Street</c>, <c>Contact</c>, <c>Identity</c> and <c>Fields</c> were on this list
    /// and have been taken off, because the thing they would mostly match is public
    /// catalog data: a broker has a name, a city and a contact mailbox, and none of them
    /// belong to any tenant. Redacting those buys nothing and costs the log lines
    /// somebody debugging a broker actually needs — and a redactor that eats the useful
    /// half of a line is one people route around, which is how the entries that do matter
    /// stop being trusted.
    /// </para>
    /// <para>
    /// Nothing identifying got cheaper to leak by removing them. A tenant's own name and
    /// address reach a log through <see cref="IdentifyingTypes"/>, which is matched by
    /// type and does not care what the property was called; the specific spellings below
    /// cover the members those types are made of.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> IdentifyingNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Who somebody is. Not "Name" — a broker has one of those too.
            "Names",
            "FullName",
            "GivenName",
            "FamilyName",
            "Surname",
            "DateOfBirth",
            "Dob",

            // How to reach them. Not "Contact" — a broker's opt-out mailbox is one.
            "Email",
            "EmailAddress",
            "Phone",
            "PhoneNumber",
            "Contacts",

            // Where they live, or lived. Not "City" or "Street", which say almost
            // nothing on their own and are as likely to be a broker's registered office.
            "Address",
            "Addresses",
            "Line1",
            "Line2",
            "PostalCode",
            "ZipCode",

            // The shapes these travel in elsewhere in this codebase. Not "Fields" or
            // "Identity" on their own: too vague to mean this reliably, and the types
            // they name are matched by type anyway.
            "IdentityFields",
            "ReleasedFields",
            "Plaintext",
        };

    /// <summary>
    /// Whether a property of this name holds something identifying.
    /// </summary>
    public static bool IsIdentifyingName(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        if (IdentifyingNames.Contains(propertyName))
        {
            return true;
        }

        // A suffix match, so a name that was qualified on the way to the call site is
        // still caught. Anchored on a word boundary rather than a bare EndsWith, which
        // would fire on 'Rename' for 'Name'.
        foreach (var identifying in IdentifyingNames)
        {
            if (propertyName.Length > identifying.Length
                && propertyName.EndsWith(identifying, StringComparison.OrdinalIgnoreCase)
                && char.IsUpper(propertyName[^identifying.Length]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Types that are identifying whole, whatever property they arrive in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names above only work when somebody chose a name that says what the value is.
    /// This works when they did not: a profile's fields logged as <c>{Thing}</c> is still
    /// a profile's fields.
    /// </para>
    /// <para>
    /// It matters most for the records, because a record's generated
    /// <see cref="object.ToString"/> prints every member it has. A type in here that
    /// reached a log event without being destructured would otherwise render its entire
    /// contents from a call site that looks like it is logging one value.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<Type> IdentifyingTypes { get; } =
        new HashSet<Type>
        {
            typeof(ProfileIdentityFields),
            typeof(ProfileDetails),
            typeof(ProfileAddress),
            typeof(ProfileContact),

            // The account row itself: it carries the address somebody signed up with.
            typeof(Tenant),
        };

    /// <summary>Whether a value of this type is identifying whole.</summary>
    public static bool IsIdentifyingType(Type type) => IdentifyingTypes.Contains(type);

    /// <summary>
    /// Whether a value is identifying regardless of what it was called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second net, and the one that catches the case the names cannot: an address
    /// that arrived in a property called <c>Detail</c>, or an exception message that
    /// quotes the account it was about. Only an email shape is matched here, because an
    /// email address is the one piece of identity in this system that has a form nothing
    /// else shares — a run of digits could be a phone number or a receipt reference, and
    /// a redactor that eats reference numbers is one people route around.
    /// </para>
    /// <para>
    /// <b>Unlike the names, this is not scoped to this codebase's own loggers, and the
    /// cost of that is real.</b> A broker's published opt-out mailbox is email-shaped and
    /// belongs to nobody here, and it gets withheld like any other. It stays unscoped
    /// because the leak it prevents happens under somebody else's logger: EF Core writes
    /// a failed command at error level with the exception attached, so a second signup at
    /// an address already registered puts that address into an event sourced from
    /// <c>Microsoft.EntityFrameworkCore.Database.Command</c>. Scoping this to
    /// <c>Dbr.*</c> would trade a broker mailbox nobody needed in a log for a tenant's
    /// own address in one, which is the wrong way round.
    /// </para>
    /// <para>
    /// So a log line that wants to say which mailbox a removal went to says which broker
    /// instead. The mailbox is catalog data and is one lookup away; whose account it was
    /// sent for is the part that must not be sitting in a log to begin with.
    /// </para>
    /// </remarks>
    public static bool IsIdentifyingValue(string value) =>
        value is not null && EmailShape().IsMatch(value);

    /// <summary>
    /// The same value rule applied to a finished line of output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last thing between a log event and a sink, and the only part of this that can
    /// reach an exception. An exception's message is not a property and an enricher
    /// cannot rewrite it, which matters here rather than in the abstract: a duplicate
    /// address at signup surfaces as a Postgres unique-violation whose message quotes the
    /// value that collided.
    /// </para>
    /// <para>
    /// Only the value rule, never the name list, because at this point the structure is
    /// gone and a name is indistinguishable from prose that happens to contain the same
    /// word. The marker contains no quote or backslash, so substituting it inside
    /// already-encoded JSON cannot break the encoding.
    /// </para>
    /// </remarks>
    public static string RedactText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return EmailShape().Replace(text, Marker);
    }

    /// <summary>
    /// Looser than a validator and much tighter than it first appears it can afford to
    /// be.
    /// </summary>
    /// <remarks>
    /// The characters are restricted to the ones an address actually uses rather than
    /// "anything but a space", which was the first attempt and was wrong in a way worth
    /// keeping a note about: applied to a line of JSON it matched from the opening brace
    /// through to the middle of a timestamp, because <c>{"@t":"1970-01-01T00:00:00.0"</c>
    /// contains something either side of an <c>@</c>. A pattern that eats the document
    /// it is scanning is worse than none — it destroys the log line and looks like
    /// redaction working.
    /// </remarks>
    [GeneratedRegex(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}
