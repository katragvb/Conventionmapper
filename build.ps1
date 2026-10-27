# Usage: ./build.ps1 [-Version 1.2.3]   (default: the <Version> in the .csproj files)
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$versionArg = @()
if ($Version) { $versionArg = @("-p:Version=$Version") }

dotnet restore ConventionMapper.sln
dotnet build ConventionMapper.sln -c Release --no-restore @versionArg
dotnet test tests/ConventionMapper.Tests/ConventionMapper.Tests.csproj -c Release --no-build
dotnet pack src/ConventionMapper/ConventionMapper.csproj -c Release --no-build @versionArg -o artifacts
dotnet pack src/ConventionMapper.SourceGenerator/ConventionMapper.SourceGenerator.csproj -c Release --no-build @versionArg -o artifacts
dotnet pack src/ConventionMapper.Testing/ConventionMapper.Testing.csproj -c Release --no-build @versionArg -o artifacts
dotnet pack src/ConventionMapper.Testing.EntityFrameworkCore/ConventionMapper.Testing.EntityFrameworkCore.csproj -c Release --no-build @versionArg -o artifacts

Write-Host "`nPackages written to ./artifacts:"
Get-ChildItem artifacts | ForEach-Object { $_.Name }
