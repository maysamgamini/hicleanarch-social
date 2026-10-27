using Posts.Domain;

namespace Posts.UnitTests;

public class MediaRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("image/gif")]
    public void An_image_of_a_known_type_is_pending_with_an_upload_of_five_minutes(string type)
    {
        var media = Media.Request(Guid.NewGuid(), type, 1_000, Now).Value;

        Assert.Equal(MediaStatus.Pending, media.Status);
        Assert.Equal(Now.AddMinutes(5), media.UploadExpiresAt);
        Assert.Equal($"media/{media.OwnerId:N}/{media.Id:N}", media.ObjectKey);
    }

    [Theory]
    [InlineData("image/svg+xml", 1_000, "media_type")]
    [InlineData(null, 1_000, "media_type")]
    [InlineData("image/png", 0, "media_size")]
    [InlineData("image/png", (10 * 1024 * 1024) + 1, "media_size")]
    public void Anything_else_is_refused(string? type, long size, string reason) =>
        Assert.Equal(reason, Media.Request(Guid.NewGuid(), type, size, Now).Errors[0].Code);

    [Fact]
    public void A_pending_image_is_usable_by_no_one()
    {
        var media = Media.Request(Guid.NewGuid(), "image/png", 10, Now).Value;
        Assert.False(media.UsableBy(media.OwnerId));
    }
}
