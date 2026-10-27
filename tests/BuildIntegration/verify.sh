#!/usr/bin/env bash
# Verifies <ConventionMapperEmitGeneratedCode>: local builds write the generated code, CI builds fail when it is stale.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf Generated bin obj
fail() { echo "FAIL: $1"; exit 1; }

dotnet build -nologo -v q -p:ConventionMapperCheckGeneratedCode=false > build1.log || { cat build1.log; fail "local build"; }
if grep -q "warning" build1.log; then cat build1.log; fail "the build targets produce warnings"; fi
test -f Generated/BuildIntegration.OrderMapper.g.cs || fail "generated file not written"
test -f Generated/ConventionMapper.DataFlowReport.g.cs || fail "report not written"
grep -q "Title = order.Name" Generated/BuildIntegration.OrderMapper.g.cs || fail "unexpected generated code"
grep -q "/// <item><term>Title</term>" Generated/BuildIntegration.OrderMapper.g.cs || fail "docs option not applied"
grep -q "/// <summary>Maps an order.</summary>" Generated/BuildIntegration.OrderMapper.g.cs || fail "declaration docs not kept"
grep -q "/// <remarks>" Generated/BuildIntegration.OrderMapper.g.cs || fail "data flow not in remarks"
grep -q "Maps an order." bin/Debug/net8.0/BuildIntegration.xml || fail "declaration docs lost in the XML file"

dotnet build -nologo -v q -p:ConventionMapperCheckGeneratedCode=true > build2.log || { cat build2.log; fail "CI build with up-to-date code"; }

sed -i 's/Title = order.Name/Title = order.Changed/' Generated/BuildIntegration.OrderMapper.g.cs
touch Generated/Removed.g.cs
if dotnet build -nologo -v q -p:ConventionMapperCheckGeneratedCode=true > build3.log; then fail "CI build passed with stale code"; fi
grep -q "CM0100" build3.log || { cat build3.log; fail "no CM0100"; }
grep -q "changed: BuildIntegration.OrderMapper.g.cs" build3.log || fail "change not listed"
grep -q "stale: Removed.g.cs" build3.log || fail "stale file not listed"

dotnet build -nologo -v n -p:ConventionMapperCheckGeneratedCode=false > build4.log || { cat build4.log; fail "local build after change"; }
grep -q "ConventionMapper updated the generated code" build4.log || { cat build4.log; fail "no message when the generated code was rewritten"; }

# CI detection from the environment, without the explicit property.
sed -i 's/Title = order.Name/Title = order.Changed/' Generated/BuildIntegration.OrderMapper.g.cs
if GITLAB_CI=true dotnet build -nologo -v q > build5.log; then fail "GITLAB_CI build passed with stale code"; fi
grep -q "CM0100" build5.log || { cat build5.log; fail "no CM0100 with GITLAB_CI"; }
dotnet build -nologo -v q -p:ConventionMapperCheckGeneratedCode=false > build6.log || { cat build6.log; fail "restore build"; }
grep -q "Title = order.Name" Generated/BuildIntegration.OrderMapper.g.cs || fail "local build did not restore the file"
test ! -f Generated/Removed.g.cs || fail "stale file not deleted"
rm -f build*.log
echo "OK: generated code is written locally and checked in CI"
