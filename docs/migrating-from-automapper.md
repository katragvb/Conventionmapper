# Migrating from AutoMapper

ConventionMapper follows AutoMapper's API closely. Most projects switch in four steps: swap the packages, change the namespace, build, and run the tests.

## 1. Swap the packages

```
dotnet remove package AutoMapper
dotnet remove package AutoMapper.Extensions.Microsoft.DependencyInjection   # if you used it
dotnet remove package AutoMapper.Collection                                 # if you used it
dotnet remove package AutoMapper.Extensions.EnumMapping                     # if you used it
dotnet add package ConventionMapper
```

Dependency injection, collection merging and strict enum mapping are all in the core `ConventionMapper` package, so there is nothing else to install.

## 2. Change namespaces and registration

| AutoMapper | ConventionMapper |
| --- | --- |
| `using AutoMapper;` | `using ConventionMapper;` |
| `using AutoMapper.Configuration.Annotations;` | `using ConventionMapper;` |
| `services.AddAutoMapper(typeof(Program));` | `services.AddConventionMapper(typeof(Program));` |
| `services.AddAutoMapper(cfg => { ... }, typeof(Program));` | `services.AddConventionMapper(cfg => { ... }, typeof(Program));` |
| `cfg.LicenseKey = "...";` | Delete the line (MIT licensed, no key) |
| `cfg.AddCollectionMappers();` | Delete the line (built in) |
| `[Ignore]` attribute | `[IgnoreMap]` |

## 3. Everything else keeps its name

| Area | Same names as AutoMapper |
| --- | --- |
| Configuration | `MapperConfiguration`, `IMapper`, `IConfigurationProvider`, `Profile`, `CreateMap`, `ReverseMap`, `AssertConfigurationIsValid`, `CompileMappings` |
| Members | `ForMember`, `ForPath`, `ForCtorParam`, `ForSourceMember` / `DoNotValidate`, `MapFrom`, `Ignore`, `Condition`, `PreCondition`, `NullSubstitute`, `UseDestinationValue`, `ForAllMembers` |
| Construction and conversion | `ConstructUsing`, `ConvertUsing`, `AddTransform`, `ValueTransformers` |
| Lifecycle | `BeforeMap`, `AfterMap` |
| Inheritance and graphs | `Include`, `IncludeBase`, `IncludeMembers`, `MaxDepth`, `PreserveReferences` |
| Conventions | Naming conventions, `RecognizePrefixes`, `ReplaceMemberName`, `AddGlobalIgnore`, `ShouldMapProperty`, `ForAllMaps`, `AllowNullCollections`, `AllowNullDestinationValues` |
| Extension points | `IValueResolver`, `IMemberValueResolver`, `ITypeConverter`, `IValueConverter`, `IMappingAction`, `ResolutionContext` / `opt.Items` |
| Attributes | `[AutoMap]`, `[SourceMember]` |
| Queries | `ProjectTo`, including parameters and `ExplicitExpansion` |
| Former add-on packages | `EqualityComparison` (AutoMapper.Collection), `ConvertUsingEnumMapping` (AutoMapper.Extensions.EnumMapping) |

## 4. Not supported

| AutoMapper | What to do instead |
| --- | --- |
| `BuildExecutionPlan()` | `config.Explain<TSource, TDestination>()` returns a readable description of the map. |
| `IncludeAllDerived()` | List each derived map with `Include<TDerivedSource, TDerivedDestination>()`. |
| Expression mapping (`AutoMapper.Extensions.ExpressionMapping`) | Not available. |
| `AutoMapper.Data` (`IDataReader` mapping) | Not available. |
| Proxies for interface destination types | Map to concrete classes. |

## 5. Verify

1. Build the project. The analyzer included in `ConventionMapper` flags `CreateMap<,>()` calls with unmapped destination members (CM1001) in the IDE and in the build output. See [analyzers.md](analyzers.md).
2. Keep, or add, a test that calls `config.AssertConfigurationIsValid()`. It reports every unmapped member, missing map and unknown source path.
3. Run your test suite. Configurations that compile produce the same results.
4. Enum to enum mapping matches by name first, then by numeric value. If your code relied on numeric mapping, use `ConvertUsingEnumMapping(o => o.MapByValue())`.
5. Optional: set `cfg.ValidateNullability = true` to catch nullable sources mapped onto non-nullable destinations.

## Optional: move hot paths to build-time mapping

After migrating, maps on hot paths, or in apps that use Native AOT or trimming, can move to the `ConventionMapper.SourceGenerator` package. It generates plain C# at compile time with the same conventions, and unmapped members become build errors. See [source-generator.md](source-generator.md).
