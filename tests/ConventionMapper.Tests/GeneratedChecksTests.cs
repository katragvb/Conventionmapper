using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading;
using ConventionMapper;

// End-to-end: culture-invariant text, [MapFormat], [MapChecked] and [MapValidate], compiled by the generator in this project and run.
namespace ConventionMapper.Tests.Checks
{
    public class Reading
    {
        public decimal Amount { get; set; }
        public DateTime EffectiveDate { get; set; }
        public string StartDate { get; set; } = "";
        public long Count { get; set; }
        public decimal Rate { get; set; }
        public DateTime Day { get; set; }
    }

    public class ReadingDto
    {
        public string Amount { get; set; } = "";
        public string EffectiveDate { get; set; } = "";
        public DateTime StartDate { get; set; }
        public int Count { get; set; }
        public double Rate { get; set; }
#if NET8_0_OR_GREATER
        public DateOnly Day { get; set; }
#endif
    }

    public class Applicant
    {
        [Required] public string? Name { get; set; }
        [MaxLength(5)] public string Code { get; set; } = "";
        [StringLength(10, MinimumLength = 3)] public string? Nick { get; set; }
        [Range(18, 120)] public int Age { get; set; }
        [Range(0, 1)] public double? Score { get; set; }
        public Address? Home { get; set; }
    }

    public class Address { [Required] public string? City { get; set; } [MaxLength(3)] public string? Zip { get; set; } }

    [GeneratedMapper]
    public static partial class ChecksMapper
    {
        [MapFormat(nameof(ReadingDto.EffectiveDate), "yyyy-MM-dd")]
        [MapFormat(nameof(ReadingDto.StartDate), "dd/MM/yyyy")]
        [MapChecked(nameof(ReadingDto.Count), nameof(ReadingDto.Rate)
#if NET8_0_OR_GREATER
            , nameof(ReadingDto.Day)
#endif
        )]
        public static partial ReadingDto ToDto(Reading reading);

        [MapValidate]
        public static partial IReadOnlyList<(string Member, string Message)> Validate(Applicant applicant);
    }

    public class GeneratedChecksTests
    {
        private static Reading Sample() => new Reading
        {
            Amount = 1.5m, EffectiveDate = new DateTime(2024, 3, 9), StartDate = "01/02/2024", Count = 7, Rate = 0.25m, Day = new DateTime(2024, 3, 9),
        };

        [Fact]
        public void Numbers_and_dates_are_written_in_the_invariant_culture()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var dto = ChecksMapper.ToDto(Sample());
                Assert.Equal("1.5", dto.Amount);                // not "1,5"
                Assert.Equal("2024-03-09", dto.EffectiveDate);  // [MapFormat]
                Assert.Equal(new DateTime(2024, 2, 1), dto.StartDate); // ParseExact, invariant
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Checked_conversions_throw_naming_the_member()
        {
            var dto = ChecksMapper.ToDto(Sample());
            Assert.Equal(7, dto.Count);
            Assert.Equal(0.25, dto.Rate);

            var tooBig = Sample();
            tooBig.Count = long.MaxValue;
            var overflow = Assert.Throws<OverflowException>(() => ChecksMapper.ToDto(tooBig));
            Assert.Equal("ToDto: ReadingDto.Count: 9223372036854775807 does not fit in int.", overflow.Message);

            var precise = Sample();
            precise.Rate = 0.1234567890123456789m;
            Assert.Contains("ReadingDto.Rate: 0.1234567890123456789 loses precision as double.", Assert.Throws<OverflowException>(() => ChecksMapper.ToDto(precise)).Message);
#if NET8_0_OR_GREATER
            Assert.Equal(new DateOnly(2024, 3, 9), dto.Day);
            var withTime = Sample();
            withTime.Day = new DateTime(2024, 3, 9, 10, 30, 0);
            Assert.Contains("ReadingDto.Day", Assert.Throws<OverflowException>(() => ChecksMapper.ToDto(withTime)).Message);
#endif
        }

        [Fact]
        public void Validate_checks_lengths_required_members_ranges_and_nested_objects()
        {
            Assert.Empty(ChecksMapper.Validate(new Applicant { Name = "Ann", Code = "AB1", Nick = "Annie", Age = 30, Score = 0.5, Home = new Address { City = "Leeds", Zip = "LS1" } }));

            var problems = ChecksMapper.Validate(new Applicant { Name = " ", Code = "TOO-LONG", Nick = "A", Age = 12, Score = 2, Home = new Address { Zip = "LS12" } });
            Assert.Equal(new[]
            {
                ("Name", "Name is required."),
                ("Code", "Code is longer than 5 characters (8)."),
                ("Nick", "Nick is shorter than 3 characters."),
                ("Age", "Age must be between 18 and 120."),
                ("Score", "Score must be between 0 and 1."),
                ("Home.City", "Home.City is required."),
                ("Home.Zip", "Home.Zip is longer than 3 characters (4)."),
            }, problems);
            Assert.Equal(("(value)", "A value is required."), Assert.Single(ChecksMapper.Validate(null!)));
        }
    }
}
