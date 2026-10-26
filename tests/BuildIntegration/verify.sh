#!/usr/bin/env bash
# Verifies <ConventionMapperEmitGeneratedCode>: local builds write the generated code, CI builds fail when it is stale.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf Generated bin obj
fail() { echo "FAIL: $1"; exit 1; }

dotnet build -nologo -v q -p:CI=false > build1.log || { cat build1.log; fail "local build"; }
test -f Generated/BuildIntegration.OrderMapper.g.cs || fail "generated file not written"
test -f Generated/ConventionMapper.DataFlowReport.g.cs || fail "report not written"
grep -q "Title = order.Name" Generated/BuildIntegration.OrderMapper.g.cs || fail "unexpected generated code"
grep -q "/// <item><term>Title</term>" Generated/BuildIntegration.OrderMapper.g.cs || fail "docs option not applied"
grep -q "/// <summary>Maps an order.</summary>" Generated/BuildIntegration.OrderMapper.g.cs || fail "declaration docs not kept"
grep -q "/// <remarks>" Generated/BuildIntegration.OrderMapper.g.cs || fail "data flow not in remarks"
grep -q "Maps an order." bin/Debug/net8.0/BuildIntegration.xml || fail "declaration docs lost in the XML file"

dotnet build -nologo -v q -p:CI=true > build2.log || { cat build2.log; fail "CI build with up-to-date code"; }

sed -i 's/Title = order.Name/Title = order.Changed/' Generated/BuildIntegration.OrderMapper.g.cs
touch Generated/Removed.g.cs
if dotnet build -nologo -v q -p:CI=true > build3.log; then fail "CI build passed with stale code"; fi
grep -q "CM0100" build3.log || { cat build3.log; fail "no CM0100"; }
grep -q "changed: BuildIntegration.OrderMapper.g.cs" build3.log || fail "change not listed"
grep -q "stale: Removed.g.cs" build3.log || fail "stale file not listed"

dotnet build -nologo -v q -p:CI=false > build4.log || { cat build4.log; fail "local build after change"; }
grep -q "Title = order.Name" Generated/BuildIntegration.OrderMapper.g.cs || fail "local build did not restore the file"
test ! -f Generated/Removed.g.cs || fail "stale file not deleted"
rm -f build*.log
echo "OK: generated code is written locally and checked in CI"
