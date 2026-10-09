#!/usr/bin/env bash
# Prints a local Keycloak access token for a demo user (submitter | viewer | auditor).
# Usage: ./scripts/get-token.sh submitter
set -euo pipefail
USER_NAME=${1:-submitter}
IDP_URL=${2:-http://localhost:8080/realms/careflow}

curl -sf -X POST "$IDP_URL/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=careflow-cli -d "username=$USER_NAME" -d "password=$USER_NAME" \
  | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p'
