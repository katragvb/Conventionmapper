# Usage: ./build.ps1 -Version 1.0.0
param([string]$Version = "1.7.1")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet restore ConventionMapper.sln
dotnet build ConventionMapper.sln -c Release --no-restore -p:Version=$Version
dotnet test tests/ConventionMapper.Tests/ConventionMapper.Tests.csproj -c Release --no-build
dotnet pack src/ConventionMapper/ConventionMapper.csproj -c Release --no-build -p:Version=$Version -o artifacts
dotnet pack src/ConventionMapper.SourceGenerator/ConventionMapper.SourceGenerator.csproj -c Release --no-build -p:Version=$Version -o artifacts

Write-Host "`nPackages written to ./artifacts:"
Get-ChildItem artifacts | ForEach-Object { $_.Name }
