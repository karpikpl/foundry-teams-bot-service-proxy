# foundry-teams-bot-service-proxy

[![ci](https://github.com/karpikpl/foundry-teams-bot-service-proxy/actions/workflows/ci.yml/badge.svg)](https://github.com/karpikpl/foundry-teams-bot-service-proxy/actions/workflows/ci.yml)
[![release](https://github.com/karpikpl/foundry-teams-bot-service-proxy/actions/workflows/release.yml/badge.svg)](https://github.com/karpikpl/foundry-teams-bot-service-proxy/actions/workflows/release.yml)
[![ghcr](https://img.shields.io/badge/ghcr-foundry--teams--bot--service--proxy-blue?logo=docker)](https://github.com/karpikpl/foundry-teams-bot-service-proxy/pkgs/container/foundry-teams-bot-service-proxy)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> A Microsoft Teams bot that proxies Azure Bot Service traffic to Microsoft Foundry agents via the per-agent OpenAI **Responses API**. Talk to your Foundry agents directly from Teams chat, with streaming, MCP-tool approvals, function tools, agent picker, and Teams-app manifest generation built in.

This is a **sample** that solves the practical problem of exposing Foundry agents through Bot Service when the built-in "Publish to Teams" feature isn't enough (corporate-firewall scenarios, multi-tenant routing, custom approval UX, etc.). Designed for clarity over breadth; ~1500 lines of focused C#.

---

## What you get

- **Streaming responses** in Teams 1:1 chat (using the official Teams streaming-ux protocol)
- **MCP tool approvals** with per-tool "always approve" memory
- **Function tools** dispatched in-process (no external sidecar)
- **Per-agent URL routing** — one App Service can serve many agents across many Foundry projects
- **Server-side conversations** so Foundry's portal shows tracing tied to your real conversation IDs
- **Teams-app manifest generation** — `/admin/manifest` asks for a Bot Service app ID and downloads a sideloadable `.zip` per agent
- **Cosmos-backed per-conversation state** with no key access (AAD only)
- **Docker image** published to GHCR (multi-arch, signed)
- **150+ tests** covering cards, state, JWT validation, URL safety, tool dispatch

## Architecture

```
Teams / Bot Service ─┐
Direct Line          │
Web Chat             │
                     ▼
            ┌──────────────────────┐
            │  App Service         │  This repo's container image
            │  /api/messages       │
            │  /api/messages/...   │  (URL-routed multi-agent)
            └────┬─────────────────┘
                 │ OpenAI SDK +
                 │   EntraIdAuthenticationPolicy  (AAD, scope=https://ai.azure.com)
                 │   ApiVersionPolicy             (?api-version=2025-05-15-preview)
                 ▼
            ┌──────────────────────────────────────────────┐
            │  Foundry per-agent OpenAI endpoint           │
            │  …/agents/{name}/endpoint/protocols/openai/v1│
            │   - POST /responses         (streaming SSE)  │
            │   - POST /conversations                       │
            │   - POST /conversations/{id}/items            │
            │   - DELETE /conversations/{id}                │
            └──────────────────────────────────────────────┘
            ┌──────────────────────┐
            │  Cosmos serverless   │ ← IStorage (per-conv state)
            └──────────────────────┘
```

The app holds no global agent-definition state — it fetches the Foundry agent catalog on demand with the signed-in user's OBO token and caches it per `(userObjectId, projectEndpoint)` for a short TTL. Teams activities are then forwarded to the selected Foundry per-agent URL and streaming events are piped back.

## Quick start

### Run the container locally

```bash
docker pull ghcr.io/karpikpl/foundry-teams-bot-service-proxy:latest

az login   # required so DefaultAzureCredential can authenticate against your dev creds

docker run --rm -p 8080:8080 \
  -e Foundry__ProjectEndpoint="https://your-foundry.services.ai.azure.com/api/projects/your-project" \
  -e Cosmos__Endpoint="https://your-cosmos.documents.azure.com:443/" \
  -e MicrosoftAppId="<bot-uami-client-id>" \
  -e MicrosoftAppType="UserAssignedMSI" \
  -e MicrosoftAppTenantId="<tenant-id>" \
  -e BOTSERVICE_UAMI_CLIENTID="<bot-uami-client-id>" \
  -e AZURE_CLIENT_ID="<app-uami-client-id>" \
  -v $HOME/.azure:/home/app/.azure:ro \
  ghcr.io/karpikpl/foundry-teams-bot-service-proxy:latest

curl http://localhost:8080/health        # → 200 Healthy
curl http://localhost:8080/admin/agents  # → JSON list of configured agents
```

### Deploy to Azure App Service for Containers

1. Provision: Foundry project + 1+ agents, Cosmos serverless (AAD-only), App Service plan, App Service registered to the GHCR image, Bot Service registration with the App Service's UMI as MSA app id
2. App Service identity: assign two UAMIs — one for the app (Cosmos data-plane), one shared by all Bot Service registrations that target this app
3. Roles: grant the app UMI `Cosmos DB Built-in Data Contributor` on the Cosmos account. It no longer needs `Azure AI User` / `Foundry User` on the Foundry project because catalog and chat calls use per-user OBO tokens.
4. App settings: the env vars listed in [Configuration](#configuration)
5. Bot Service endpoint: `https://{app}.azurewebsites.net/api/messages`

See [docs/deploy.md](docs/deploy.md) for a step-by-step.

### Sideload to Teams

1. Visit `https://{app}.azurewebsites.net/admin` for browser chat or manifest generation. The popup can use the configured default Foundry project or another host/project.
2. For manifests, pick an agent and paste the Azure Bot Service **Microsoft App ID** (Application/client ID). The app generates the `.zip` directly; no bot registration store is used.
3. In Teams: **Apps → Manage your apps → Upload an app → Upload a custom app** → pick the zip.
4. Open a 1:1 chat with the bot. Type `/help`.

## Configuration

| Env var | Required | Description |
|---|---|---|
| `Foundry__ProjectEndpoint` | ✅ | Project URL up to `.../api/projects/{name}` |
| `Foundry__CatalogCacheSeconds` | optional | Per-user agent catalog TTL in seconds; defaults to `300` |
| `Cosmos__Endpoint` | ✅ | Cosmos serverless URL (AAD auth, no keys) |
| `Cosmos__Database` | optional | Defaults to `botstate` |
| `Cosmos__Container` | optional | Defaults to `conversations` |
| `MicrosoftAppId` | ✅ | Bot Service registration UMI client id |
| `MicrosoftAppType` | ✅ | `UserAssignedMSI` |
| `MicrosoftAppTenantId` | ✅ | AAD tenant id |
| `BOTSERVICE_UAMI_CLIENTID` | ✅ | UMI client id the JWT middleware validates `aud` against |
| `AZURE_CLIENT_ID` | optional | If set, `ManagedIdentityCredential` targets this specific UAMI |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | optional | App Insights wiring |
| `AdminChatAuth__Enabled` | required for catalog/chat | Set `true` to require Entra ID sign-in for `/admin` catalog, manifest, and chat endpoints |
| `AdminChatAuth__TenantId` | optional | Tenant for browser chat sign-in; falls back to `TeamsSso__TenantId` |
| `AdminChatAuth__ClientId` | optional | AAD app client ID; falls back to `TeamsSso__AadAppId` |
| `AdminChatAuth__ClientSecret` | required when enabled | Client secret for the browser chat confidential client flow |
| `AdminChatAuth__Instance` | optional | Authority instance; defaults to `https://login.microsoftonline.com/` |

Cosmos is only used for per-conversation bot state. Manifest generation no longer stores bot ↔ agent registrations; operators paste the Bot Service app ID into the inline manifest form.

### Required: per-user identity for `/admin` and Teams chat

Agent catalog discovery no longer runs in the proxy's managed identity. The first authenticated `/admin` request or Teams SSO turn acquires a user-delegated Foundry token, fetches that user's catalog, and caches it under `(userObjectId, projectEndpoint)`. If OBO is unavailable, catalog lookups return no agents instead of falling back to UAMI.

Setup:

1. Reuse the Teams SSO AAD app when possible. If `TeamsSso__AadAppId` is already set, leave `AdminChatAuth__ClientId` unset so the app falls back to it.
2. In the AAD app registration, add a **Web** platform redirect URI: `https://<host>/signin-oidc`.
3. Ensure the app has delegated permission for Azure AI Foundry and grant/admin-consent it as appropriate.
4. Create a client secret and set `AdminChatAuth__ClientSecret` from App Service settings or Key Vault.
5. Enable the feature:

```bash
AdminChatAuth__Enabled=true
AdminChatAuth__TenantId=<tenant-id>          # optional if TeamsSso__TenantId is set
AdminChatAuth__ClientId=<aad-app-client-id> # optional if TeamsSso__AadAppId is set
AdminChatAuth__ClientSecret=<secret>
AdminChatAuth__Instance=https://login.microsoftonline.com/
```

When enabled, the signed-in user's token is acquired for `https://ai.azure.com/.default` and sent to Foundry for catalog and chat requests. The cache TTL is configurable with `Foundry__CatalogCacheSeconds` (default `300`). Workload Identity or managed-credential confidential-client auth could remove the client secret in a future version; v1 uses `ClientSecret` only.

### Agent catalog

There is no background refresh loop and no static `Foundry__Agents__*` catalog. `/agents`, routed bot turns, manifest generation, and `/admin/chat` fetch the active agents from Foundry on demand using the signed-in user's OBO token. The container app identity needs no Foundry RBAC.

### URL-routed multi-agent

Same App Service can serve **N** agents from **N** Foundry projects. Each Bot Service registration just points to a different URL:

```
POST /api/messages                                          → default agent
POST /api/messages/{foundryHost}/{project}/{agent}          → that specific agent
```

Examples:

```
/api/messages/aif-myacct/proj-prod/docs-assistant
/api/messages/https%3A%2F%2Faif.example.com%2Fapi%2Fprojects%2Fp/code-helper
```

## Commands cheat sheet

| Command | Effect |
|---|---|
| `/help` | Show this card |
| `/agents` | Pick which Foundry agent to chat with |
| `/agent` | Show active agent + endpoint |
| `/reset` | Delete the Foundry conversation, start fresh |
| `/stop` | Cancel the running agent turn |
| `/tokens` | Show cumulative + last-run token usage |
| `/usage on\|off` | Toggle the per-run usage footer |
| `/tools on\|off` | Show or hide tool-call cards (off by default — turn on for troubleshooting) |
| `/auto list\|clear` | Manage MCP tools the user has marked "always approve" |

## Build from source

```bash
git clone https://github.com/karpikpl/foundry-teams-bot-service-proxy.git
cd foundry-teams-bot-service-proxy

dotnet test                    # 150+ tests
docker build -t fb:local .
```

## Development tips

- The `samples/foundry-responses-api.http` file is a VS Code REST Client playground for probing the Foundry API directly (handy for debugging without the bot in the loop)
- Diagnostic fallback: unknown SSE events and unknown response item types are logged at Warning level with the raw JSON — useful when Foundry adds new tool types the OpenAI SDK doesn't model yet
- The container is multi-arch (`linux/amd64` + `linux/arm64`) and is built and signed by the release workflow on every `vX.Y.Z` tag

## How it works (the short version)

1. **One `OpenAIClient` per per-agent URL.** Wrapped with two pipeline policies: a bearer-token auth policy that pulls AAD tokens from `TokenCredential` (scope `https://ai.azure.com/.default`), and an api-version policy that appends `?api-version=2025-05-15-preview`.
2. **Server-side conversations.** First user message creates a Foundry conversation; subsequent turns reuse it. `/reset` deletes it.
3. **Streaming.** The Foundry Responses API returns standard OpenAI SSE; we forward text deltas to Teams as streaming chunks (via the `streaminfo` Adaptive Card entity protocol).
4. **MCP approvals.** When the agent wants to call an MCP tool that requires approval, Foundry emits an `mcp_approval_request` item. The bot and `/admin/chat` pause, show an Approve/Deny card, then resume with a `mcp_approval_response` input item chained via `previous_response_id`.
5. **Function tools.** Same pattern with `function_call` items, dispatched by `FunctionToolDispatcher` (sample implementations: `get_current_time`, `calculate`).
6. **Per-conversation state in Cosmos.** Just `ConversationId`, `AgentEndpoint`, token counters, and the auto-approve set. ETag-`*` writes (Bot Framework guarantees per-conversation serialization, so last-writer-wins is safe).

## Repository layout

```
.
├── src/AgentChat/              # The bot (Program.cs + Bots/ + Services/ + Foundry/ + Controllers/ + Middleware/)
├── tests/AgentChat.Tests/      # 150+ xUnit tests
├── samples/                    # .http file for probing Foundry directly
├── docs/                       # Deployment notes
├── .github/workflows/          # ci.yml + release.yml
├── Dockerfile                  # Multi-stage, runs as non-root
└── README.md
```

## License

[MIT](LICENSE)

## Disclaimer

This is a sample. It's been tested against real Foundry projects but is not a Microsoft product and has no SLA. Pin to a release tag (`v0.1.0` etc.) rather than `latest` in production.
