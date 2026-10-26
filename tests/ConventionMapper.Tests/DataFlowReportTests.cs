using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class FlowAddress { public string City { get; set; } = ""; }
    public class FlowCustomer { public string Name { get; set; } = ""; public FlowAddress Address { get; set; } = new FlowAddress(); }
    public class FlowCustomerDto { public string Name { get; set; } = ""; public string AddressCity { get; set; } = ""; public string Label { get; set; } = ""; public string Internal { get; set; } = ""; }

    [GeneratedMapper]
    public static partial class FlowMapper
    {
        [MapIgnore("Label")]
        [MapIgnore("Internal")]
        public static partial FlowCustomerDto ToDto(FlowCustomer customer);

        public static partial SecProtectedDto ToProtected(SecUser user);
    }

    public class DataFlowReportTests
    {
        [Fact]
        public void Runtime_report_lists_every_flow_and_sensitive_member()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<FlowCustomer, FlowCustomerDto>()
                    .ForMember(d => d.Label, o => o.MapFrom(s => s.Name + "!"))
                    .ForMember(d => d.Internal, o => o.Ignore());
                cfg.CreateMap<SecUser, SecProtectedDto>();
                cfg.CreateMap<SecUser, SecUserDto>();
            });

            var report = config.DataFlowReport();

            Assert.StartsWith("# Data-flow report", report);
            Assert.Contains("3 map(s)", report);
            Assert.Contains("### `FlowCustomer` → `FlowCustomerDto`", report);
            Assert.Contains("| AddressCity | `Address.City` | flattened |", report);
            Assert.Contains("| Internal | — | ignored |", report);
            Assert.Matches(@"\| Label \| `[^`]+` \| computed \|", report);
            Assert.Contains("| PasswordHash (sensitive) | `PasswordHash` | sensitive |", report);
            // Sensitive summary: where each [Sensitive] member goes.
            Assert.Contains("| `SecUser.PasswordHash` | `SecProtectedDto.PasswordHash` (protected) |", report);
        }

        [Fact]
        public void Runtime_report_without_sensitive_members_says_so()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<FlowAddress, FlowAddress>());

            Assert.Contains("No mapped type has members marked `[Sensitive]`.", config.DataFlowReport());
        }

        [Fact]
        public void Generator_writes_the_report_into_the_assembly()
        {
            var report = ConventionMapper.Generated.DataFlowReport.Markdown;

            Assert.StartsWith("# Data-flow report", report);
            Assert.Contains("## `ConventionMapper.Tests.FlowMapper`", report);
            Assert.Contains("### `FlowCustomer` → `FlowCustomerDto`", report);
            Assert.Contains("| AddressCity | `Address.City` | flattened |", report);
            Assert.Contains("| Label | — | ignored |", report);
            Assert.Contains("| PasswordHash (sensitive) | `PasswordHash` | sensitive |", report);
            Assert.Contains("| `SecUser.PasswordHash` | `SecProtectedDto.PasswordHash` (protected) |", report);
        }
    }
}
