#!/usr/bin/env bash
# Rolt een nieuwe revisie uit in Azure Container Apps zonder dat gebruikers iets merken (blue/green):
#  1. nieuwe revisie krijgt 0% verkeer, de huidige revisie blijft live
#  2. health check op de nieuwe revisie
#  3. geslaagd  -> 100% verkeer naar nieuwe revisie, daarna controle op de publieke URL
#                  (mislukt die controle: automatische terugval naar de vorige revisie)
#     mislukt   -> nieuwe revisie wordt gedeactiveerd, vorige revisie blijft live (rollback), pipeline faalt
# Vereist: app staat in "multiple revisions"-modus en verkeer staat vastgepind op een benoemde revisie (zie infra/setup-azure.sh).
set -euo pipefail
: "${APP:?APP ontbreekt}" "${RG:?RG ontbreekt}" "${IMAGE:?IMAGE ontbreekt}" "${SHA:?SHA ontbreekt}"
SUFFIX="r${SHA:0:7}"
NEW="${APP}--${SUFFIX}"
log() { echo "$(date -u +%H:%M:%S) $*"; }

PREV=$(az containerapp ingress traffic show -n "$APP" -g "$RG" --query "[?weight==\`100\`].revisionName | [0]" -o tsv || true)
log "Vorige actieve revisie: ${PREV:-<geen, eerste uitrol>}"

az containerapp update -n "$APP" -g "$RG" --image "$IMAGE" --revision-suffix "$SUFFIX" \
  --set-env-vars APP_VERSION="$SHA" -o none
log "Nieuwe revisie $NEW aangemaakt (0% verkeer)"

NEW_FQDN=$(az containerapp revision show -n "$APP" -g "$RG" --revision "$NEW" --query properties.fqdn -o tsv)
healthy=0
for i in $(seq 1 20); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "https://${NEW_FQDN}/health" || true)
  log "Health check poging $i: HTTP ${code:-000}"
  if [ "$code" = "200" ]; then healthy=1; break; fi
  sleep 6
done

if [ "$healthy" -ne 1 ]; then
  log "MISLUKT: nieuwe revisie is niet gezond. ROLLBACK: verkeer blijft op ${PREV:-<geen>}."
  az containerapp revision deactivate -n "$APP" -g "$RG" --revision "$NEW" -o none || true
  log "Revisie $NEW gedeactiveerd. Rollback voltooid."
  exit 1
fi

if [ -z "$PREV" ]; then
  az containerapp ingress traffic set -n "$APP" -g "$RG" --revision-weight "$NEW=100" -o none
  log "Eerste uitrol: 100% verkeer naar $NEW"
  exit 0
fi

az containerapp ingress traffic set -n "$APP" -g "$RG" --revision-weight "$NEW=100" "$PREV=0" -o none
log "Verkeer geschakeld: 100% naar $NEW"

PUBLIC=$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)
sleep 10
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://${PUBLIC}/health" || true)
if [ "$code" != "200" ]; then
  log "Controle na schakelen mislukt (HTTP ${code:-000}). ROLLBACK naar $PREV."
  az containerapp ingress traffic set -n "$APP" -g "$RG" --revision-weight "$PREV=100" "$NEW=0" -o none
  az containerapp revision deactivate -n "$APP" -g "$RG" --revision "$NEW" -o none || true
  log "Rollback voltooid: $PREV is weer 100% live."
  exit 1
fi

# Houd alleen de vorige revisie (voor een snelle terugval) en de nieuwe actief.
for rev in $(az containerapp revision list -n "$APP" -g "$RG" --query "[?properties.active].name" -o tsv); do
  if [ "$rev" != "$NEW" ] && [ "$rev" != "$PREV" ]; then
    az containerapp revision deactivate -n "$APP" -g "$RG" --revision "$rev" -o none || true
  fi
done
log "Uitrol geslaagd: $NEW live, $PREV bewaard voor terugval."
