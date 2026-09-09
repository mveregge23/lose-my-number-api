// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>The address a demand speaks from, and the job a reply to it belongs to.</summary>
public class JobMailboxTests
{
    private const string Domain = "removals.example.org";

    [Fact]
    public void An_address_names_the_job_it_was_built_for()
    {
        var jobId = Guid.NewGuid();

        var mailbox = JobMailbox.For(jobId, Domain);

        Assert.Equal($"removals-{jobId:D}@{Domain}", mailbox.Address);
        Assert.Equal(jobId, mailbox.JobId);
    }

    [Fact]
    public void What_was_sent_from_is_what_a_reply_resolves_to()
    {
        // The property the whole type exists for. Building the address and reading it back
        // are the two halves of one conversation, and a system where they disagree cannot
        // tell a company that never answered from one whose answer it failed to recognise.
        var jobId = Guid.NewGuid();

        var mailbox = JobMailbox.For(jobId, Domain);

        Assert.True(JobMailbox.TryResolve(mailbox.Address, Domain, out var resolved));
        Assert.Equal(jobId, resolved);
    }

    [Fact]
    public void There_is_no_plus_in_it()
    {
        // A subaddress is stripped by some mail systems and rejected as disposable by some
        // form validators, and both failures are silent from here.
        var mailbox = JobMailbox.For(Guid.NewGuid(), Domain);

        Assert.DoesNotContain("+", mailbox.Address, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REMOVALS-{0}@removals.example.org")]
    [InlineData("removals-{0}@REMOVALS.EXAMPLE.ORG")]
    [InlineData("  removals-{0}@removals.example.org  ")]
    public void A_reply_still_resolves_when_something_on_the_path_changed_its_case(string shape)
    {
        // Local parts are case-sensitive by the letter of the standard and lowercased by a
        // great deal of software in practice. A demand lost to that would look exactly like
        // a company that stayed quiet.
        var jobId = Guid.NewGuid();

        var address = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, jobId);

        Assert.True(JobMailbox.TryResolve(address, Domain, out var resolved));
        Assert.Equal(jobId, resolved);
    }

    [Fact]
    public void An_address_on_somebody_elses_domain_is_not_ours()
    {
        // A catch-all accepts every recipient, so what a message was addressed to is not
        // evidence that it was addressed to this instance.
        var jobId = Guid.NewGuid();

        Assert.False(
            JobMailbox.TryResolve($"removals-{jobId:D}@lookalike.example.net", Domain, out _));
    }

    [Theory]
    [InlineData("hello@removals.example.org")]
    [InlineData("removals-not-a-guid@removals.example.org")]
    [InlineData("removals-@removals.example.org")]
    [InlineData("postmaster@removals.example.org")]
    [InlineData("removals.example.org")]
    [InlineData("@removals.example.org")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_a_catch_all_receives_names_no_job(string? address) =>
        Assert.False(JobMailbox.TryResolve(address, Domain, out _));

    [Fact]
    public void The_empty_job_gets_no_address()
    {
        // It would give every demand the same return address, and the first reply would
        // resolve to all of them.
        Assert.Throws<ArgumentException>(() => JobMailbox.For(Guid.Empty, Domain));
    }

    [Fact]
    public void The_empty_job_is_not_resolved_out_of_one_either()
    {
        // The other direction of the same rule: an address spelling the empty guid is
        // well-formed, and it still names nothing.
        Assert.False(
            JobMailbox.TryResolve($"removals-{Guid.Empty:D}@{Domain}", Domain, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("mail@example.org")]
    [InlineData("example .org")]
    public void A_domain_that_cannot_carry_a_return_address_is_refused(string domain) =>
        Assert.Throws<ArgumentException>(() => JobMailbox.For(Guid.NewGuid(), domain));

    [Fact]
    public void A_trailing_root_dot_is_the_same_domain()
    {
        // A fully-qualified name with the root label spelled out is the same host, and an
        // operator who configures one should not get addresses nothing resolves.
        var jobId = Guid.NewGuid();

        var mailbox = JobMailbox.For(jobId, "removals.example.org.");

        Assert.Equal($"removals-{jobId:D}@removals.example.org", mailbox.Address);
    }
}
