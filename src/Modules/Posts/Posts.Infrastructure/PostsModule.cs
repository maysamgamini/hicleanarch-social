using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Posts.Application;
using Posts.Contracts;
using Posts.Domain;
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
        return services
            .AddPostsApplication()
            .AddScoped<IPostRepository, EfPostRepository>()
            .AddScoped<IdempotentRequests<PostsDbContext>>()
            .AddSingleton<IdempotencyKeyPurge<PostsDbContext>>()
            .AddSingleton<IIdempotencyKeyPurge>(sp => sp.GetRequiredService<IdempotencyKeyPurge<PostsDbContext>>())
            .AddHostedService(sp => sp.GetRequiredService<IdempotencyKeyPurge<PostsDbContext>>());
    }

    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var posts = app.MapGroup("/v1/posts").WithTags("Posts");

        // The 201 is "stored": the post, its key and its PostPublished committed together. A retry
        // with the same key gets the same reply; the same key with another body, 422.
        posts.MapPost("/", (PostRequest request, ClaimsPrincipal user, HttpContext http, IDispatcher dispatcher, IdempotentRequests<PostsDbContext> once) =>
            once.Run(http, user.UserId(), request, async ct =>
            {
                var result = await dispatcher.Send<PublishPost, PostView>(new PublishPost(user.UserId(), request.Text), ct);
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

/// <summary>The JSON body of <c>POST /v1/posts</c> and <c>PATCH /v1/posts/{id}</c>.</summary>
internal sealed record PostRequest(string? Text);

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
