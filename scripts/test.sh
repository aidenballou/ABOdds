#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

dotnet restore ABOdds.slnx
test_container_id=$(docker run --detach --rm --publish 127.0.0.1::5432 \
  --env POSTGRES_USER=abodds_test --env POSTGRES_PASSWORD=abodds_test \
  --tmpfs /var/lib/postgresql/data postgres:17-alpine)
trap 'docker stop "$test_container_id" >/dev/null 2>&1 || true' EXIT

ready=false
for attempt in {1..60}; do
  if docker exec "$test_container_id" pg_isready -U abodds_test >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 0.25
done
if [[ "$ready" != true ]]; then
  echo "The disposable PostgreSQL test server did not become ready." >&2
  exit 1
fi

test_binding=$(docker port "$test_container_id" 5432/tcp)
export ABODDS_TEST_POSTGRES="Host=127.0.0.1;Port=${test_binding##*:};Username=abodds_test;Password=abodds_test;GSS Encryption Mode=Disable"
dotnet test ABOdds.slnx --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false "$@"
