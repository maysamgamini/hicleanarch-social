using System.Net;
using System.Net.Http.Headers;

using Amazon;
using Amazon.Runtime;
using Amazon.S3;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Posts.Infrastructure.MediaStores;

namespace Integration.Tests;

/// <summary>
/// The S3 adapter's own test, against Moto's S3 (motoserver/moto, Apache 2.0) in a container: no AWS
/// account and no call to AWS. Pre-signing is local; the PUT and the HEAD go to Moto.
/// </summary>
public sealed class S3MediaStoreTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Bucket = "social-media-test";

    private readonly IContainer _moto = new ContainerBuilder("motoserver/moto:5.2.3")
        .WithPortBinding(5000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(5000).ForPath("/moto-api/")))
        .Build();

    private AmazonS3Client _s3 = null!;

    public async ValueTask InitializeAsync()
    {
        await _moto.StartAsync(Ct);
        _s3 = new AmazonS3Client(new BasicAWSCredentials("moto", "moto"), new AmazonS3Config
        {
            ServiceURL = $"http://{_moto.Hostname}:{_moto.GetMappedPublicPort(5000)}",
            ForcePathStyle = true,
            AuthenticationRegion = RegionEndpoint.USEast1.SystemName,
        });
        await _s3.PutBucketAsync(Bucket, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _s3.Dispose();
        await _moto.DisposeAsync();
    }

    private static async Task<HttpStatusCode> Put(Uri url, string contentType, byte[] bytes)
    {
        using var http = new HttpClient();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return (await http.PutAsync(url, content, Ct)).StatusCode;
    }

    [Fact]
    public async Task A_presigned_put_within_its_time_stores_the_object()
    {
        var store = new S3MediaStore(_s3, Bucket, "https://media.example.test");
        var key = $"media/{Guid.NewGuid():N}/{Guid.NewGuid():N}";
        byte[] bytes = [1, 2, 3, 4, 5];

        var ticket = await store.IssueUpload(key, "image/png", bytes.Length, DateTimeOffset.UtcNow.AddMinutes(5), Ct);

        Assert.Equal(HttpStatusCode.OK, await Put(ticket.Url, "image/png", bytes));
        Assert.True(await store.Holds(key, "image/png", bytes.Length, Ct));
        Assert.False(await store.Holds(key, "image/png", bytes.Length + 1, Ct));
        Assert.Equal($"https://media.example.test/{key}", store.PublicUrl(key));
    }

    [Fact]
    public async Task After_it_expires_the_presigned_put_is_refused()
    {
        var store = new S3MediaStore(_s3, Bucket, "https://media.example.test");
        var key = $"media/{Guid.NewGuid():N}/{Guid.NewGuid():N}";
        var ticket = await store.IssueUpload(key, "image/png", 3, DateTimeOffset.UtcNow.AddSeconds(1), Ct);

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, await Put(ticket.Url, "image/png", [1, 2, 3]));
        Assert.False(await store.Holds(key, "image/png", 3, Ct));
    }
}
