```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                    | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| ConventionMapper_Pooled   | 33.80 ns | 0.268 ns | 0.224 ns |  1.00 |    0.00 | 0.0048 |      80 B |        1.00 |
| ConventionMapper_Unpooled | 35.52 ns | 0.698 ns | 0.686 ns |  1.05 |    0.02 | 0.0114 |     192 B |        2.40 |
| AutoMapper                | 43.20 ns | 0.852 ns | 0.947 ns |  1.27 |    0.03 | 0.0048 |      80 B |        1.00 |
