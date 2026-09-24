// SPDX-FileCopyrightText: 2026 Max Veregge
// SPDX-License-Identifier: AGPL-3.0-or-later

using Dbr.Domain.Mail;

namespace Dbr.Infrastructure.Tests.Mail;

/// <summary>
/// Reducing a header value to the spelling an attempt stores.
/// </summary>
/// <remarks>
/// The failure this guards against is silent in the worst way: an id that fails to match
/// produces a reply filed against no attempt, which reads exactly like a company that
/// never answered.
/// </remarks>
public class MessageIdentifierTests
{
    [Theory]
    [InlineData("<abc@relay.test>", "abc@relay.test")]
    [InlineData("abc@relay.test", "abc@relay.test")]
    [InlineData("  <abc@relay.test>  ", "abc@relay.test")]
    [InlineData("< abc@relay.test >", "abc@relay.test")]
    public void The_brackets_come_off_and_the_id_is_what_is_left(string header, string expected) =>
        Assert.Equal(expected, MessageIdentifier.Bare(header));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    [InlineData("not-an-id")]
    [InlineData("@relay.test")]
    [InlineData("abc@")]
    [InlineData("two ids@relay.test")]
    public void What_cannot_be_an_id_is_not_guessed_at(string? header) =>
        Assert.Null(MessageIdentifier.Bare(header));

    /// <summary>
    /// A thread's ids come back in the order the header holds them.
    /// </summary>
    /// <remarks>
    /// The order is the thread's own, and the correlation that follows reads it backwards
    /// on purpose — so a reader reversing it here would quietly make the oldest message in
    /// a long ticket the one a reply is matched against.
    /// </remarks>
    [Fact]
    public void A_references_header_yields_every_id_in_order()
    {
        var references = MessageIdentifier.All("<first@relay.test> <second@relay.test> <third@relay.test>");

        Assert.Equal(["first@relay.test", "second@relay.test", "third@relay.test"], references);
    }

    [Fact]
    public void A_repeated_id_is_asked_about_once()
    {
        var references = MessageIdentifier.All("<one@relay.test> <two@relay.test> <one@relay.test>");

        Assert.Equal(["one@relay.test", "two@relay.test"], references);
    }

    [Fact]
    public void A_header_with_nothing_usable_in_it_yields_nothing() =>
        Assert.Empty(MessageIdentifier.All("mangled by something in the path"));
}
