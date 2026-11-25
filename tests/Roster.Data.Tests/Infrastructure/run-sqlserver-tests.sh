#!/usr/bin/env bash
# Runs the SQL Server tests ([Trait("Category", "SqlServer")]) of the data and web test
# projects against the server in ROSTER_TEST_SQLSERVER. See README.md in this folder.
#
#   ROSTER_TEST_SQLSERVER="Server=localhost,1433;User Id=...;Password=...;TrustServerCertificate=True" \
#   ROSTER_TEST_ID=D4 tests/Roster.Data.Tests/Infrastructure/run-sqlserver-tests.sh [extra filter] [dotnet test options]
#
# The optional first argument narrows the run with another filter term, e.g.
#   run-sqlserver-tests.sh "FullyQualifiedName~Roster.Data.Tests.Jobs"
# On the team machine, run it through the build queue: $S/master/heavy.sh <this script>.
set -euo pipefail

if [ -z "${ROSTER_TEST_SQLSERVER:-}" ]; then
  echo "ROSTER_TEST_SQLSERVER is not set: give it a server-level connection string (no Database)." >&2
  echo "See tests/Roster.Data.Tests/Infrastructure/README.md." >&2
  exit 2
fi
export ROSTER_TEST_ID="${ROSTER_TEST_ID:-Dev}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
filter="Category=SqlServer"
if [ $# -gt 0 ] && [[ "$1" != -* ]]; then
  filter="Category=SqlServer&$1"
  shift
fi

status=0
for project in tests/Roster.Data.Tests tests/Roster.Web.Tests; do
  echo "== $project ($filter, databases Roster_${ROSTER_TEST_ID}_*)"
  dotnet test "$root/$project" --filter "$filter" "$@" || status=$?
done
exit $status
