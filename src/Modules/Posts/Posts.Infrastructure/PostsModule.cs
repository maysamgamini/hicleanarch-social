using System.Security.Claims;

using Amazon.S3;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Posts.Application;
using Posts.Contracts;
using Posts.Domain;
using Posts.Infrastructure.MediaStores;
using Posts.Infrastructure.Persistence;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;
using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Identity;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Posts.Infrastructure;

/// <summary>Everything a host needs to run the Posts module: its services and its endpoints.</summary>
public static class PostsModule
{
    public static IServiceCollection AddPostsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Posts")
            ?? throw new InvalidOperationException("ConnectionStrings:Posts is not set.");
        services.AddDbContext<PostsDbContext>((sp, db) => PostsDbOptions
            .Configure((DbContextOptionsBuilder<PostsDbContext>)db, connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxCommitInterceptor<PostsDbContext>>()));
        services.AddModuleEvents<PostsDbContext>(ModuleName.Value);
        services.AddPublishedEvent<PostPublished>();
        services.AddPublishedEvent<PostEdited>();
        services.AddPublishedEvent<PostDeleted>();
        services.AddKeyedScoped<ITransactionScope>(ModuleName.Value, (sp, _) => new ModuleTransaction<PostsDbContext>(sp.GetRequiredService<PostsDbContext>()));
        services.AddSingleton<IModuleMigrator, PostsMigrator>();
        services.AddSingleton(new ModuleDatabase("Posts", PostsDbContext.Schema, connectionString));
        AddMediaStore(services, configuration);
        return services
            .AddPostsApplication()
            .AddScoped<IPostRepository, EfPostRepository>()
            .AddScoped<IMediaRepository, EfMediaRepository>()
            .AddSingleton(new MediaOptions(configuration.GetValue("Media:UploadFor", Domain.Media.UploadFor)))
            .AddScoped<IdempotentRequests<PostsDbContext>>()
            .AddSingleton<IdempotencyKeyPurge<PostsDbContext>>()
            .AddSingleton<IIdempotencyKeyPurge>(sp => sp.GetRequiredService<IdempotencyKeyPurge<PostsDbContext>>())
            .AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurge<PostsDbContext>>())
            .AddHostedService<MediaCleanup>();
    }

    /// <summary>
    /// The media store by configuration (Media:Store): S3 behind CloudFront on AWS (Media:Bucket,
    /// Media:PublicBase, and Media:S3:ServiceUrl for a local S3 such as Moto), the fake everywhere else.
    /// </summary>
    private static void AddMediaStore(IServiceCollection services, IConfiguration configuration)
    {
        if (string.Equals(configuration["Media:Store"], "S3", StringComparison.OrdinalIgnoreCase))
        {
            var bucket = configuration["Media:Bucket"] ?? throw new InvalidOperationException("Media:Bucket is not set.");
            var publicBase = configuration["Media:PublicBase"] ?? throw new InvalidOperationException("Media:PublicBase is not set: public URLs go through CloudFront.");
            var serviceUrl = configuration["Media:S3:ServiceUrl"];
            services.AddSingleton<IAmazonS3>(_ => serviceUrl is null
                ? new AmazonS3Client()
                : new AmazonS3Client(new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true }));
            services.AddSingleton<IMediaStore>(sp => new S3MediaStore(sp.GetRequiredService<IAmazonS3>(), bucket, publicBase));
            return;
        }

        services.AddSingleton(sp => new FakeMediaStore(sp.GetRequiredService<TimeProvider>(), configuration["Media:Fake:BaseUrl"] ?? "http://localhost"));
        services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<FakeMediaStore>());
    }

    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var media = app.MapGroup("/v1/media").WithTags("Media");

        // A media row and an upload URL: the client then sends the file there itself, and confirms.
        media.MapPost("/upload-url", async (UploadRequest request, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send<RequestUpload, MediaView>(new RequestUpload(user.UserId(), request.ContentType, request.Size), ct);
            return result.IsSuccess
                ? Results.Created($"/v1/media/{result.Value.MediaId}", MediaReply.Of(result.Value))
                : result.Errors.ToProblem();
        });

        media.MapPost("/{id:guid}/confirm", async (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send<ConfirmUpload, MediaView>(new ConfirmUpload(id, user.UserId()), ct);
            return result.IsSuccess ? Results.Ok(MediaReply.Of(result.Value)) : result.Errors.ToProblem();
        });

        // The fake store's own URLs, only when it is the store: what S3 and CloudFront answer on AWS.
        if (app.ServiceProvider.GetService<FakeMediaStore>() is { } fake)
        {
            app.MapPut("/dev/media-uploads/{**key}", async (string key, long expires, long size, string sig, HttpRequest request) =>
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body, request.HttpContext.RequestAborted);
                return fake.Accept(key, expires, size, sig, request.ContentType, body.ToArray()) ? Results.Ok() : Results.StatusCode(StatusCodes.Status403Forbidden);
            }).AllowAnonymous().ExcludeFromDescription();

            app.MapGet("/dev/media/{**key}", (string key) => fake.Read(key) is { } stored
                ? Results.File(stored.Bytes, stored.ContentType)
                : Results.NotFound()).AllowAnonymous().ExcludeFromDescription();
        }

        var posts = app.MapGroup("/v1/posts").WithTags("Posts");

        // The 201 is "stored": the post, its key and its PostPublished committed together. A retry
        // with the same key gets the same reply; the same key with another body, 422.
        posts.MapPost("/", (PostRequest request, ClaimsPrincipal user, HttpContext http, IDispatcher dispatcher, IdempotentRequests<PostsDbContext> once) =>
            once.Run(http, user.UserId(), request, async ct =>
            {
                var result = await dispatcher.Send<PublishPost, PostView>(new PublishPost(user.UserId(), request.Text, request.MediaIds), ct);
                return result.IsSuccess
                    ? Result.Success(new Reply(StatusCodes.Status201Created, PostReply.Stored(result.Value), $"/v1/posts/{result.Value.PostId}"))
                    : Result.Failure<Reply>(result.Errors);
            }));

        posts.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Answer(await dispatcher.Query<GetPost, PostView>(new GetPost(id, user.UserId()), ct)));

        posts.MapPatch("/{id:guid}", async (Guid id, PostRequest request, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Answer(await dispatcher.Send<EditPost, PostView>(new EditPost(id, user.UserId(), request.Text), ct)));

        posts.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IDispatcher dispatcher, CancellationToken ct) =>
            Answer(await dispatcher.Send<DeletePost, PostView>(new DeletePost(id, user.UserId()), ct)));

        return app;
    }

    private static IResult Answer(Result<PostView> result) =>
        result.IsSuccess ? Results.Ok(PostReply.Of(result.Value)) : result.Errors.ToProblem();
}

