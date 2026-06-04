# Teams SSO troubleshooting

Use this guide when Teams shows `signin/failure`, `invokeerror`, or OAuth Connection Test failures for the Foundry Teams proxy bot.

## Symptom table

| Symptom operators see | First place to look |
|---|---|
| `signin/failure` with `code: resourcematchfailed` | Teams manifest `webApplicationInfo.resource`, OAuthCard `tokenExchangeResource.uri`, and Entra `identifierUris` must all point at the same `api://botid-<ssoAppId>` resource. |
| `signin/failure` with generic `code: invokeerror` | Start with Q1. Confirm Bot Service saw the `signin/*` invoke and check dependency calls / App Insights events for the status returned. |
| OAuth Connection Test returns `AADSTS7000215` | Bot Service OAuth connection client secret is wrong, expired, or pasted with the secret ID instead of the secret value. |
| OAuth Connection Test returns `AADSTS65001` | Admin/user consent is missing for the SSO app delegated permissions. |
| OAuth Connection Test returns `AADSTS50194` | App is configured for the wrong audience/tenant; verify supported account types and tenant ID. |
| OAuth Connection Test returns `AADSTS500113` | Bot Framework redirect URI is missing from the SSO app registration. |
| Teams never sends `signin/tokenExchange` | Check Teams manifest `webApplicationInfo`, `validDomains`, and preauthorized Teams clients. |
| Bot returns `412 PreconditionFailed` to `signin/tokenExchange` | Token exchange failed or duplicate exchange was detected; check Q1 and Q3 for the exact invoke and status. |

## Q1 — Bot activity timeline

Run this in the Log Analytics workspace attached to the Bot Service diagnostic setting. It joins inbound Bot Service requests to outbound dependencies by `CorrelationId` for the last hour and focuses on `signin/*` invokes.

```kusto
let lookback = 1h;
let signinRequests =
    ABSBotRequests
    | where TimeGenerated > ago(lookback)
    | extend ActivityName = tostring(coalesce(column_ifexists('ActivityName', ''), column_ifexists('Name', ''), column_ifexists('OperationName', '')))
    | extend RequestText = strcat(ActivityName, ' ', tostring(column_ifexists('Url', '')), ' ', tostring(column_ifexists('Properties', '')))
    | where RequestText has 'signin/'
    | project CorrelationId, RequestTime = TimeGenerated, ActivityName, Channel = column_ifexists('Channel', ''),
              ConversationId = column_ifexists('ConversationId', ''), UserId = column_ifexists('UserId', ''),
              RequestResult = column_ifexists('ResultType', ''), RequestStatus = column_ifexists('StatusCode', '');
let dependencies =
    ABSBotDependencies
    | where TimeGenerated > ago(lookback)
    | project CorrelationId, DependencyTime = TimeGenerated, DependencyName = column_ifexists('Name', ''),
              Target = column_ifexists('Target', ''), DependencyType = column_ifexists('DependencyType', ''),
              DependencyResult = column_ifexists('ResultCode', ''), DependencySuccess = column_ifexists('Success', '');
signinRequests
| join kind=leftouter dependencies on CorrelationId
| extend TimelineTime = coalesce(DependencyTime, RequestTime)
| project TimelineTime, CorrelationId, ActivityName, Channel, ConversationId, UserId,
          RequestStatus, RequestResult, DependencyName, Target, DependencyType, DependencyResult, DependencySuccess
| order by TimelineTime asc
```

If dependency rows are empty, verify the Bot Service diagnostic setting uses `categoryGroup: allLogs` (or includes `DependencyRequest` if that category is exposed in the tenant/API version). New diagnostic data can take several minutes to appear.

## Q2 — AAD sign-in attempts for the SSO app

Prerequisite: the tenant must export Microsoft Entra sign-in logs to this workspace (**Microsoft Entra ID → Diagnostic settings → SignInLogs**). Replace `<ssoAppId>` with the SSO app registration client ID.

```kusto
let ssoAppId = '<ssoAppId>';
union isfuzzy=true SigninLogs, AADSignInLogs
| where TimeGenerated > ago(1h)
| where AppId == ssoAppId or ResourceIdentity == ssoAppId
| extend ErrorCode = tostring(Status.errorCode), FailureReason = tostring(Status.failureReason)
| project TimeGenerated, UserPrincipalName, AppDisplayName, AppId, ResourceDisplayName,
          ResultType, ResultDescription, ErrorCode, FailureReason, ConditionalAccessStatus,
          IPAddress, CorrelationId
| order by TimeGenerated desc
```

If the workspace table is not enabled, test Graph directly instead: `az rest --method get --url "https://graph.microsoft.com/v1.0/auditLogs/signIns?\$filter=appId eq '<ssoAppId>'&\$top=10"`. This is tenant-scoped and requires sign-in log availability/licensing plus appropriate Graph permissions; do not change tenant diagnostics from an incident shell.

