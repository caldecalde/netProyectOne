#!/usr/bin/env bash
# End-to-end check of a running CareFlow stack (Bash twin of smoke-test.ps1).
# Usage: ./scripts/smoke-test.sh [API_URL] [IDP_URL]
set -euo pipefail

API_URL=${1:-http://localhost:5080}
IDP_URL=${2:-http://localhost:8080/realms/careflow}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
failures=0

json_field() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"; }

token() {
  curl -sf -X POST "$IDP_URL/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=careflow-cli -d "username=$1" -d "password=$1" | json_field access_token
}

check() {
  if [ "$2" = "true" ]; then echo "  PASS  $1"; else echo "  FAIL  $1 ${3:-}"; failures=$((failures + 1)); fi
}

echo "CareFlow smoke test against $API_URL"
SUBMITTER=$(token submitter)
VIEWER=$(token viewer)

ready=$(curl -s -o /dev/null -w '%{http_code}' "$API_URL/health/ready")
check "readiness probe" "$([ "$ready" = 200 ] && echo true || echo false)"

for sample in samples/hl7/adt-a01.hl7 samples/x12/837p-single-claim.x12; do
  response=$(curl -s -w '\n%{http_code}' -H "Authorization: Bearer $SUBMITTER" -F "file=@$ROOT/$sample" "$API_URL/api/v1/files")
  status=$(echo "$response" | tail -n1)
  body=$(echo "$response" | sed '$d')
  check "upload $sample (202 new or 200 duplicate)" "$([[ "$status" =~ ^20[02]$ ]] && echo true || echo false)" "got $status: $body"
  id=$(echo "$body" | json_field id)
  read_status=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $VIEWER" "$API_URL/api/v1/files/$id")
  check "  viewer can read status of $id" "$([ "$read_status" = 200 ] && echo true || echo false)"
done

forbidden=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $VIEWER" -F "file=@$ROOT/samples/hl7/adt-a01.hl7" "$API_URL/api/v1/files")
check "viewer cannot upload (403)" "$([ "$forbidden" = 403 ] && echo true || echo false)" "got $forbidden"

anonymous=$(curl -s -o /dev/null -w '%{http_code}' -F "file=@$ROOT/samples/hl7/adt-a01.hl7" "$API_URL/api/v1/files")
check "anonymous upload rejected (401)" "$([ "$anonymous" = 401 ] && echo true || echo false)" "got $anonymous"

if [ "$failures" -gt 0 ]; then echo "$failures check(s) failed"; exit 1; fi
echo "All checks passed"
