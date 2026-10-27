#!/usr/bin/env bash
# Usage: ./build.sh [version]        e.g. ./build.sh 1.2.3 (default: the <Version> in the .csproj files)
# Restores, builds, tests and packs ConventionMapper, ConventionMapper.SourceGenerator, ConventionMapper.Testing and
# ConventionMapper.Testing.EntityFrameworkCore into ./artifacts
set -euo pipefail
VERSION_ARG=()
if [ $# -gt 0 ]; then VERSION_ARG=(-p:Version="$1"); fi
cd "$(dirname "$0")"

dotnet restore ConventionMapper.sln
dotnet build ConventionMapper.sln -c Release --no-restore ${VERSION_ARG[@]+"${VERSION_ARG[@]}"}
dotnet test tests/ConventionMapper.Tests/ConventionMapper.Tests.csproj -c Release --no-build
dotnet pack src/ConventionMapper/ConventionMapper.csproj -c Release --no-build ${VERSION_ARG[@]+"${VERSION_ARG[@]}"} -o artifacts
dotnet pack src/ConventionMapper.SourceGenerator/ConventionMapper.SourceGenerator.csproj -c Release --no-build ${VERSION_ARG[@]+"${VERSION_ARG[@]}"} -o artifacts
dotnet pack src/ConventionMapper.Testing/ConventionMapper.Testing.csproj -c Release --no-build ${VERSION_ARG[@]+"${VERSION_ARG[@]}"} -o artifacts
dotnet pack src/ConventionMapper.Testing.EntityFrameworkCore/ConventionMapper.Testing.EntityFrameworkCore.csproj -c Release --no-build ${VERSION_ARG[@]+"${VERSION_ARG[@]}"} -o artifacts

echo
echo "Packages written to ./artifacts:"
ls -1 artifacts