## Q3 — App Insights AppEvents for bot activities

Run this against the Application Insights workspace for the proxy app. It filters bot activity events to invokes and extracts the fields operators usually need to see what the bot returned.

```kusto
AppEvents
| where TimeGenerated > ago(1h)
| extend D = todynamic(column_ifexists('Properties', dynamic({})))
| extend ActivityType = tostring(coalesce(D['Activity Type'], D['ActivityType'], D['activityType']))
| where ActivityType == 'invoke'
| project TimeGenerated,
          Name = tostring(coalesce(D['Name'], column_ifexists('Name', ''))),
          StatusCode = tostring(coalesce(D['StatusCode'], D['Status Code'], D['statusCode'])),
          RecipientId = tostring(coalesce(D['Recipient ID'], D['RecipientId'], D['recipientId'])),
          ConversationId = tostring(coalesce(D['Conversation ID'], D['ConversationId'], D['conversationId'])),
          OperationId = column_ifexists('OperationId', ''),
          ParentId = column_ifexists('ParentId', '')
| order by TimeGenerated desc
```

Use this when Q1 proves the invoke reached the bot but Teams still reports a generic failure. `StatusCode` should show whether the handler returned `200`, `412`, or another invoke response.

## Pre-flight checklist

- **Teams manifest `validDomains`** — Check the generated manifest contains `token.botframework.com` and `*.botframework.com`. Fix by setting `TeamsSso__AadAppId`/`TeamsSso__Resource` so the manifest builder emits SSO fields, then reinstall the Teams app.
- **Entra `identifierUris`** — Check `az ad app show --id <ssoAppId> --query identifierUris -o tsv`; it must include `api://botid-<ssoAppId>`. Fix with `az ad app update --id <ssoAppId> --identifier-uris api://botid-<ssoAppId>`.
- **`requestedAccessTokenVersion`** — Check `az ad app show --id <ssoAppId> --query api.requestedAccessTokenVersion -o tsv`; it should be `2`. Fix with Graph PATCH: `{"api":{"requestedAccessTokenVersion":2}}`.
- **Token `idtyp` claim** — Decode the Teams SSO token or inspect sign-in logs; user-delegated tokens should not look like app-only tokens. Fix by using delegated `access_as_user` scope and Teams `webApplicationInfo`, not client credentials.
- **Delegated `User.Read`** — Check API permissions on the SSO app include Microsoft Graph `User.Read` when profile reads are expected. Fix by adding delegated `User.Read` and granting/admin-consenting as required.
- **Preauthorized Teams clients** — Check `api.preAuthorizedApplications` includes Teams desktop `1fec8e78-bce4-4aaf-ab1b-5451cc387264` and mobile/web `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` for the `access_as_user` scope. Fix by patching `preAuthorizedApplications` with that scope ID.
- **OAuth connection secret and token exchange URL** — In Bot Service OAuth Connection Settings, check client ID is `<ssoAppId>`, secret is the secret value, and token exchange URL is `api://botid-<ssoAppId>`. Fix by rotating/pasting the secret value and aligning the URL.
- **Manifest `webApplicationInfo`** — Check the Teams app package has `webApplicationInfo.id = <ssoAppId>` and `webApplicationInfo.resource = api://botid-<ssoAppId>`. Fix the proxy settings, regenerate the app package, and reinstall it in Teams.

## Common AADSTS codes

| Code | One-line fix |
|---|---|
| `AADSTS7000215` | Replace the OAuth connection secret with the current client secret **value**, not the secret ID. |
| `AADSTS65001` | Grant user/admin consent for the SSO app delegated permissions. |
| `AADSTS50194` | Align the app registration audience/tenant with the Bot Service OAuth connection tenant. |
| `AADSTS500113` | Add `https://token.botframework.com/.auth/web/redirect` to the SSO app web redirect URIs. |

## References

- [Configure diagnostics for Azure Bot Service](https://learn.microsoft.com/azure/bot-service/bot-service-manage-diagnostic-logs)
- [Azure Bot Service resource log queries](https://learn.microsoft.com/azure/bot-service/bot-service-resource-logs-queries)
- [Configure SSO for a Teams bot](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview)
- [Register an app in Microsoft Entra ID for Teams bot SSO](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad)
- [Teams app manifest `webApplicationInfo`](https://learn.microsoft.com/microsoftteams/platform/resources/schema/manifest-schema#webapplicationinfo)
- [Microsoft Entra application manifest reference](https://learn.microsoft.com/entra/identity-platform/reference-app-manifest)
- [Stream Microsoft Entra logs to Azure Monitor](https://learn.microsoft.com/entra/identity/monitoring-health/howto-integrate-activity-logs-with-azure-monitor-logs)
