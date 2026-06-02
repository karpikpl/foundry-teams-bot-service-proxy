# Deployment notes

This walks through deploying the bot to **Azure App Service for Containers** with **Cosmos serverless** for state and **Bot Service** as the Teams front door. Everything is AAD-only (no keys).

Reference your own Terraform/Bicep — this is a checklist, not a copy-pasteable script.

## Resources you need

| Resource | Why |
|---|---|
| **Azure AI Foundry account + project** | Where your agents live |
| **One or more Foundry agents** | Provisioned via Foundry portal or TF — see [Creating agents](#creating-agents) below |
| **Cosmos DB serverless account** | Per-conversation state. SQL API. AAD-only (disable key access) |
| **App Service plan** (Linux, B1+) | Hosts the container |
| **App Service** (Linux container) | Pulls from `ghcr.io/karpikpl/foundry-teams-bot-service-proxy:vX.Y.Z` |
| **2 × User-assigned managed identity** | One for the app, one shared by Bot Service registrations |
| **Bot Service registration** (Azure Bot, multi-tenant or single-tenant) | The Teams channel front door |
| **Application Insights** (optional) | For traces + the diagnostic fallback logs |

## Identity model

Two UMIs:

1. **App UMI** (`uami-app`) — assigned to the App Service. Used by `DefaultAzureCredential` to obtain tokens for Foundry (`https://ai.azure.com/.default`) and Cosmos. Holds:
    - `Azure AI User` on the Foundry account
    - `Cosmos DB Built-in Data Contributor` on the Cosmos account

2. **Bot UMI** (`uami-bot`) — assigned to the Bot Service registration as its MSA app id. Multiple Bot Service registrations can share this UMI, which gives you N Teams app entries pointing at the same App Service.

Both UMIs are assigned to the App Service's `userAssignedIdentities`. The app one is what `AZURE_CLIENT_ID` points to.

## App settings

| Setting | Value |
|---|---|
| `WEBSITES_PORT` | `8080` |
| `Foundry__ProjectEndpoint` | `https://{foundry}.services.ai.azure.com/api/projects/{project}` |
| `Cosmos__Endpoint` | `https://{cosmos}.documents.azure.com:443/` |
| `MicrosoftAppId` | `<bot-uami-client-id>` |
| `MicrosoftAppType` | `UserAssignedMSI` |
| `MicrosoftAppTenantId` | `<tenant-id>` |
| `BOTSERVICE_UAMI_CLIENTID` | `<bot-uami-client-id>` (same as MicrosoftAppId in single-bot setup) |
| `AZURE_CLIENT_ID` | `<app-uami-client-id>` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | _(optional)_ |

## Creating agents

The bot doesn't provision agents — that's a deliberate choice so Foundry portal/Terraform owns agent lifecycle. Create them however you prefer; the bot just needs the agent **name** to live at `…/api/projects/{project}/agents/{name}/endpoint/protocols/openai/v1`.

If you want to keep the default catalog (`docs-assistant`, `code-helper`, `orchestrator`), create those 3 agents. Otherwise override with `Foundry__Agents__*` env vars (see [README](../README.md#configuration)).

Sample Foundry agent definition (REST):

```bash
POST https://{foundry}.services.ai.azure.com/api/projects/{project}/agents?api-version=2025-05-15-preview
Authorization: Bearer <token-for-https://ai.azure.com>
Content-Type: application/json

{
  "name": "docs-assistant",
  "definition": {
    "kind": "declarative",
    "model": "gpt-4o",
    "instructions": "You are a documentation assistant. Search MS Learn docs via MCP and cite sources.",
    "tools": [
      {
        "type": "mcp",
        "server_label": "microsoft_learn",
        "server_url": "https://learn.microsoft.com/api/mcp",
        "require_approval": "always"
      }
    ]
  }
}
```

## Bot Service registration

Channels:

- **Microsoft Teams** — required
- **Direct Line** — useful for Web Chat testing during development
- **Web Chat** — optional, included with Direct Line

Endpoint: `https://{app}.azurewebsites.net/api/messages`

If you want URL-routed multi-agent: configure additional Bot Service registrations (each is one Teams app entry) and point each at `https://{app}.azurewebsites.net/api/messages/{foundryHost}/{project}/{agent}`.

## Multi-project routing

One App Service can serve Bot Service registrations for multiple Foundry projects by using URL-routed endpoints:

```text
Teams app A → /api/messages/foundry-a/project-a/agent-one → project-a catalog
Teams app B → /api/messages/foundry-b/project-b/agent-two → project-b catalog
```

For routed conversations, the bot derives the project from the incoming agent endpoint and scopes `/agents`, `/agent`, and picker submissions to that project. The configured `Foundry__ProjectEndpoint` remains the fallback for the default `/api/messages` route and for any route that cannot be decoded.

## Teams app manifest

Generate via `/admin/manifest` — the page lists configured agents and lets you download a sideloadable `.zip` per agent. You can also POST to `/admin/manifest/{agentName}` for an automated flow.

To sideload manually:

1. Teams → **Apps** → **Manage your apps** → **Upload an app** → **Upload a custom app**
2. Pick the `.zip`
3. Open a 1:1 chat with the bot

For organization-wide rollout, submit the manifest to your Teams admin center.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `/health` returns 503 | App startup failure — check container logs for `Failed to initialize` |
| Bot responds with "An error occurred" in Teams | Look for `Foundry HTTP 4xx` in container logs — usually `MicrosoftAppId` mismatch or missing AAD role |
| Streaming text doesn't appear in Teams | Channel must be `msteams` and conversation type `personal` (1:1) — group/channel chats don't support streaming-ux |
| `Bot Connector HTTP 400` | Activity payload too large (~28 KB Teams limit) — see the per-conversation `/tools off` default |
| Unknown SSE event logged | Foundry added something the OpenAI SDK doesn't model — log shows full JSON, file an issue |

## Observability

The bot emits the following at `Information` level:

- `Configured N agents` — startup summary of the agent catalog
- Bot Framework activity send/receive logs (Kestrel + HttpClient)

And at `Warning`:

- `Unhandled stream event` / `Unhandled output item` — Foundry sent something the SDK doesn't model; raw JSON included
- `Failed cleanup of old conv during agent switch` — non-fatal; the conv may already be gone

Hook App Insights via `APPLICATIONINSIGHTS_CONNECTION_STRING` and these all appear in `traces`.
