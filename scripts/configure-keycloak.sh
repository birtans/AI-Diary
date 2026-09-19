#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Fallback/manual Keycloak setup for a realm that was not imported, or when
# the service-account roles from realm-export.json did not come through.
# Grants the diary-api service account the realm-management roles it needs.
#
# Usage: KC_URL=http://localhost:8082 ./scripts/configure-keycloak.sh
# ---------------------------------------------------------------------------
set -euo pipefail

KC_URL="${KC_URL:-http://localhost:8082}"
KC_ADMIN="${KC_ADMIN:-admin}"
KC_ADMIN_PASSWORD="${KC_ADMIN_PASSWORD:-admin}"
REALM="${REALM:-diary}"

echo "Waiting for Keycloak…"
until curl -sf "$KC_URL/realms/master" >/dev/null; do sleep 2; done

echo "Fetching admin token…"
TOKEN=$(curl -sf -X POST "$KC_URL/realms/master/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "client_id=admin-cli" \
  -d "username=$KC_ADMIN" \
  -d "password=$KC_ADMIN_PASSWORD" \
  -d "grant_type=password" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

AUTH="Authorization: Bearer $TOKEN"

echo "Looking up the diary-api service account…"
SA_ID=$(curl -sf -H "$AUTH" "$KC_URL/admin/realms/$REALM/users?username=service-account-diary-api" \
  | python3 -c "import sys,json; users=json.load(sys.stdin); print(users[0]['id'] if users else '')")

if [[ -z "$SA_ID" ]]; then
  echo "service-account-diary-api not found — is the client 'diary-api' present with service accounts enabled?"
  exit 1
fi

echo "Granting realm-management roles…"
CLIENT_ID=$(curl -sf -H "$AUTH" "$KC_URL/admin/realms/$REALM/clients?clientId=realm-management" \
  | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

PAYLOAD=$(for role in manage-users view-users query-users view-realm; do
  curl -sf -H "$AUTH" "$KC_URL/admin/realms/$REALM/clients/$CLIENT_ID/roles/$role" | python3 -c "import sys,json; r=json.load(sys.stdin); print(json.dumps({'id':r['id'],'name':r['name']}))"
done | python3 -c "
import sys, json
roles = [json.loads(line) for line in sys.stdin]
print(json.dumps(roles))
")

curl -sf -X POST -H "$AUTH" -H "Content-Type: application/json" \
  -d "$PAYLOAD" \
  "$KC_URL/admin/realms/$REALM/users/$SA_ID/role-mappings/clients/$CLIENT_ID" >/dev/null

echo "Done. The API can now use the Keycloak Admin API."
