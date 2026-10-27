using Accounts.Domain;

namespace Accounts.UnitTests;

public class HandleRulesTests
{
    [Theory]
    [InlineData("ada")]
    [InlineData("Ada_Lovelace")]
    [InlineData("a1_")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123")]
    public void A_handle_of_3_to_30_letters_digits_and_underscores_is_accepted(string text) =>
        Assert.True(Handle.Create(text).IsSuccess);

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")]
    public void A_handle_shorter_than_3_or_longer_than_30_is_refused(string text) =>
        Assert.Equal("handle_length", Assert.Single(Handle.Create(text).Errors).Code);

    [Fact]
    public void A_missing_handle_is_refused() =>
        Assert.Equal("handle_length", Assert.Single(Handle.Create(null).Errors).Code);

    [Theory]
    [InlineData("ada lovelace")]
    [InlineData("ada-lovelace")]
    [InlineData("ada.lovelace")]
    [InlineData("adé")]
    [InlineData("ada@home")]
    public void A_handle_with_anything_but_letters_digits_and_underscores_is_refused(string text) =>
        Assert.Equal("handle_characters", Assert.Single(Handle.Create(text).Errors).Code);

    [Fact]
    public void Two_handles_that_differ_only_in_case_are_the_same_handle()
    {
        var typed = Handle.Create("Ada_Lovelace").Value;

        Assert.Equal(Handle.Create("ada_lovelace").Value, typed);
        Assert.Equal("Ada_Lovelace", typed.Value); // the owner's own case is kept for display
    }

    [Fact]
    public void A_display_name_is_trimmed_and_must_hold_1_to_50_characters()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.Equal("Ada", Account.Register(Guid.NewGuid(), "ada", "  Ada  ", now).Value.DisplayName);
        Assert.Equal("display_name_length", Assert.Single(Account.Register(Guid.NewGuid(), "ada", "   ", now).Errors).Code);
        Assert.Equal("display_name_length", Assert.Single(Account.Register(Guid.NewGuid(), "ada", new string('x', 51), now).Errors).Code);
    }

    [Fact]
    public void Every_broken_rule_is_reported_at_once()
    {
        var refused = Account.Register(Guid.NewGuid(), "a", "", DateTimeOffset.UnixEpoch);

        Assert.Equal(["handle_length", "display_name_length"], refused.Errors.Select(e => e.Code));
    }
}
