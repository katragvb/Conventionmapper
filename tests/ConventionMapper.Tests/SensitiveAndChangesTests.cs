using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    // ------------------------------------------------------------------ data-leak guard models

    public class SecUser
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        [Sensitive] public string PasswordHash { get; set; } = "hash";
        public SecProfile Profile { get; set; } = new SecProfile();
    }

    public class SecProfile { [Sensitive] public string ApiKey { get; set; } = "key"; public string Nickname { get; set; } = "nick"; }

    public class SecUserDto { public int Id { get; set; } public string Email { get; set; } = ""; }
    public class SecLeakyDto { public int Id { get; set; } public string PasswordHash { get; set; } = ""; }
    public class SecFlatLeakyDto { public int Id { get; set; } public string ProfileApiKey { get; set; } = ""; }
    public class SecProtectedDto { public int Id { get; set; } [Sensitive] public string PasswordHash { get; set; } = ""; }
    public class SecDerivedDto { public int Id { get; set; } public bool HasPassword { get; set; } }
    public record SecRecordDto(int Id, string PasswordHash);

    public class SensitiveGuardTests
    {
        [Fact]
        public void Non_sensitive_maps_are_unaffected()
        {
            var mapper = new MapperConfiguration(cfg => cfg.CreateMap<SecUser, SecUserDto>()).CreateMapper();
            Assert.Equal("a@x", mapper.Map<SecUser, SecUserDto>(new SecUser { Email = "a@x" }).Email);
        }

        [Fact]
        public void Copying_a_sensitive_member_by_convention_fails_the_configuration()
        {
            var ex = Assert.Throws<MapperConfigurationException>(() => new MapperConfiguration(cfg => cfg.CreateMap<SecUser, SecLeakyDto>()));
            var error = Assert.Single(ex.Errors);
            Assert.Contains("PasswordHash", error);
            Assert.Contains("[Sensitive]", error);
        }

        [Fact]
        public void Flattening_a_sensitive_nested_member_fails_the_configuration()
        {
            var ex = Assert.Throws<MapperConfigurationException>(() => new MapperConfiguration(cfg => cfg.CreateMap<SecUser, SecFlatLeakyDto>()));
            Assert.Contains("Profile.ApiKey", Assert.Single(ex.Errors));
        }

        [Fact]
        public void MapFrom_a_sensitive_member_fails_the_configuration()
        {
            Assert.Throws<MapperConfigurationException>(() => new MapperConfiguration(cfg =>
                cfg.CreateMap<SecUser, SecUserDto>().ForMember(d => d.Email, o => o.MapFrom(s => s.PasswordHash))));
        }

        [Fact]
        public void Constructor_parameters_are_checked()
        {
            Assert.Throws<MapperConfigurationException>(() => new MapperConfiguration(cfg => cfg.CreateMap<SecUser, SecRecordDto>()));
        }

        [Fact]
        public void Ignored_or_protected_destinations_and_derived_values_are_allowed()
        {
            var mapper = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<SecUser, SecLeakyDto>().ForMember(d => d.PasswordHash, o => o.Ignore());
                cfg.CreateMap<SecUser, SecProtectedDto>();
                cfg.CreateMap<SecUser, SecDerivedDto>().ForMember(d => d.HasPassword, o => o.MapFrom(s => s.PasswordHash != "")); // derived, not copied
            }).CreateMapper();

            Assert.Equal("", mapper.Map<SecUser, SecLeakyDto>(new SecUser()).PasswordHash);
            Assert.Equal("hash", mapper.Map<SecUser, SecProtectedDto>(new SecUser()).PasswordHash);
            Assert.True(mapper.Map<SecUser, SecDerivedDto>(new SecUser()).HasPassword);
        }
    }

    // ------------------------------------------------------------------ change tracking models

    public class ChgAddress { public string City { get; set; } = ""; }
    public class ChgLine { public string Sku { get; set; } = ""; public int Qty { get; set; } }

    public class ChgCustomerDto
    {
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public ChgAddress Address { get; set; } = new ChgAddress();
        public List<ChgLine> Lines { get; set; } = new List<ChgLine>();
        public List<int> Codes { get; set; } = new List<int>();
        [Sensitive] public string Token { get; set; } = "";
    }

    public class ChgCustomer
    {
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public ChgAddress Address { get; set; } = new ChgAddress();
        public List<ChgLine> Lines { get; set; } = new List<ChgLine>();
        public List<int> Codes { get; set; } = new List<int>();
        [Sensitive] public string Token { get; set; } = "";
    }

    public class ChangeTrackingTests
    {
        private static IMapper Mapper() => new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<ChgCustomerDto, ChgCustomer>();
            cfg.CreateMap<ChgAddress, ChgAddress>();
            cfg.CreateMap<ChgLine, ChgLine>();
        }).CreateMapper();

        private static ChgCustomer Entity() => new ChgCustomer
        {
            Email = "a@x.com",
            Phone = "123",
            Address = new ChgAddress { City = "London" },
            Lines = { new ChgLine { Sku = "A", Qty = 1 }, new ChgLine { Sku = "B", Qty = 2 } },
            Codes = { 1, 2 },
            Token = "old-token"
        };

        private static ChgCustomerDto SameAs(ChgCustomer e) => new ChgCustomerDto
        {
            Email = e.Email,
            Phone = e.Phone,
            Address = new ChgAddress { City = e.Address.City },
            Lines = e.Lines.Select(l => new ChgLine { Sku = l.Sku, Qty = l.Qty }).ToList(),
            Codes = e.Codes.ToList(),
            Token = e.Token
        };

        [Fact]
        public void No_op_mapping_reports_no_changes()
        {
            var entity = Entity();
            var changes = Mapper().MapWithChanges(SameAs(entity), entity);
            Assert.False(changes.HasChanges);
            Assert.Equal("(no changes)", changes.ToString());
        }

        [Fact]
        public void Reports_scalar_nested_collection_and_sequence_changes_with_old_and_new_values()
        {
            var entity = Entity();
            var dto = SameAs(entity);
            dto.Email = "b@x.com";
            dto.Address.City = "Paris";
            dto.Lines[1].Qty = 5;
            dto.Lines.Add(new ChgLine { Sku = "C", Qty = 3 });
            dto.Codes.Add(3);

            var changes = Mapper().MapWithChanges(dto, entity);

            Assert.Equal("a@x.com", changes.Find("Email")!.OldValue);
            Assert.Equal("b@x.com", changes.Find("Email")!.NewValue);
            Assert.Equal("Paris", changes.Find("Address.City")!.NewValue);
            Assert.Equal(5, changes.Find("Lines[1].Qty")!.NewValue);
            Assert.Equal("C", changes.Find("Lines[2].Sku")!.NewValue);
            Assert.Equal(3, changes.Find("Lines.Count")!.NewValue);
            Assert.Equal("Codes: [1, 2] -> [1, 2, 3]", changes.Find("Codes")!.ToString());
            Assert.True(changes.Changed("Address"));
            Assert.True(changes.Changed("Lines"));
            Assert.Null(changes.Find("Phone"));
            Assert.Equal("b@x.com", entity.Email); // the mapping really happened
        }

        [Fact]
        public void Sensitive_values_are_reported_but_redacted()
        {
            var entity = Entity();
            var dto = SameAs(entity);
            dto.Token = "new-token";

            var change = Assert.Single(Mapper().MapWithChanges(dto, entity));

            Assert.Equal("Token", change.Path);
            Assert.True(change.IsSensitive);
            Assert.Null(change.OldValue);
            Assert.Null(change.NewValue);
            Assert.Equal("Token: (sensitive value changed)", change.ToString());
            Assert.Equal("new-token", entity.Token);
        }

        [Fact]
        public void Options_are_passed_through_for_patch_semantics()
        {
            var entity = Entity();
            var patch = SameAs(entity);
            patch.Phone = null;
            patch.Email = "patched@x.com";

            var changes = Mapper().MapWithChanges(patch, entity, opt => opt.IgnoreNullSourceValues());

            Assert.Equal("Email", Assert.Single(changes).Path);
            Assert.Equal("123", entity.Phone);
        }
    }
}
