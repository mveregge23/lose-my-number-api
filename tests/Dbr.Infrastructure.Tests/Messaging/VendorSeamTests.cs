// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Dbr.Domain.Messaging;
using Dbr.Infrastructure.DependencyInjection;
using Dbr.Infrastructure.Messaging;

namespace Dbr.Infrastructure.Tests.Messaging;

/// <summary>
/// That the library carrying the messages stays where it can be replaced.
/// </summary>
/// <remarks>
/// <para>
/// §1 says a vendor's software sits behind an interface the core never bypasses, so
/// swapping it is a registration change rather than a rewrite. That is easy to write down
/// and easy to erode: one <c>ConsumeContext</c> parameter on a handler, one
/// <c>IConsumer</c> in a registration signature, and the seam is gone without anybody
/// deciding to remove it.
/// </para>
/// <para>
/// The concern is not theoretical. MassTransit v9 requires a commercial licence and this
/// codebase is pinned to the last Apache-2.0 line, so the day this matters is a date
/// somebody else picked.
/// </para>
/// </remarks>
public class VendorSeamTests
{
    private static readonly string[] Vendors = ["MassTransit", "RabbitMQ", "Npgsql", "Quartz"];

    [Fact]
    public void The_domain_names_no_vendor()
    {
        // The strongest form of the rule, and the cheapest to keep: the domain project
        // references nothing at all, so anything it needs from the outside world has to be
        // expressed as an interface somebody else implements.
        var referenced = typeof(IBrokerScopedMessage).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => Vendors.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            referenced.Count == 0,
            $"Dbr.Domain now depends on {string.Join(", ", referenced)}. Whatever it needed should "
            + "be an interface here and an implementation in Dbr.Infrastructure.");
    }

    [Fact]
    public void Nothing_a_composition_root_touches_names_the_bus()
    {
        // The surface that would spread the dependency furthest, because every process
        // wanting to do something with a broker calls it.
        var surface = typeof(BrokerLaneRegistrations).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType)
                .Concat(method.GetGenericArguments().SelectMany(a => a.GetGenericParameterConstraints())))
            .Concat(typeof(MessagingServiceCollectionExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(p => p.ParameterType)))
            .Select(type => type.Assembly.GetName().Name ?? string.Empty)
            .Where(name => Vendors.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        Assert.True(
            surface.Count == 0,
            $"The messaging registration surface now exposes {string.Join(", ", surface)}. A caller "
            + "should name a kind of work and something that handles it, never the bus.");
    }

    [Fact]
    public void A_handler_is_handed_the_work_and_nothing_else()
    {
        // The other place the dependency would spread: into every handler, one context
        // parameter at a time.
        var handle = typeof(IBrokerWorkHandler<>).GetMethods().Single();

        Assert.Equal(
            [typeof(CancellationToken)],
            handle.GetParameters().Skip(1).Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void The_replaceable_part_is_in_one_place()
    {
        // Says where the seam is, so that "swap the transport" has an answer more specific
        // than "somewhere in Infrastructure". Everything naming the vendor lives under one
        // namespace; a new implementation is a sibling of it.
        var leaked = typeof(BrokerPacer).Assembly
            .GetTypes()
            .Where(type => type.Namespace is { } ns
                && ns.StartsWith("Dbr.Infrastructure.Messaging", StringComparison.Ordinal)
                && !ns.EndsWith("MassTransitBus", StringComparison.Ordinal))
            .Where(NamesTheBus)
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"These sit outside Messaging.MassTransitBus and still name it: {string.Join(", ", leaked)}.");
    }

    private static bool NamesTheBus(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .Concat(type.GetInterfaces())
            .Any(t => (t.Assembly.GetName().Name ?? string.Empty)
                .Contains("MassTransit", StringComparison.OrdinalIgnoreCase));
}
