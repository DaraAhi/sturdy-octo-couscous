#!/usr/bin/env bash
# Eenmalige inrichting van Azure voor de ritplanning-PoC. Uitvoeren in Azure Cloud Shell (bash) of lokaal met `az login`.
# LET OP: dit script is niet getest in jouw tenant; lees het door, voer het stap voor stap uit en pas namen aan.
# De exacte az-opties kunnen per CLI-versie verschillen: `az containerapp <commando> --help`.
set -euo pipefail

# ---- Aan te passen -------------------------------------------------------
SUFFIX="${SUFFIX:-$RANDOM}"                 # maakt namen uniek
LOC="westeurope"                            # EU-regio i.v.m. AVG
RG="draloop-rg"
ACR="draloopacr${SUFFIX}"
KV="draloop-kv-${SUFFIX}"
ENV_NAME="draloop-env"
APP="ritplanning"
LAW="draloop-logs"
TEAMS_WEBHOOK_URL="${TEAMS_WEBHOOK_URL:?Zet eerst TEAMS_WEBHOOK_URL (Teams Workflows-webhook, zie README)}"
# --------------------------------------------------------------------------

SUB=$(az account show --query id -o tsv)
TENANT=$(az account show --query tenantId -o tsv)

az group create -n "$RG" -l "$LOC" -o none
az monitor log-analytics workspace create -g "$RG" -n "$LAW" -l "$LOC" -o none
LAW_ID=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query customerId -o tsv)
LAW_KEY=$(az monitor log-analytics workspace get-shared-keys -g "$RG" -n "$LAW" --query primarySharedKey -o tsv)

az acr create -g "$RG" -n "$ACR" --sku Basic -l "$LOC" -o none
az containerapp env create -g "$RG" -n "$ENV_NAME" -l "$LOC" \
  --logs-workspace-id "$LAW_ID" --logs-workspace-key "$LAW_KEY" -o none

# Key Vault met het Teams-webhook-secret (RBAC-modus)
az keyvault create -g "$RG" -n "$KV" -l "$LOC" --enable-rbac-authorization true -o none
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee "$ME" --role "Key Vault Secrets Officer" \
  --scope "$(az keyvault show -n "$KV" --query id -o tsv)" -o none
sleep 20   # RBAC-propagatie
az keyvault secret set --vault-name "$KV" -n teams-webhook --value "$TEAMS_WEBHOOK_URL" -o none

# Eerste image bouwen in ACR (zonder lokale Docker nodig)
az acr build -r "$ACR" -t ritplanning:init .

# Container App: eerst met een voorbeeld-image, zodat de managed identity bestaat voordat we rechten uitdelen
az containerapp create -g "$RG" -n "$APP" --environment "$ENV_NAME" \
  --image mcr.microsoft.com/k8se/quickstart:latest --target-port 80 --ingress external \
  --system-assigned --revisions-mode multiple --min-replicas 1 --max-replicas 5 \
  --scale-rule-name http-schaling --scale-rule-type http --scale-rule-http-concurrency 30 -o none

MI=$(az containerapp show -g "$RG" -n "$APP" --query identity.principalId -o tsv)
az role assignment create --assignee-object-id "$MI" --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope "$(az acr show -n "$ACR" --query id -o tsv)" -o none
az role assignment create --assignee-object-id "$MI" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$(az keyvault show -n "$KV" --query id -o tsv)" -o none
sleep 30   # RBAC-propagatie

az containerapp registry set -g "$RG" -n "$APP" --server "$ACR.azurecr.io" --identity system -o none
SECRET_URI=$(az keyvault secret show --vault-name "$KV" -n teams-webhook --query id -o tsv)
az containerapp secret set -g "$RG" -n "$APP" \
  --secrets "teams-webhook=keyvaultref:${SECRET_URI},identityref:system" -o none

az containerapp ingress update -g "$RG" -n "$APP" --target-port 8080 -o none
az containerapp update -g "$RG" -n "$APP" --image "$ACR.azurecr.io/ritplanning:init" \
  --revision-suffix init \
  --set-env-vars APP_VERSION=init TEAMS_WEBHOOK_URL=secretref:teams-webhook -o none

# Verkeer vastpinnen op de benoemde revisie (nodig voor blue/green in scripts/deploy.sh)
az containerapp ingress traffic set -g "$RG" -n "$APP" --revision-weight "${APP}--init=100" -o none

FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)

# Entra ID app-registratie met app-rollen (Planner / Chauffeur / ITBeheerder)
CLIENT_ID=$(az ad app create --display-name "Ritplanning (Draloop PoC)" --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://${FQDN}/.auth/login/aad/callback" --enable-id-token-issuance true \
  --app-roles @infra/approles.json --query appId -o tsv)
SECRET=$(az ad app credential reset --id "$CLIENT_ID" --display-name containerapp --years 1 --query password -o tsv)
az ad sp create --id "$CLIENT_ID" -o none || true

# Ingebouwde authenticatie (Easy Auth): iedereen moet inloggen, alleen /health is open voor probes en pipeline
az containerapp secret set -g "$RG" -n "$APP" --secrets "microsoft-provider-authentication-secret=${SECRET}" -o none
az containerapp auth microsoft update -g "$RG" -n "$APP" --client-id "$CLIENT_ID" \
  --client-secret-name microsoft-provider-authentication-secret \
  --issuer "https://login.microsoftonline.com/${TENANT}/v2.0" --yes -o none
az containerapp auth update -g "$RG" -n "$APP" --enabled true \
  --unauthenticated-client-action RedirectToLoginPage --redirect-provider azureactivedirectory \
  --excluded-paths "/health" -o none

# Alleen gebruikers met een app-rol mogen inloggen ("Assignment required")
SP_ID=$(az ad sp show --id "$CLIENT_ID" --query id -o tsv)
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${SP_ID}" \
  --body '{"appRoleAssignmentRequired": true}' -o none

cat <<OUT

Klaar. Bewaar deze waarden:
  App-URL:        https://${FQDN}
  Resource group: ${RG}
  ACR:            ${ACR}
  App:            ${APP}
  Client ID:      ${CLIENT_ID}
  Tenant ID:      ${TENANT}
  Subscription:   ${SUB}

Volgende stappen (zie README.md): rollen toewijzen aan groepen/gebruikers, Graph-rechten (infra/grant-graph.sh),
GitHub OIDC (infra/setup-github-oidc.sh), Conditional Access-beleid in het Entra-portal.
OUT
