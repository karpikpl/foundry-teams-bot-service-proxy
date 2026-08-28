#!/bin/sh
set -eu

agent_name="teams-invocations-agent-csharp"
resource_group=$(azd env get-value AZURE_RESOURCE_GROUP)
subscription_id=$(azd env get-value AZURE_SUBSCRIPTION_ID)
cosmos_endpoint=$(azd env get-value COSMOS_ENDPOINT)
cosmos_account=$(printf '%s' "$cosmos_endpoint" | sed -E 's#^https://([^.]+)\..*$#\1#')

principal_id=$(
  AZD_DISABLE_AGENT_DETECT=1 azd ai agent show "$agent_name" --output json |
    jq -r '.instance_identity.principal_id'
)
if [ -z "$principal_id" ] || [ "$principal_id" = "null" ]; then
  printf 'Could not resolve the hosted agent principal ID.\n' >&2
  exit 1
fi

resource_group_scope="/subscriptions/$subscription_id/resourceGroups/$resource_group"
for role in "Foundry User" "Foundry Agent Consumer"; do
  existing=$(
    az role assignment list \
      --assignee-object-id "$principal_id" \
      --scope "$resource_group_scope" \
      --role "$role" \
      --query 'length(@)' \
      --output tsv
  )
  if [ "$existing" = "0" ]; then
    az role assignment create \
      --assignee-object-id "$principal_id" \
      --assignee-principal-type ServicePrincipal \
      --role "$role" \
      --scope "$resource_group_scope" \
      --output none
  fi
done

cosmos_scope="/subscriptions/$subscription_id/resourceGroups/$resource_group/providers/Microsoft.DocumentDB/databaseAccounts/$cosmos_account"
cosmos_role_id="$cosmos_scope/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
cosmos_assignment_count=$(
  az cosmosdb sql role assignment list \
    --resource-group "$resource_group" \
    --account-name "$cosmos_account" \
    --query "[?principalId=='$principal_id'] | length(@)" \
    --output tsv
)
if [ "$cosmos_assignment_count" = "0" ]; then
  az cosmosdb sql role assignment create \
    --resource-group "$resource_group" \
    --account-name "$cosmos_account" \
    --scope "/" \
    --principal-id "$principal_id" \
    --role-definition-id "$cosmos_role_id" \
    --output none
fi

printf 'Configured hosted identity %s for Foundry and Cosmos access.\n' "$principal_id"
