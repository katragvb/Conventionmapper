#!/usr/bin/env bash
# Usage: ./build.sh [version]        e.g. ./build.sh 1.0.0
# Restores, builds, tests and packs ConventionMapper and ConventionMapper.SourceGenerator into ./artifacts
set -euo pipefail
VERSION="${1:-1.5.0}"
cd "$(dirname "$0")"

dotnet restore ConventionMapper.sln
dotnet build ConventionMapper.sln -c Release --no-restore -p:Version="$VERSION"
dotnet test tests/ConventionMapper.Tests/ConventionMapper.Tests.csproj -c Release --no-build
dotnet pack src/ConventionMapper/ConventionMapper.csproj -c Release --no-build -p:Version="$VERSION" -o artifacts
dotnet pack src/ConventionMapper.SourceGenerator/ConventionMapper.SourceGenerator.csproj -c Release --no-build -p:Version="$VERSION" -o artifacts

echo
echo "Packages written to ./artifacts:"
ls -1 artifacts
