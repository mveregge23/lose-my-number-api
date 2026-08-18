// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Serilog.Core;
using Serilog.Events;

namespace Dbr.Infrastructure.Observability;

/// <summary>
/// Rewrites identifying values out of a log event before any sink sees it.
/// </summary>
/// <remarks>
/// <para>
/// Serilog renders a message from its properties rather than from a string built at the
/// call site, which is the whole reason this can work at all: replacing a property
/// replaces it everywhere the event is going, in the rendered line and in the structured
/// payload alike. A logger that formatted eagerly would leave nothing to rewrite.
/// </para>
/// <para>
/// <b>It must stay the last enricher registered.</b> Enrichers run in order, so the one
/// at the end is the only one that sees everything the ones before it added. Registering
/// this first would read as "runs first, so it is safe" and would in fact mean any
/// enricher added later could put an address back.
/// </para>
/// <para>
/// <b>What it cannot see.</b> A message built by string interpolation arrives as a
/// template with no properties in it, so there is nothing here to find and nothing to
/// replace. That case is closed at compile time instead — the build refuses a log
/// template that is not a constant — because a gap this one cannot cover has to be
/// covered somewhere it cannot be forgotten.
/// </para>
/// </remarks>
public sealed class PiiRedactingEnricher : ILogEventEnricher
{
    private static readonly ScalarValue Redacted = new(PiiRedaction.Marker);

    /// <summary>
    /// Where a log event has to come from for its property names to mean what this
    /// thinks they mean.
    /// </summary>
    private const string OwnSourcePrefix = "Dbr.";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var byName = NamesMeanWhatWeThink(logEvent);

        // Materialised before iterating: the loop writes back into the same collection.
        foreach (var property in logEvent.Properties.ToArray())
        {
            var redacted = Redact(property.Key, property.Value, byName);

            if (!ReferenceEquals(redacted, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, redacted));
            }
        }
    }

    /// <summary>
    /// Whether the name list applies to this event, as opposed to only the rules that
    /// judge a value on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is a vocabulary, and a vocabulary belongs to whoever wrote the call. In
    /// this codebase <c>Address</c> is where somebody lives and <c>Name</c> is what they
    /// are called. In the framework's own events they are a listening URL and an
    /// environment, and redacting those was not a hypothetical — it is what the first
    /// version of this did, and the API came up announcing that it was listening on
    /// <c>[redacted]</c> in the <c>[redacted]</c> environment.
    /// </para>
    /// <para>
    /// The value and type rules still apply to every event regardless, so a framework
    /// log line quoting an address is still caught by its shape, and one handed an
    /// identity of ours is still caught by its type. Only the names are scoped.
    /// </para>
    /// <para>
    /// An event with no source at all is treated as ours. Being wrong in that direction
    /// costs a redacted line nobody needed; being wrong in the other costs the thing
    /// this exists to prevent.
    /// </para>
    /// </remarks>
    private static bool NamesMeanWhatWeThink(LogEvent logEvent) =>
        !logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
        || source is not ScalarValue { Value: string context }
        || context.StartsWith(OwnSourcePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The value as it should be written, or the value itself when nothing is wrong with
    /// it.
    /// </summary>
    /// <remarks>
    /// Returns the original instance when nothing changed, so the caller can tell
    /// "checked and fine" from "rewritten" by reference and skip the write.
    /// </remarks>
    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value, bool byName)
    {
        if (byName && PiiRedaction.IsIdentifyingName(name))
        {
            return Redacted;
        }

        return value switch
        {
            ScalarValue scalar => RedactScalar(scalar),
            StructureValue structure => RedactStructure(structure, byName),
            SequenceValue sequence => RedactSequence(name, sequence, byName),
            DictionaryValue dictionary => RedactDictionary(dictionary, byName),
            _ => value,
        };
    }

    private static LogEventPropertyValue RedactScalar(ScalarValue scalar) => scalar.Value switch
    {
        // A record whose ToString prints every member it has, arriving under a name
        // nobody thought to add to the list.
        not null when PiiRedaction.IsIdentifyingType(scalar.Value.GetType()) => Redacted,

        string text when PiiRedaction.IsIdentifyingValue(text) => Redacted,

        _ => scalar,
    };

    /// <summary>
    /// A destructured object: each member judged by its own name, not the name of
    /// whatever is holding it.
    /// </summary>
    private static LogEventPropertyValue RedactStructure(StructureValue structure, bool byName)
    {
        List<LogEventProperty>? rewritten = null;

        for (var i = 0; i < structure.Properties.Count; i++)
        {
            var property = structure.Properties[i];
            var redacted = Redact(property.Name, property.Value, byName);

            if (ReferenceEquals(redacted, property.Value))
            {
                rewritten?.Add(property);

                continue;
            }

            rewritten ??= [.. structure.Properties.Take(i)];
            rewritten.Add(new LogEventProperty(property.Name, redacted));
        }

        return rewritten is null ? structure : new StructureValue(rewritten, structure.TypeTag);
    }

    /// <summary>
    /// A list, judged element by element under the name of the list itself.
    /// </summary>
    /// <remarks>
    /// Elements have no names of their own, so the enclosing one is carried down. It has
    /// already been found not to be identifying — otherwise the whole list would be gone
    /// by now — which leaves the element's own type and shape to decide.
    /// </remarks>
    private static LogEventPropertyValue RedactSequence(string name, SequenceValue sequence, bool byName)
    {
        List<LogEventPropertyValue>? rewritten = null;

        for (var i = 0; i < sequence.Elements.Count; i++)
        {
            var element = sequence.Elements[i];
            var redacted = Redact(name, element, byName);

            if (ReferenceEquals(redacted, element))
            {
                rewritten?.Add(element);

                continue;
            }

            rewritten ??= [.. sequence.Elements.Take(i)];
            rewritten.Add(redacted);
        }

        return rewritten is null ? sequence : new SequenceValue(rewritten);
    }

    /// <summary>
    /// A dictionary, whose keys are names somebody chose at runtime and are judged as
    /// such.
    /// </summary>
    private static LogEventPropertyValue RedactDictionary(DictionaryValue dictionary, bool byName)
    {
        var rewritten = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(
            dictionary.Elements.Count);

        var changed = false;

        foreach (var entry in dictionary.Elements)
        {
            var key = entry.Key.Value as string ?? string.Empty;
            var redacted = Redact(key, entry.Value, byName);

            changed |= !ReferenceEquals(redacted, entry.Value);
            rewritten.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(entry.Key, redacted));
        }

        return changed ? new DictionaryValue(rewritten) : dictionary;
    }
}
