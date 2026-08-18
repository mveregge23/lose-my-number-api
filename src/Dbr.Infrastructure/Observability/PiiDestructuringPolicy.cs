// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Serilog.Core;
using Serilog.Events;

namespace Dbr.Infrastructure.Observability;

/// <summary>
/// Refuses to unpack the types that are identifying whole.
/// </summary>
/// <remarks>
/// <para>
/// The enricher would catch these anyway, by the member names inside them. This stops
/// them being taken apart in the first place, which is a smaller thing to get right:
/// the enricher's correctness depends on a list of names covering every member of every
/// identity type, and this depends on the type being on one short list.
/// </para>
/// <para>
/// The failure it exists for is a member somebody adds later. A new field on a profile
/// with a name nobody put in the deny list would start reaching sinks the day it was
/// added, silently and from call sites that had not changed. Refusing at the type
/// removes that whole class of regression rather than asking the list to keep up.
/// </para>
/// </remarks>
public sealed class PiiDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        if (value is not null && PiiRedaction.IsIdentifyingType(value.GetType()))
        {
            result = new ScalarValue(PiiRedaction.Marker);

            return true;
        }

        result = null!;

        return false;
    }
}
