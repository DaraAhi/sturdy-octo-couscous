#!/usr/bin/env bash
# Smoke-test van de API. Twee modi:
#
#  1) Lokaal/CI (standaard): app draait in Production-modus zonder Entra ID. De test bootst de header
#     X-MS-CLIENT-PRINCIPAL na die Azure Container Apps na een echte login toevoegt.
#     Dit toetst de autorisatielogica van de app, NIET Entra ID of Conditional Access zelf.
#       ./scripts/smoke-test.sh http://localhost:8080
#
#  2) Tegen Azure: gebruik scripts/loadtest.py of de browser voor echte logins (T1-T3).
set -uo pipefail
BASE="${1:-http://localhost:8080}"
PASS=0; FAIL=0

principal() { # $1=upn  $2=komma-gescheiden rollen
  python3 - "$1" "$2" <<'PY'
import base64, json, sys
upn, rollen = sys.argv[1], [r for r in sys.argv[2].split(",") if r]
claims = [{"typ": "preferred_username", "val": upn}, {"typ": "name", "val": upn}]
claims += [{"typ": "roles", "val": r} for r in rollen]
print(base64.b64encode(json.dumps({"auth_typ": "aad", "name_typ": "name", "role_typ": "roles", "claims": claims}).encode()).decode())
PY
}

check() { # naam verwacht-status werkelijke-status
  if [ "$2" = "$3" ]; then echo "  OK    $1 (HTTP $3)"; PASS=$((PASS+1)); else echo "  FAIL  $1: verwacht $2, kreeg $3"; FAIL=$((FAIL+1)); fi
}
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
body()   { curl -s "$@"; }

PLANNER=$(principal planner@draloop.test Planner)
CHAUF=$(principal chauffeur1@draloop.test Chauffeur)
IT=$(principal it@draloop.test ITBeheerder)
NOROL=$(principal gast@draloop.test "")

echo "Smoke-test tegen $BASE"
check "health zonder login"                         200 "$(status "$BASE/health")"
check "api zonder login wordt geweigerd"            401 "$(status "$BASE/api/ritten")"
check "vervalste X-Dev-User wordt genegeerd"        401 "$(status -H 'X-Dev-User: x|x|ITBeheerder' "$BASE/api/ritten")"
check "planner ziet ritten"                         200 "$(status -H "X-MS-CLIENT-PRINCIPAL: $PLANNER" "$BASE/api/ritten")"

N_CHAUF=$(body -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" "$BASE/api/ritten" | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))')
N_IT=$(body -H "X-MS-CLIENT-PRINCIPAL: $IT" "$BASE/api/ritten" | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))')
N_NOROL=$(body -H "X-MS-CLIENT-PRINCIPAL: $NOROL" "$BASE/api/ritten" | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))')
check "chauffeur ziet alleen eigen ritten (6 van 12)" 6 "$N_CHAUF"
check "IT-beheerder ziet alle ritten (12)"            12 "$N_IT"
check "gebruiker zonder rol ziet 0 ritten"            0 "$N_NOROL"

check "chauffeur mag auditlog niet zien"            403 "$(status -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" "$BASE/api/beheer/audit")"
check "IT-beheerder ziet auditlog"                  200 "$(status -H "X-MS-CLIENT-PRINCIPAL: $IT" "$BASE/api/beheer/audit")"
check "chauffeur mag ERP-mock niet downloaden"      403 "$(status -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" "$BASE/mock/erp/export")"
check "IT-beheerder krijgt ERP-mock CSV"            200 "$(status -H "X-MS-CLIENT-PRINCIPAL: $IT" "$BASE/mock/erp/export")"

check "wijziging zonder CSRF-header wordt geweigerd" 400 "$(status -X PUT -H "X-MS-CLIENT-PRINCIPAL: $PLANNER" -H 'Content-Type: application/json' -d '{"chauffeurId":"chauffeur2@draloop.test","vertrek":"2026-10-08T08:00:00Z"}' "$BASE/api/ritten/1/toewijzing")"
check "chauffeur mag niet toewijzen"                403 "$(status -X PUT -H "X-Requested-With: ritplanning" -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" -H 'Content-Type: application/json' -d '{"chauffeurId":"x","vertrek":"2026-10-08T08:00:00Z"}' "$BASE/api/ritten/1/toewijzing")"
check "planner wijst rit toe"                       200 "$(status -X PUT -H "X-Requested-With: ritplanning" -H "X-MS-CLIENT-PRINCIPAL: $PLANNER" -H 'Content-Type: application/json' -d '{"chauffeurId":"chauffeur2@draloop.test","vertrek":"2026-10-08T08:00:00Z"}' "$BASE/api/ritten/1/toewijzing")"

echo 'dummy pdf' > /tmp/cmr-test.txt
check "chauffeur bevestigt EIGEN rit (rit 3)"       200 "$(status -X POST -H "X-Requested-With: ritplanning" -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" --data-binary @/tmp/cmr-test.txt "$BASE/api/ritten/3/bevestig?bestandsnaam=cmr.txt")"
check "chauffeur bevestigt rit van COLLEGA (rit 2)" 403 "$(status -X POST -H "X-Requested-With: ritplanning" -H "X-MS-CLIENT-PRINCIPAL: $CHAUF" --data-binary @/tmp/cmr-test.txt "$BASE/api/ritten/2/bevestig?bestandsnaam=cmr.txt")"

echo "Resultaat: $PASS geslaagd, $FAIL mislukt"
[ "$FAIL" -eq 0 ]
