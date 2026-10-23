```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                          | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| Manual                          |   7.754 ns | 0.1977 ns | 0.3136 ns |  1.00 |    0.00 | 0.0048 |      80 B |        1.00 |
| ConventionMapper                |  15.961 ns | 0.2906 ns | 0.2427 ns |  1.99 |    0.05 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Generated      |   7.962 ns | 0.2005 ns | 0.3765 ns |  1.03 |    0.07 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_ObjectOverload |  18.094 ns | 0.2640 ns | 0.2205 ns |  2.25 |    0.07 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Interpreted    | 306.277 ns | 6.1425 ns | 7.7683 ns | 39.04 |    2.07 | 0.0200 |     336 B |        4.20 |
| Mapster                         |  15.561 ns | 0.1572 ns | 0.1313 ns |  1.94 |    0.06 | 0.0048 |      80 B |        1.00 |
