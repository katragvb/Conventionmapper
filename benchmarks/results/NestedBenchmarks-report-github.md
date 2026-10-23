```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                     | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Manual                     | 79.64 ns | 1.568 ns | 3.342 ns |  1.00 |    0.00 | 0.0272 |     456 B |        1.00 |
| ConventionMapper           | 61.93 ns | 1.150 ns | 1.721 ns |  0.77 |    0.03 | 0.0229 |     384 B |        0.84 |
| ConventionMapper_Generated | 52.66 ns | 1.014 ns | 1.750 ns |  0.66 |    0.03 | 0.0229 |     384 B |        0.84 |
| Mapster                    | 69.39 ns | 1.291 ns | 1.078 ns |  0.87 |    0.04 | 0.0229 |     384 B |        0.84 |