/// <summary>The JSON body of <c>POST /v1/posts</c> (up to 4 media ids, s3-l10) and <c>PATCH /v1/posts/{id}</c>.</summary>
internal sealed record PostRequest(string? Text, IReadOnlyList<Guid>? MediaIds = null);

/// <summary>The JSON body of <c>POST /v1/media/upload-url</c>.</summary>
internal sealed record UploadRequest(string? ContentType, long Size);

/// <summary>Media on the wire: while pending, where and until when to upload; once uploaded, its public URL.</summary>
internal sealed record MediaReply(Guid MediaId, string Status, string? UploadUrl, IReadOnlyDictionary<string, string>? UploadHeaders, DateTimeOffset? ExpiresAt, string? Url)
{
    public static MediaReply Of(MediaView m) => new(m.MediaId, m.Status, m.Upload?.Url.ToString(), m.Upload?.Headers,
        m.Upload is null ? null : m.UploadExpiresAt, m.Url);
}

/// <summary>
/// A post on the wire. <c>delivery</c> is set only on the reply to the publish: <c>stored</c>, the one
/// acknowledgment the request itself can promise.
/// </summary>
internal sealed record PostReply(Guid PostId, Guid AuthorId, string? Text, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt, string? Delivery)
{
    public static PostReply Of(PostView p) => new(p.PostId, p.AuthorId, p.Text, p.Status, p.CreatedAt, p.EditedAt, p.DeletedAt, null);

    public static PostReply Stored(PostView p) => Of(p) with { Delivery = "stored" };
}

/// <summary>The media cleanup, hourly on worker tasks: unattached uploads older than a day go (s3-l10).</summary>
internal sealed class MediaCleanup(IServiceScopeFactory scopes) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CleanUpMedia, int>(new CleanUpMedia(), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the next hour tries again
            }
        }
    }
}
