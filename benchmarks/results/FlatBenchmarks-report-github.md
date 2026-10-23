```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                          | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| Manual                          |   7.607 ns | 0.1509 ns | 0.1412 ns |  1.00 |    0.00 | 0.0048 |      80 B |        1.00 |
| ConventionMapper                |  15.970 ns | 0.3497 ns | 0.3434 ns |  2.10 |    0.06 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Generated      |   8.462 ns | 0.1675 ns | 0.1398 ns |  1.11 |    0.03 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_ObjectOverload |  17.868 ns | 0.3229 ns | 0.2862 ns |  2.35 |    0.07 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Interpreted    | 318.971 ns | 3.7428 ns | 3.1254 ns | 41.88 |    0.70 | 0.0200 |     336 B |        4.20 |
| Mapster                         |  15.476 ns | 0.3454 ns | 0.3231 ns |  2.03 |    0.04 | 0.0048 |      80 B |        1.00 |
| AutoMapper                      |  42.208 ns | 0.8514 ns | 0.7547 ns |  5.55 |    0.11 | 0.0048 |      80 B |        1.00 |
