using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Social.BuildingBlocks.Domain;

namespace Timeline.Application;

/// <summary>Where the next page starts: after the entry with this score and this post id.</summary>
public sealed record FeedPosition(long Score, Guid PostId);

/// <summary>
/// The signing keys, from configuration: new cursors are signed with <see cref="Current"/>; a cursor
/// signed with <see cref="Previous"/> is still accepted, so a key can be rotated without breaking the
/// pages people are scrolling.
/// </summary>
public sealed record CursorKeys(string Current, string? Previous);

/// <summary>
/// An opaque cursor: base64url of <c>{"v":1,"s":score,"p":post id}</c>, a dot, and base64url of an
/// HMAC-SHA256 over that JSON and the reader's id. A cursor edited by hand, made up, or taken from
/// another reader fails the check: the client can carry it, never build or reuse it.
/// </summary>
public sealed class FeedCursors(CursorKeys keys)
{
    public static readonly Error Invalid = Error.Validation("invalid_cursor", "This cursor was not issued to you, or was changed.");

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("s")] long Score,
        [property: JsonPropertyName("p")] string PostId);

    public string Issue(Guid reader, FeedPosition position)
    {
        var json = JsonSerializer.Serialize(new Payload(1, position.Score, position.PostId.ToString("N")));
        return Base64Url(Encoding.UTF8.GetBytes(json)) + "." + Base64Url(Sign(keys.Current, json, reader));
    }

    public Result<FeedPosition> Read(Guid reader, string cursor)
    {
        var parts = cursor.Split('.');
        if (parts.Length != 2 || !TryDecode(parts[0], out var body) || !TryDecode(parts[1], out var signature))
        {
            return Result.Failure<FeedPosition>(Invalid);
        }

        var json = Encoding.UTF8.GetString(body);
        var signedByUs = new[] { keys.Current, keys.Previous }
            .Where(k => !string.IsNullOrEmpty(k))
            .Any(k => CryptographicOperations.FixedTimeEquals(Sign(k!, json, reader), signature));
        if (!signedByUs)
        {
            return Result.Failure<FeedPosition>(Invalid);
        }

        var payload = JsonSerializer.Deserialize<Payload>(json);
        return payload is { Version: 1 } && Guid.TryParseExact(payload.PostId, "N", out var post)
            ? Result.Success(new FeedPosition(payload.Score, post))
            : Result.Failure<FeedPosition>(Invalid);
    }

    private static byte[] Sign(string key, string json, Guid reader) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(json + "|" + reader.ToString("N")));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string text, out byte[] bytes)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        bytes = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, bytes, out var written) && (bytes = bytes[..written]).Length > 0;
    }
}
