// Run:  dotnet run -c Release --project benchmarks/ConventionMapper.Benchmarks -- --filter *
using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Mapster;
using CM = ConventionMapper;

public class Person
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public int Age { get; set; }
    public DateTime BirthDate { get; set; }
    public bool IsActive { get; set; }
    public decimal Score { get; set; }
}

public class PersonDto
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public int Age { get; set; }
    public DateTime BirthDate { get; set; }
    public bool IsActive { get; set; }
    public decimal Score { get; set; }
}

public class Customer { public string Name { get; set; } public Address Address { get; set; } }
public class Address { public string City { get; set; } }
public class Line { public string Product { get; set; } public int Quantity { get; set; } public decimal Price { get; set; } }
public class Order { public int Id { get; set; } public Customer Customer { get; set; } public List<Line> Lines { get; set; } }
public class LineDto { public string Product { get; set; } public int Quantity { get; set; } public decimal Price { get; set; } }
public class OrderDto { public int Id { get; set; } public string CustomerName { get; set; } public string CustomerAddressCity { get; set; } public List<LineDto> Lines { get; set; } }

/// <summary>Implemented at build time by ConventionMapper.SourceGenerator.</summary>
[CM.GeneratedMapper]
public static partial class GeneratedMappers
{
    public static partial PersonDto ToDto(Person person);
    public static partial OrderDto ToDto(Order order);
    public static partial LineDto ToDto(Line line);
}

[MemoryDiagnoser]
public class FlatBenchmarks
{
    private readonly Person _person = new Person { Id = 1, FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com", Age = 36, BirthDate = new DateTime(1815, 12, 10), IsActive = true, Score = 99.5m };
    private CM.IMapper _cm, _cmInterpreted;
    private TypeAdapterConfig _mapster;

    [GlobalSetup]
    public void Setup()
    {
        var cm = new CM.MapperConfiguration(cfg => cfg.CreateMap<Person, PersonDto>());
        cm.CompileMappings();
        _cm = cm.CreateMapper();
        _cmInterpreted = new CM.MapperConfiguration(cfg => { cfg.UseCompiledMappings = false; cfg.CreateMap<Person, PersonDto>(); }).CreateMapper();
        _mapster = new TypeAdapterConfig();
        _mapster.NewConfig<Person, PersonDto>();
        _mapster.Compile();
    }

    [Benchmark(Baseline = true)]
    public PersonDto Manual() => new PersonDto
    {
        Id = _person.Id, FirstName = _person.FirstName, LastName = _person.LastName, Email = _person.Email,
        Age = _person.Age, BirthDate = _person.BirthDate, IsActive = _person.IsActive, Score = _person.Score
    };

    [Benchmark] public PersonDto ConventionMapper() => _cm.Map<Person, PersonDto>(_person);
    [Benchmark] public PersonDto ConventionMapper_Generated() => GeneratedMappers.ToDto(_person);
    [Benchmark] public PersonDto ConventionMapper_ObjectOverload() => _cm.Map<PersonDto>(_person);
    [Benchmark] public PersonDto ConventionMapper_Interpreted() => _cmInterpreted.Map<Person, PersonDto>(_person);
    [Benchmark] public PersonDto Mapster() => _person.Adapt<PersonDto>(_mapster);
}

[MemoryDiagnoser]
public class NestedBenchmarks
{
    private readonly Order _order = new Order
    {
        Id = 7,
        Customer = new Customer { Name = "Ada", Address = new Address { City = "London" } },
        Lines = Enumerable.Range(1, 5).Select(i => new Line { Product = "P" + i, Quantity = i, Price = i * 1.5m }).ToList()
    };
    private CM.IMapper _cm;
    private TypeAdapterConfig _mapster;

    [GlobalSetup]
    public void Setup()
    {
        var cm = new CM.MapperConfiguration(cfg => { cfg.CreateMap<Order, OrderDto>(); cfg.CreateMap<Line, LineDto>(); });
        cm.CompileMappings();
        _cm = cm.CreateMapper();
        _mapster = new TypeAdapterConfig();
        _mapster.NewConfig<Order, OrderDto>();
        _mapster.NewConfig<Line, LineDto>();
        _mapster.Compile();
    }

    [Benchmark(Baseline = true)]
    public OrderDto Manual() => new OrderDto
    {
        Id = _order.Id,
        CustomerName = _order.Customer?.Name,
        CustomerAddressCity = _order.Customer?.Address?.City,
        Lines = _order.Lines.Select(l => new LineDto { Product = l.Product, Quantity = l.Quantity, Price = l.Price }).ToList()
    };

    [Benchmark] public OrderDto ConventionMapper() => _cm.Map<Order, OrderDto>(_order);
    [Benchmark] public OrderDto ConventionMapper_Generated() => GeneratedMappers.ToDto(_order);
    [Benchmark] public OrderDto Mapster() => _order.Adapt<OrderDto>(_mapster);
}

/// <summary>A map with a condition needs a per-call ResolutionContext: this measures context pooling.</summary>
[MemoryDiagnoser]
public class ContextBenchmarks
{
    private readonly Person _person = new Person { Id = 1, FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com", Age = 36, BirthDate = new DateTime(1815, 12, 10), IsActive = true, Score = 99.5m };
    private CM.IMapper _pooled, _unpooled;

    private static void Configure(CM.IMapperConfigurationExpression cfg) =>
        cfg.CreateMap<Person, PersonDto>().ForMember(d => d.Email, o => o.Condition((s, d, sm, dm, ctx) => ctx != null));

    [GlobalSetup]
    public void Setup()
    {
        var pooled = new CM.MapperConfiguration(cfg => Configure(cfg));
        pooled.CompileMappings();
        _pooled = pooled.CreateMapper();
        var unpooled = new CM.MapperConfiguration(cfg => { CM.ContextPoolingExtensions.DisableContextPooling(cfg); Configure(cfg); });
        unpooled.CompileMappings();
        _unpooled = unpooled.CreateMapper();
    }

    [Benchmark(Baseline = true)] public PersonDto ConventionMapper_Pooled() => _pooled.Map<Person, PersonDto>(_person);
    [Benchmark] public PersonDto ConventionMapper_Unpooled() => _unpooled.Map<Person, PersonDto>(_person);
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
