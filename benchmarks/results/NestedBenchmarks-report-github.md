```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                     | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Manual                     | 73.62 ns | 1.506 ns | 3.832 ns |  1.00 |    0.00 | 0.0272 |     456 B |        1.00 |
| ConventionMapper           | 61.50 ns | 1.210 ns | 1.243 ns |  0.79 |    0.03 | 0.0229 |     384 B |        0.84 |
| ConventionMapper_Generated | 52.08 ns | 1.053 ns | 0.933 ns |  0.67 |    0.03 | 0.0229 |     384 B |        0.84 |
| Mapster                    | 67.63 ns | 1.385 ns | 1.848 ns |  0.88 |    0.04 | 0.0229 |     384 B |        0.84 |
