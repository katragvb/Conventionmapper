```

BenchmarkDotNet v0.13.12, Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 9V45, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.32 (8.0.3226.50405), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                     | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Manual                     |  80.37 ns | 1.447 ns | 1.283 ns |  1.00 |    0.00 | 0.0272 |     456 B |        1.00 |
| ConventionMapper           |  63.57 ns | 1.281 ns | 1.475 ns |  0.79 |    0.02 | 0.0229 |     384 B |        0.84 |
| ConventionMapper_Generated |  57.75 ns | 1.169 ns | 1.921 ns |  0.72 |    0.02 | 0.0229 |     384 B |        0.84 |
| Mapster                    |  72.38 ns | 1.283 ns | 1.200 ns |  0.90 |    0.02 | 0.0229 |     384 B |        0.84 |
| AutoMapper                 | 123.91 ns | 0.839 ns | 0.785 ns |  1.54 |    0.02 | 0.0277 |     464 B |        1.02 |
