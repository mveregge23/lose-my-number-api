// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Api.InternalEdge;

namespace Dbr.Api.Tests.InternalEdge;

/// <summary>
/// Who may open a connection to the internal listener.
/// </summary>
/// <remarks>
/// Two questions, and the interesting cases are the ones that answer yes to exactly one of
/// them. A certificate signed by the right authority with the wrong name, and one carrying
/// the right name signed by somebody else, are both things an attacker can produce — the
/// first by being any other holder of a certificate from this deployment's authority, the
/// second by simply writing the name they want on a certificate of their own.
/// </remarks>
public class InternalClientGateTests : IDisposable
{
    private const string Worker = "dbr-worker";

    private readonly TestPki _pki = TestPki.Create();

    private readonly TestPki _elsewhere = TestPki.Create("somebody-elses-ca");

    private InternalClientGate Gate => new(_pki.Authority, Worker);

    [Fact]
    public void The_worker_this_deployment_issued_is_let_in()
    {
        Assert.True(Gate.Accepts(_pki.Issue(Worker, forServer: false)));
    }

    [Fact]
    public void Presenting_nothing_is_refused()
    {
        // Kestrel is configured to require a certificate, so this should be unreachable —
        // which is exactly why it is asserted rather than assumed. A gate that said yes to
        // null would turn one changed setting into an open door.
        Assert.False(Gate.Accepts(null));
    }

    [Fact]
    public void A_certificate_from_another_authority_is_refused()
    {
        Assert.False(Gate.Accepts(_elsewhere.Issue(Worker, forServer: false)));
    }

    /// <summary>
    /// The case chain validation alone would wave through.
    /// </summary>
    /// <remarks>
    /// Any other certificate this authority ever signs is signed by this authority. If the
    /// name were not checked, every one of them would be a worker.
    /// </remarks>
    [Fact]
    public void Another_certificate_from_the_right_authority_is_refused()
    {
        Assert.False(Gate.Accepts(_pki.Issue("api-internal", forServer: true)));
    }

    /// <summary>
    /// The case a name check alone would wave through.
    /// </summary>
    [Fact]
    public void Somebody_elses_certificate_carrying_the_right_name_is_refused()
    {
        using var impostor = TestPki.Create("impostor-ca");

        Assert.False(Gate.Accepts(impostor.Issue(Worker, forServer: false)));
    }

    [Fact]
    public void The_name_is_compared_exactly()
    {
        // Not case-insensitively and not by prefix. A common name is a string somebody
        // else chooses, so every character of the comparison is doing work.
        Assert.False(Gate.Accepts(_pki.Issue("DBR-WORKER", forServer: false)));
        Assert.False(Gate.Accepts(_pki.Issue("dbr-worker-2", forServer: false)));
    }

    [Fact]
    public void A_gate_needs_an_authority_and_a_name()
    {
        Assert.Throws<ArgumentNullException>(() => new InternalClientGate(null!, Worker));
        Assert.Throws<ArgumentException>(() => new InternalClientGate(_pki.Authority, "  "));
    }

    public void Dispose()
    {
        _pki.Dispose();
        _elsewhere.Dispose();
        GC.SuppressFinalize(this);
    }
}
