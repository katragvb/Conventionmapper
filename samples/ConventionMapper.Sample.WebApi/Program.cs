// Run:  dotnet run --project samples/ConventionMapper.Sample.WebApi
//
//   GET   /posts?user=ada&includeTags=true   ProjectTo with a query parameter and explicit expansion
//   GET   /posts/1                           ProjectTo to a details DTO
//   PATCH /posts/1   {"title":"New title"}   mapper.Patch: only non-null fields are applied
//   PUT   /posts/1/tags [{"id":1,"name":"csharp"},{"id":0,"name":"new"}]   collection merge by key
//   GET   /explain                           config.Explain() output
using System.Linq.Expressions;
using ConventionMapper;
using Microsoft.EntityFrameworkCore;
using Sample;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BlogDb>(options => options.UseInMemoryDatabase("blog"));
builder.Services.AddConventionMapper(typeof(BlogProfile));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var config = scope.ServiceProvider.GetRequiredService<MapperConfiguration>();
    config.AssertConfigurationIsValid();  // fail fast on startup
    config.CompileMappings();             // compile every map now instead of on the first request

    var db = scope.ServiceProvider.GetRequiredService<BlogDb>();
    db.Posts.AddRange(
        new Post { Title = "Hello, mapper", Body = "First post.", Author = "ada", PublishedAt = DateTime.UtcNow.AddDays(-2),
                   Tags = { new Tag { Name = "csharp" }, new Tag { Name = "dotnet" } } },
        new Post { Title = "Projections", Body = "ProjectTo selects only what the DTO needs.", Author = "grace", PublishedAt = DateTime.UtcNow.AddDays(-1),
                   Tags = { new Tag { Name = "efcore" } } });
    db.SaveChanges();
}

app.MapGet("/posts", async (BlogDb db, IMapper mapper, string? user, bool? includeTags) =>
{
    var expand = includeTags == true
        ? new Expression<Func<PostSummaryDto, object>>[] { p => p.Tags! }
        : Array.Empty<Expression<Func<PostSummaryDto, object>>>();

    return await db.Posts
        .OrderByDescending(p => p.PublishedAt)
        .ProjectTo<PostSummaryDto>(mapper.ConfigurationProvider, new { currentUser = user ?? "" }, expand)
        .ToListAsync();
});

app.MapGet("/posts/{id:int}", async (int id, BlogDb db, IMapper mapper) =>
    await db.Posts.Where(p => p.Id == id).ProjectTo<PostDetailsDto>(mapper.ConfigurationProvider).SingleOrDefaultAsync()
        is { } post ? Results.Ok(post) : Results.NotFound());

app.MapMethods("/posts/{id:int}", new[] { "PATCH" }, async (int id, PostPatchDto patch, BlogDb db, IMapper mapper) =>
{
    var post = await db.Posts.Include(p => p.Tags).SingleOrDefaultAsync(p => p.Id == id);
    if (post is null) return Results.NotFound();
    mapper.Patch(patch, post);
    await db.SaveChangesAsync();
    return Results.Ok(mapper.Map<PostDetailsDto>(post));
});

app.MapPut("/posts/{id:int}/tags", async (int id, List<TagDto> tags, BlogDb db, IMapper mapper) =>
{
    var post = await db.Posts.Include(p => p.Tags).SingleOrDefaultAsync(p => p.Id == id);
    if (post is null) return Results.NotFound();
    mapper.Map(new PostTagsUpdate { Tags = tags }, post);
    await db.SaveChangesAsync();
    return Results.Ok(mapper.Map<PostDetailsDto>(post));
});

app.MapGet("/explain", (MapperConfiguration config) =>
    Results.Text(config.Explain<Post, PostSummaryDto>() + Environment.NewLine + config.Explain<PostPatchDto, Post>()));

app.Run();
