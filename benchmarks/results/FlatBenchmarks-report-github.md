```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                          | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| Manual                          |   7.821 ns | 0.1612 ns | 0.1429 ns |  1.00 |    0.00 | 0.0048 |      80 B |        1.00 |
| ConventionMapper                |  13.868 ns | 0.3024 ns | 0.3824 ns |  1.79 |    0.06 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Generated      |   7.949 ns | 0.2008 ns | 0.2232 ns |  1.01 |    0.03 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_ObjectOverload |  16.953 ns | 0.3759 ns | 0.4329 ns |  2.16 |    0.08 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Interpreted    | 313.629 ns | 3.2671 ns | 2.5508 ns | 39.98 |    0.62 | 0.0200 |     336 B |        4.20 |
| Mapster                         |  15.959 ns | 0.3123 ns | 0.2608 ns |  2.04 |    0.05 | 0.0048 |      80 B |        1.00 |
