using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Posts.Application;

namespace Posts.Infrastructure.MediaStores;

/// <summary>
/// The media store everywhere but AWS: the bytes in memory, upload URLs served by this host
/// (<c>PUT /dev/media-uploads/{key}</c>), signed and with an expiry, as a pre-signed S3 URL would be.
/// Every rule test runs on it. <c>Media:Fake:BaseUrl</c> is the host's own address (http://localhost).
/// </summary>
internal sealed class FakeMediaStore(TimeProvider clock, string baseUrl) : IMediaStore
{
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (string ContentType, byte[] Bytes)> _objects = new();

    public Task<UploadTicket> IssueUpload(string objectKey, string contentType, long size, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var expires = expiresAt.ToUnixTimeSeconds();
        var url = $"{baseUrl.TrimEnd('/')}/dev/media-uploads/{objectKey}?expires={expires}&size={size}&sig={Sign(objectKey, expires, contentType, size)}";
        return Task.FromResult(new UploadTicket(new Uri(url), new Dictionary<string, string> { ["Content-Type"] = contentType }));
    }

    public Task<bool> Holds(string objectKey, string contentType, long size, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.TryGetValue(objectKey, out var stored) && stored.ContentType == contentType && stored.Bytes.LongLength == size);

    public string PublicUrl(string objectKey) => $"{baseUrl.TrimEnd('/')}/dev/media/{objectKey}";

    public Task Delete(string objectKey, CancellationToken cancellationToken)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    /// <summary>An upload through its URL: refused if the signature does not match, it has expired, or the file is not what was asked for.</summary>
    public bool Accept(string objectKey, long expires, long size, string sig, string? contentType, byte[] bytes)
    {
        if (contentType is null || clock.GetUtcNow().ToUnixTimeSeconds() > expires || bytes.LongLength != size
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Sign(objectKey, expires, contentType, size))))
        {
            return false;
        }

        _objects[objectKey] = (contentType, bytes);
        return true;
    }

    public (string ContentType, byte[] Bytes)? Read(string objectKey) => _objects.TryGetValue(objectKey, out var stored) ? stored : null;

    private string Sign(string objectKey, long expires, string contentType, long size) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_signingKey,
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{objectKey}\n{expires}\n{contentType}\n{size}"))));
}
