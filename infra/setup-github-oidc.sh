#!/usr/bin/env bash
# Laat GitHub Actions inloggen bij Azure zonder wachtwoord (federated credential / OIDC).
set -euo pipefail
GH_REPO="${GH_REPO:?bijv. jouwnaam/ritplanning-poc}"
RG="${RG:-draloop-rg}"; ACR="${ACR:?naam van je ACR}"
SUB=$(az account show --query id -o tsv); TENANT=$(az account show --query tenantId -o tsv)

APPID=$(az ad app create --display-name "github-ritplanning-deploy" --query appId -o tsv)
az ad sp create --id "$APPID" -o none
az ad app federated-credential create --id "$APPID" --parameters "{
  \"name\": \"github-main\", \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${GH_REPO}:ref:refs/heads/main\", \"audiences\": [\"api://AzureADTokenExchange\"]}" -o none

RG_ID=$(az group show -n "$RG" --query id -o tsv)
az role assignment create --assignee "$APPID" --role Contributor --scope "$RG_ID" -o none
az role assignment create --assignee "$APPID" --role AcrPush --scope "$(az acr show -n "$ACR" --query id -o tsv)" -o none

echo "Zet in GitHub (Settings > Secrets and variables > Actions):"
echo "  secrets:   AZURE_CLIENT_ID=$APPID  AZURE_TENANT_ID=$TENANT  AZURE_SUBSCRIPTION_ID=$SUB"
echo "  variables: ACR_NAME=$ACR  RESOURCE_GROUP=$RG  APP_NAME=ritplanning"
