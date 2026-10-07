#!/usr/bin/env bash
# Geeft de managed identity van de Container App ALLEEN rechten op één SharePoint-site (Sites.Selected).
# Uitvoeren als Global Administrator / Privileged Role Administrator.
# Let op: Microsoft Graph biedt geen applicatie-machtiging om berichten in een Teams-kanaal te plaatsen
# (ChannelMessage.Send is alleen 'delegated'). Daarom gebruikt deze PoC een Teams Workflows-webhook (zie README).
set -euo pipefail
RG="${RG:-draloop-rg}"; APP="${APP:-ritplanning}"
SITE_HOSTPAD="${SITE_HOSTPAD:?bijv. contoso.sharepoint.com:/sites/Planning}"

MI=$(az containerapp show -g "$RG" -n "$APP" --query identity.principalId -o tsv)
GRAPH_SP=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
ROLE_ID=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 \
  --query "appRoles[?value=='Sites.Selected' && contains(allowedMemberTypes,'Application')].id | [0]" -o tsv)

# 1. Sites.Selected toekennen aan de managed identity
az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${MI}/appRoleAssignments" \
  --body "{\"principalId\":\"${MI}\",\"resourceId\":\"${GRAPH_SP}\",\"appRoleId\":\"${ROLE_ID}\"}" -o none

# 2. Site-id en drive-id opzoeken
SITE_ID=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/sites/${SITE_HOSTPAD}" --query id -o tsv)
DRIVE_ID=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/sites/${SITE_ID}/drive" --query id -o tsv)
MI_APPID=$(az ad sp show --id "$MI" --query appId -o tsv)

# 3. Schrijfrechten op precies deze site. Dit vraagt rechten die de az-CLI-token niet altijd heeft;
#    lukt dit niet, doe dan hetzelfde in Graph Explorer (POST /sites/{id}/permissions) of met PnP PowerShell:
#    Grant-PnPAzureADAppSitePermission -AppId <MI_APPID> -DisplayName ritplanning -Site <site-url> -Permissions Write
az rest --method POST --uri "https://graph.microsoft.com/v1.0/sites/${SITE_ID}/permissions" \
  --body "{\"roles\":[\"write\"],\"grantedToIdentities\":[{\"application\":{\"id\":\"${MI_APPID}\",\"displayName\":\"ritplanning\"}}]}" -o none

az containerapp update -g "$RG" -n "$APP" --set-env-vars GRAPH_DRIVE_ID="$DRIVE_ID" -o none
echo "Klaar. GRAPH_DRIVE_ID=$DRIVE_ID is ingesteld op de Container App."
