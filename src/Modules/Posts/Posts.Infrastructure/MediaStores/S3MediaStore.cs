using Amazon.S3;
using Amazon.S3.Model;

using Posts.Application;

namespace Posts.Infrastructure.MediaStores;

/// <summary>
/// The media store on AWS: a pre-signed PUT for one object of a stated type, valid until the upload
/// expires (5 minutes), straight from the client to S3; a HEAD to confirm; public URLs through
/// CloudFront (<c>Media:PublicBase</c>), which reads the private bucket with origin access control.
/// Pre-signing is local: issuing an upload URL makes no call to AWS.
/// </summary>
internal sealed class S3MediaStore(IAmazonS3 s3, string bucket, string publicBase) : IMediaStore
{
    public async Task<UploadTicket> IssueUpload(string objectKey, string contentType, long size, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
        });
        return new UploadTicket(new Uri(url), new Dictionary<string, string> { ["Content-Type"] = contentType });
    }

    public async Task<bool> Holds(string objectKey, string contentType, long size, CancellationToken cancellationToken)
    {
        try
        {
            var head = await s3.GetObjectMetadataAsync(bucket, objectKey, cancellationToken);
            return head.ContentLength == size && head.Headers.ContentType == contentType;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public string PublicUrl(string objectKey) => $"{publicBase.TrimEnd('/')}/{objectKey}";

    public Task Delete(string objectKey, CancellationToken cancellationToken) =>
        s3.DeleteObjectAsync(bucket, objectKey, cancellationToken);
}
