using System.Text;

using Timeline.Application;

namespace Timeline.UnitTests;

public class FeedCursorTests
{
    private static readonly Guid Reader = Guid.NewGuid();
    private static readonly FeedPosition Position = new(1_793_000_000_123, Guid.CreateVersion7());
    private static readonly FeedCursors Keys = new(new CursorKeys("current-key-0123456789abcdefghij", "previous-key-0123456789abcdefghi"));

    [Fact]
    public void A_cursor_reads_back_to_its_position_for_its_reader() =>
        Assert.Equal(Position, Keys.Read(Reader, Keys.Issue(Reader, Position)).Value);

    [Fact]
    public void A_cursor_is_opaque_json_and_a_signature()
    {
        var cursor = Keys.Issue(Reader, Position);
        var body = cursor.Split('.')[0];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (body.Length % 4)) % 4)));

        Assert.Equal($$"""{"v":1,"s":{{Position.Score}},"p":"{{Position.PostId:N}}"}""", json);
    }

    [Fact]
    public void Another_readers_cursor_is_refused() =>
        Assert.Equal("invalid_cursor", Assert.Single(Keys.Read(Guid.NewGuid(), Keys.Issue(Reader, Position)).Errors).Code);

    [Fact]
    public void A_cursor_edited_by_hand_is_refused()
    {
        var cursor = Keys.Issue(Reader, Position with { Score = Position.Score + 1 });
        var original = Keys.Issue(Reader, Position);
        var forged = cursor.Split('.')[0] + "." + original.Split('.')[1];

        Assert.False(Keys.Read(Reader, forged).IsSuccess);
        Assert.False(Keys.Read(Reader, "not-a-cursor").IsSuccess);
        Assert.False(Keys.Read(Reader, "!!.??").IsSuccess);
    }

    [Fact]
    public void A_cursor_signed_with_the_previous_key_is_accepted_and_one_with_an_unknown_key_is_not()
    {
        var old = new FeedCursors(new CursorKeys("previous-key-0123456789abcdefghi", null)).Issue(Reader, Position);
        var unknown = new FeedCursors(new CursorKeys("some-other-key-0123456789abcdefg", null)).Issue(Reader, Position);

        Assert.True(Keys.Read(Reader, old).IsSuccess);
        Assert.False(Keys.Read(Reader, unknown).IsSuccess);
    }
}
