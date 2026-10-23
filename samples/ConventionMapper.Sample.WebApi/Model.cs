using Microsoft.EntityFrameworkCore;

namespace Sample;

// ---------------- entities
public class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public List<Tag> Tags { get; set; } = new();
}

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class BlogDb : DbContext
{
    public BlogDb(DbContextOptions<BlogDb> options) : base(options) { }
    public DbSet<Post> Posts => Set<Post>();
}

// ---------------- DTOs
public class PostSummaryDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsMine { get; set; }
    public int TagCount { get; set; }
    public List<TagDto>? Tags { get; set; }      // only selected with ?includeTags=true
}

public class PostDetailsDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public List<TagDto> Tags { get; set; } = new();
}

public class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class PostPatchDto
{
    public string? Title { get; set; }
    public string? Body { get; set; }
}

public class PostTagsUpdate
{
    public List<TagDto> Tags { get; set; } = new();
}
