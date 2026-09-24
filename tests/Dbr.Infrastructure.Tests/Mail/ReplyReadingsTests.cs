// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>
/// Reading a company's answer against the phrases that company's file declares.
/// </summary>
public class ReplyReadingsTests
{
    /// <summary>
    /// The boilerplate the first real demand actually received, read as it would be.
    /// </summary>
    /// <remarks>
    /// Shortened where it repeats itself and otherwise the company's own words, because the
    /// point of this test is that the phrases in the catalog match what a company really
    /// sends rather than what somebody imagined it would send. It is Spokeo's automated
    /// reply of 21 September 2026 — which asks for a confirmation and says that a request
    /// nobody replies to is treated as resolved.
    /// </remarks>
    private const string TheReplyThatCame =
        """
        Dear Requester,

        Thank you for contacting Spokeo Customer Care.

        Since you've indicated that you'd like to opt-out your information from public
        display on Spokeo.com, we want to let you know that the fastest and simplest way to
        make your request is to use our easy, self-service tool located at
        www.spokeo.com/optout.

        If you still require assistance from a Customer Care representative, we ask that you
        respond with confirmation that you are the person listed in the record or are
        authorized to act on their behalf. Additionally, if you did not provide the specific
        URL(s) of all the listing(s) you'd like removed in your original request, please
        provide them in your response.

        Providing us with the specific URL(s) and information you'd like removed will aid us
        in opting out your information thoroughly. If we do not receive a reply, we will
        consider your request resolved.
        """;

    private static readonly ReplyPhrases Spokeo = new(
        Confirmed: [],
        Refused: [],
        NeedsUs:
        [
            "respond with confirmation that you are the person listed",
            "we will consider your request resolved",
            "please provide them in your response",
        ]);

    [Fact]
    public void The_answer_the_first_real_demand_got_reads_as_asking_us_for_something()
    {
        var read = ReplyReadings.Read(TheReplyThatCame, Spokeo);

        Assert.Equal(ReplyReading.NeedsUs, read.Reading);
        Assert.Equal("respond with confirmation that you are the person listed", read.MatchedPhrase);
    }

    /// <summary>
    /// A phrase broken across a line still matches.
    /// </summary>
    /// <remarks>
    /// Which the answer above demonstrates by itself: "respond with confirmation that you
    /// are the person listed" spans a wrap in the message as it arrived. A reader that
    /// matched the raw text would work in one mail client's wrapping and not in another's,
    /// and would fail by reading the message as unclear — quietly, and only for some
    /// companies.
    /// </remarks>
    [Fact]
    public void Wrapping_does_not_hide_a_phrase()
    {
        var read = ReplyReadings.Read(
            "we ask that you respond with confirmation\n   that you are the person listed in the record",
            Spokeo);

        Assert.Equal(ReplyReading.NeedsUs, read.Reading);
    }

    [Fact]
    public void Capitalisation_does_not_hide_a_phrase() =>
        Assert.Equal(
            ReplyReading.NeedsUs,
            ReplyReadings.Read("WE WILL CONSIDER YOUR REQUEST RESOLVED", Spokeo).Reading);

    /// <summary>
    /// A company asking for something outranks the same message confirming anything.
    /// </summary>
    /// <remarks>
    /// The ranking is the safety property. A message that both confirms and asks is a
    /// company answering two things at once, and the half that needs a person is the half
    /// that must not be lost — while a demand wrongly recorded as honoured is one nobody
    /// asks about again.
    /// </remarks>
    [Fact]
    public void A_message_that_confirms_and_also_asks_is_read_as_asking()
    {
        var phrases = new ReplyPhrases(
            Confirmed: ["your information has been removed"],
            Refused: [],
            NeedsUs: ["we will consider your request resolved"]);

        var read = ReplyReadings.Read(
            "Your information has been removed. If we do not receive a reply, we will "
            + "consider your request resolved.",
            phrases);

        Assert.Equal(ReplyReading.NeedsUs, read.Reading);
    }

    [Fact]
    public void A_refusal_outranks_a_confirmation_in_the_same_message()
    {
        var phrases = new ReplyPhrases(
            Confirmed: ["has been removed from our records"],
            Refused: ["we are unable to act on this request"],
            NeedsUs: []);

        var read = ReplyReadings.Read(
            "We are unable to act on this request. Nothing has been removed from our records.",
            phrases);

        Assert.Equal(ReplyReading.Refused, read.Reading);
    }

    [Fact]
    public void A_confirmation_is_read_when_nothing_outranks_it()
    {
        var phrases = new ReplyPhrases(
            Confirmed: ["has been removed from our records"],
            Refused: [],
            NeedsUs: []);

        var read = ReplyReadings.Read(
            "The listing has been removed from our records.",
            phrases);

        Assert.Equal(ReplyReading.Confirmed, read.Reading);
        Assert.Equal("has been removed from our records", read.MatchedPhrase);
    }

    /// <summary>
    /// A company nobody has written phrases for has every answer read by a person.
    /// </summary>
    /// <remarks>
    /// The state of every company in the catalog but one, and the reason this can be added
    /// a company at a time rather than all at once.
    /// </remarks>
    [Fact]
    public void A_company_nobody_has_described_reads_as_unclear()
    {
        var read = ReplyReadings.Read(TheReplyThatCame, ReplyPhrases.None);

        Assert.Equal(ReplyReading.Unclear, read.Reading);
        Assert.Null(read.MatchedPhrase);
    }

    [Fact]
    public void An_answer_matching_nothing_declared_reads_as_unclear()
    {
        var read = ReplyReadings.Read("We have received your message.", Spokeo);

        Assert.Equal(ReplyReading.Unclear, read.Reading);
        Assert.Null(read.MatchedPhrase);
    }

    /// <summary>
    /// A message whose prose could not be fetched is unclear rather than anything else.
    /// </summary>
    /// <remarks>
    /// A mailbox that renumbered between the pass that read a message and the fetch of its
    /// body produces this, and the honest reading of an answer nobody could read is that
    /// nobody has read it.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_answer_with_no_prose_reads_as_unclear(string? body) =>
        Assert.Equal(ReplyReading.Unclear, ReplyReadings.Read(body, Spokeo).Reading);
}
