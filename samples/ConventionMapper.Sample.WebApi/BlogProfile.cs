using ConventionMapper;

namespace Sample;

public class BlogProfile : Profile
{
    public BlogProfile()
    {
        // Replaced per query: ProjectTo<PostSummaryDto>(config, new { currentUser = "ada" })
        string currentUser = "";

        CreateMap<Post, PostSummaryDto>()
            .ForMember(d => d.IsMine, o => o.MapFrom(s => s.Author == currentUser))
            .ForMember(d => d.TagCount, o => o.MapFrom(s => s.Tags.Count))
            .ForMember(d => d.Tags, o => o.ExplicitExpansion());

        CreateMap<Post, PostDetailsDto>();
        CreateMap<Tag, TagDto>();

        // PUT /posts/{id}/tags: existing tags are updated, new ones added, missing ones removed.
        CreateMap<TagDto, Tag>().EqualityComparison((dto, tag) => dto.Id == tag.Id);
        CreateMap<PostTagsUpdate, Post>(MemberList.Source);

        // PATCH /posts/{id}: only the fields the client sent are applied (mapper.Patch).
        CreateMap<PostPatchDto, Post>(MemberList.Source);
    }
}
