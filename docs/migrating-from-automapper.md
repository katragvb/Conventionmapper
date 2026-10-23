# Migrating from AutoMapper

ConventionMapper follows AutoMapper's API closely, so most projects switch with a package swap, a namespace change and a test run.

## 1. Swap the packages

```
dotnet remove package AutoMapper
dotnet remove package AutoMapper.Extensions.Microsoft.DependencyInjection   # if you used it
dotnet remove package AutoMapper.Collection                                 # if you used it
dotnet remove package AutoMapper.Extensions.EnumMapping                     # if you used it
dotnet add package ConventionMapper
```

Dependency injection, collection merging and strict enum mapping are part of the core ConventionMapper package.

## 2. Replace namespaces and registration

| AutoMapper | ConventionMapper |
| --- | --- |
| `using AutoMapper;` | `using ConventionMapper;` |
| `using AutoMapper.Configuration.Annotations;` | `using ConventionMapper;` |
| `services.AddAutoMapper(typeof(Program));` | `services.AddConventionMapper(typeof(Program));` |
| `services.AddAutoMapper(cfg => { ... }, typeof(Program));` | `services.AddConventionMapper(cfg => { ... }, typeof(Program));` |
| `cfg.LicenseKey = "...";` | delete the line (MIT licensed, no key) |
| `cfg.AddCollectionMappers();` | delete the line (built in) |
| `[Ignore]` attribute | `[IgnoreMap]` |

## 3. Things that keep the same names

`MapperConfiguration`, `IMapper`, `IConfigurationProvider`, `Profile`, `CreateMap`, `ForMember`, `MapFrom`, `Ignore`, `ReverseMap`, `ForPath`, `ForCtorParam`, `ForSourceMember` / `DoNotValidate`, `ConstructUsing`, `ConvertUsing`, `BeforeMap`, `AfterMap`, `Include`, `IncludeBase`, `IncludeMembers`, `MaxDepth`, `PreserveReferences`, `NullSubstitute`, `Condition`, `PreCondition`, `UseDestinationValue`, `AddTransform`, `ValueTransformers`, `ForAllMaps`, `ForAllMembers`, `AllowNullCollections`, `AllowNullDestinationValues`, `ShouldMapProperty`, naming conventions, `RecognizePrefixes`, `ReplaceMemberName`, `AddGlobalIgnore`, `IValueResolver`, `IMemberValueResolver`, `ITypeConverter`, `IValueConverter`, `IMappingAction`, `ResolutionContext` / `opt.Items`, `[AutoMap]`, `[SourceMember]`, `ProjectTo` (including parameters and `ExplicitExpansion`), `AssertConfigurationIsValid`, `CompileMappings`, `EqualityComparison` and `ConvertUsingEnumMapping`.

## 4. Not supported

| AutoMapper | What to do |
| --- | --- |
| `BuildExecutionPlan()` | Use `config.Explain<TSource, TDestination>()` for a readable description. |
| `IncludeAllDerived()` | List derived maps with `Include<TDerivedSource, TDerivedDestination>()`. |
| Expression mapping (`AutoMapper.Extensions.ExpressionMapping`) | Not available. |
| `AutoMapper.Data` (`IDataReader` mapping) | Not available. |
| Proxies for interface destination types | Map to concrete classes. |

## 5. Verify

1. Keep (or add) a test that calls `config.AssertConfigurationIsValid()`. It reports every unmapped member, missing map and unknown source path.
2. Run your test suite. Map results should be the same for configurations that compile.
3. Enum to enum mapping is by name first, then by numeric value. If your code relied on numeric mapping, use `ConvertUsingEnumMapping(o => o.MapByValue())`.
4. Optional: set `cfg.ValidateNullability = true` to catch nullable sources mapped onto non-nullable destinations.
