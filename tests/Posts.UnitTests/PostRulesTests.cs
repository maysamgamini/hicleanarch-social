using Posts.Domain;

namespace Posts.UnitTests;

public class PostRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void A_posts_id_carries_its_creation_millisecond()
    {
        var post = Post.Write(Guid.NewGuid(), "Hello", Now).Value;

        Assert.Equal(7, post.Id.Version);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), Convert.ToInt64(post.Id.ToString("N")[..12], 16));
        Assert.Equal(Now, post.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void A_post_needs_some_text(string? text) =>
        Assert.Equal("text_length", Assert.Single(Post.Write(Guid.NewGuid(), text, Now).Errors).Code);

    [Fact]
    public void A_post_holds_at_most_500_characters()
    {
        Assert.True(Post.Write(Guid.NewGuid(), new string('x', 500), Now).IsSuccess);
        Assert.Equal("text_length", Assert.Single(Post.Write(Guid.NewGuid(), new string('x', 501), Now).Errors).Code);
    }

    [Fact]
    public void A_delete_leaves_a_tombstone_once_and_a_tombstone_cannot_be_edited()
    {
        var post = Post.Write(Guid.NewGuid(), "Bye", Now).Value;

        Assert.True(post.Delete(Now.AddMinutes(1)));
        Assert.False(post.Delete(Now.AddMinutes(2)));
        Assert.Equal(PostStatus.Deleted, post.Status);
        Assert.Equal(Now.AddMinutes(1), post.DeletedAt);
        Assert.Equal("post_deleted", Assert.Single(post.Edit("Back", Now.AddMinutes(3)).Errors).Code);
    }
}
