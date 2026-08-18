// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Serilog.Events;
using Serilog.Formatting;

namespace Dbr.Infrastructure.Observability;

/// <summary>
/// Wraps a formatter and applies the value rule to whatever it produced.
/// </summary>
/// <remarks>
/// <para>
/// The layer that exists for the part of a log event nobody can rewrite. Properties are
/// the enricher's job and it does that job better than this can, because it still knows
/// which value was which. By the time a line is formatted the structure is gone — what
/// is left is the ability to recognise an address by its shape, which is exactly enough
/// for the case that motivated this: an exception message quoting the value that caused
/// it.
/// </para>
/// <para>
/// It buffers the line rather than streaming it, which is the cost of matching across
/// the whole thing at once. Log lines are short and this runs after the level filter has
/// already discarded most events.
/// </para>
/// </remarks>
public sealed class RedactingTextFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new StringWriter();
        inner.Format(logEvent, buffer);

        output.Write(PiiRedaction.RedactText(buffer.ToString()));
    }
}
