# Copilot Instructions

## Project Overview

`foundry-teams-bot-service-proxy` is an ASP.NET Core (`net10.0`) service that bridges Microsoft Teams/Bot Framework activities to Azure AI Foundry agents. In the deployment model used by `msft-mfg-ai/ai-foundry-config-testing` (`options-infra/foundry-byo-vnet-teams`), each Foundry agent has its own AAD app registration and two Azure Bot Services: a **direct** bot (Foundry service principal → `activityprotocol`) and a **proxy** bot (this service's proxy app registration → this container). This repo implements only the proxy leg: it receives activities for N proxy bots, routes each turn to the right Foundry project/agent, and sends replies back through Bot Framework.

## Architecture Map

Key files and directories under `src/AgentChat/`:

```
src/AgentChat/
├── Program.cs              # DI, auth, middleware, controllers, health checks
├── appsettings.json        # Defaults + config key documentation
├── Auth/                   # Admin OIDC/OBO + Bot Framework FIC outbound auth
├── Bots/                   # Teams bot, state, cards, streaming, MCP/function tools
├── Controllers/            # Bot, admin manifest/chat, proactive notify endpoints
├── Foundry/                # Foundry REST/OpenAI SDK helpers and user-token scope
├── Middleware/             # Inbound Bot Service JWT pre-validation
├── Services/               # Agent catalog/cache and Teams SSO helpers
└── wwwroot/                # Browser admin/chat static UI assets
```

- `Program.cs`
  - Registers `AgentService`, `AgentClientCache`, `TeamsSsoService`, Cosmos-backed `IStorage`, `ConversationStore`, `FicServiceClientCredentialsFactory`, `AdapterWithErrorHandler`, and `FoundryBot`.
  - Enables `AdminChatAuth` only when configured, then wires `BotServiceJwtMiddleware` before authorization.
- `Controllers/`
  - `BotMessagesController`: POST `/api/messages` and `/api/messages/{foundryHost}/{project}/{agent}` into the Bot Framework adapter.
  - `ManifestController`: `/admin/*` landing page, agent listing, and Teams manifest zip generation; protected with `[ServiceFilter(typeof(AdminChatAuthFilter))]`.
  - `ChatTestController`: `/admin/chat/*` browser test harness with SSE streaming; protected with `AdminChatAuthFilter` and `AuthorizeForScopes`.
  - `NotifyController`: `/api/notify` proactive message helper using stored conversation references.
- `Middleware/`
  - `BotServiceJwtMiddleware`: route-bound inbound JWT guard for `/api/messages*`. **Does not touch `/api/passthrough/*`.**
- `Passthrough/`
  - `PassthroughEndpoints` + `ActivityProtocolTransformer`: YARP-based **transparent reverse proxy** at `POST /api/passthrough/{foundry}/{project}/{agent}` → Foundry's `…/endpoint/protocols/activityprotocol` URL. Forwards the inbound JWT (signed by Bot Service for the Foundry agent SP) **unchanged** so Foundry validates it normally; the proxy contributes only a network hop + path rewrite. Used when Foundry public network access is disabled and Bot Service must reach Foundry through a VNet-attached relay. The bot service for this route is configured exactly like the "direct" bot (`msaAppId` = Foundry agent SP) except the endpoint points at our container.
- `Auth/`
  - `AdminChatAuthOptions`: OIDC/OBO settings and fallbacks.
  - `AdminChatAuthFilter`: challenges anonymous `/admin/*` users when admin auth is enabled.
  - `FicServiceClientCredentialsFactory`: mints per-bot Bot Framework tokens via Federated Identity Credential (FIC), no per-bot secrets.
- `Bots/`
  - `FoundryBot`: Teams activity handler, commands (`/agents`, `/reset`, `/stop`, `/usage`, `/tools`, `/auto`, `/tokens`, `/signout`), Foundry turn execution, MCP approval flow.
  - `StreamingMessageHelper`: Teams 1:1 streaming UX helper; it sends typing/informative/streaming/final activities and must finalize open streams.
  - `ConversationStore`/`ConversationState`: Cosmos-backed state and conversation references.
  - `ManifestBuilder`, `AdaptiveCardBuilder`, `McpApproval`, `FunctionToolDispatcher`, `AgentMessageRenderer`, `TurnRouting`, `UrlSafety`, `ConsentLinkParser`.
- `Foundry/`
  - `FoundryAgentsApi`: project-level `/agents` list API and URL composition helpers.
  - `FoundryClient`: OpenAI SDK client configured for Foundry per-agent endpoints.
- `Services/`
  - `AgentService`: per-project agent catalog cache; uses UAMI unless a `FoundryUserAuthScope` OBO token is active.
  - `AgentClientCache`: per-agent Foundry client cache.
  - `TeamsSsoService`: Bot Framework token service integration for Teams SSO.

## Configuration Contract With Infra

The deployment contract is shared with `options-infra/foundry-byo-vnet-teams` in the infra repo. Treat these keys as compatibility-sensitive:

- `Bots:Routes`
  - JSON string array parsed by `BotServiceJwtMiddleware`, `FicServiceClientCredentialsFactory`, and `ManifestController`.
  - Current shape: `[{ "AgentName": "...", "ProxyAppId": "...", "DirectAppId": "..." }]`.
  - Back-compat shape: `[{ "AgentName": "...", "AppId": "..." }]`.
  - `EffectiveProxyAppId` means `ProxyAppId` if present, otherwise `AppId`; this is the audience for inbound proxy traffic and the app ID used for outbound BF tokens.
- `MicrosoftAppTenantId`
  - Customer tenant ID for single-tenant bot auth; code also falls back to `AZURE_TENANT_ID` in a few places.
- `TeamsApp:BackendAppId` / `TeamsApp:BackendSecret`
  - Shared backend app registration used for Teams SSO manifest wiring and admin OBO.
  - The infra parameter is named `teamsAppBackendId`; some docs may say `BackendId`, but the current service code reads `TeamsApp:BackendAppId`.
  - `TeamsApp:BackendSecret` is the only long-lived secret in this design.
- `AdminChatAuth:ClientId` / `AdminChatAuth:ClientSecret`
  - Admin OIDC/OBO confidential-client settings.
  - `ClientId` falls back to `TeamsApp:BackendAppId` then `TeamsSso:AadAppId`.
  - `ClientSecret` falls back to `TeamsApp:BackendSecret`.
- UAMI identity
  - The container is bound to a user-assigned managed identity via `AZURE_CLIENT_ID`.
  - That UAMI principal is the FIC subject trusted by each proxy bot app registration.
  - Cosmos and default Foundry catalog calls use `DefaultAzureCredential`/managed identity when no user OBO token is in scope.

## Auth Model: Three Flows

### 1. Inbound: Bot Service → `/api/messages/{foundry}/{project}/{agent}`

- `BotServiceJwtMiddleware` runs for `/api/messages*` before the Bot Framework adapter.
- It parses the URL shape `/api/messages/{foundry}/{project}/{agent}` and uses the `{agent}` segment to look up the expected proxy app ID from `Bots:Routes`.
- Valid issuers are:
  - `https://sts.windows.net/{tenantId}/`
  - `https://login.microsoftonline.com/{tenantId}/v2.0`
  - `https://api.botframework.com`
- Azure Bot Service can sign channel→bot tokens with the Bot Framework issuer even for SingleTenant bots.
- The real security boundary is the route-bound audience check: `aud == route.EffectiveProxyAppId`.
- The middleware does not verify JWT signatures; comments state that crypto/JWKS validation remains the CloudAdapter's job.

### 2. Outbound: container → Bot Framework replies

- `FicServiceClientCredentialsFactory` replaces per-bot secrets with FIC.
- For each outbound reply:
  1. The container UAMI gets a managed-identity token for `api://AzureADTokenExchange/.default`.
  2. The service POSTs that token as `client_assertion` to `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token`.
  3. It sets `client_id=<proxy bot appId>` and `scope=https://api.botframework.com/.default`.
  4. AAD validates the FIC on that bot app registration and returns a Bot Framework token.
- Tokens are cached per proxy app ID until near expiry.
- Result: every proxy bot can send replies with its own app identity without storing secrets on the bot registrations.

### 3. Admin OBO: `/admin/*` → Foundry as signed-in user

- `AdminChatAuthFilter` enforces OpenID Connect sign-in when `AdminChatAuth:Enabled=true`.
- `ManifestController` and `ChatTestController` both carry `[ServiceFilter(typeof(AdminChatAuthFilter))]`.
- `ChatTestController` also declares `AuthorizeForScopes` for `https://ai.azure.com/user_impersonation`.
- `ManifestController.BeginFoundryUserScopeAsync()` acquires a user token through Microsoft.Identity.Web and pushes it into `FoundryUserAuthScope`.
- `FoundryAgentsApi.ListAgentsAsync()` honors `FoundryUserAuthScope.Current` before falling back to the workload credential.
- This is why admin/manifest calls can use Foundry as the signed-in user and the container UAMI does not need `Azure AI User` RBAC for those paths.

## Testing

Run from the repo root:

```bash
dotnet test --configuration Release --no-restore --verbosity minimal
```

Verified locally: `Passed: 219, Failed: 0, Skipped: 0, Total: 219`.

Notable tests:

- `BotServiceJwtMiddlewareTests`
  - 11 tests for missing/bad auth headers, malformed JWTs, wrong issuer, wrong audience, valid AAD v2 issuer, valid Bot Framework issuer, case-insensitive audience, routed path protection, non-message bypass, and disabled config.
  - The routed path is `/api/messages/{foundry}/{project}/{agent}` (5 segments after splitting without empty entries).
- `ChatTestControllerTests.Admin_chat_auth_filter_protects_admin_controllers`
  - Verifies both `ManifestController` and `ChatTestController` carry `AdminChatAuthFilter`.
- `StreamingMessageHelperTests`
  - Covers Teams streaming helper behavior; avoid changes that leave streams open or skip finalization.

## Release Workflow

- Current infra-pinned version: `0.9.1`.
- Tag `vX.Y.Z` (or prerelease `vX.Y.Z-rc.N`, `vX.Y.Z-diag.N`) in this repo to trigger `.github/workflows/release.yml`.
- Release CI runs tests, builds a multi-arch image, pushes to GHCR, signs with cosign (stable tags only), and **creates a GitHub Release**.
  - Stable `vX.Y.Z` → image tags `X.Y.Z`, `X.Y`, `X`, `latest`, `sha-<short>` + standard GitHub Release.
  - Prerelease `vX.Y.Z-*` → image tag `X.Y.Z-…` + `sha-<short>` only (no floating tags) + GitHub Release marked as **prerelease**.
- The infra repo pins the consumed image in `options-infra/foundry-byo-vnet-teams/main.bicep` as `existingImage` passed to `modules/aca/container-app.bicep`. Bump it whenever a new stable release ships.

## Repository Hygiene Rules

These are enforced conventions — agents should refuse to skip them without explicit operator override.

- **Every pushed tag MUST result in a GitHub Release** (stable or prerelease). Do not push tags whose Release entry would be missing; the release workflow now creates them automatically — verify on the Releases page after each tag push.
- **Every branch MUST have an open or merged PR.** No long-lived "scratch" branches on the remote. Delete the branch immediately after merge (GitHub setting + `git push origin --delete <branch>` for backfill).
- **Prerelease workflow:** use `vX.Y.Z-rc.N` or `vX.Y.Z-diag.N` tags for testing in the live container app. These produce **prerelease** GitHub Releases. Promote to stable by tagging `vX.Y.Z` on the SAME commit that was last in rc.
- **Diagnostic / log-spew patches** (e.g. JWT claim logging) are always prereleases. Strip them before cutting a stable `vX.Y.Z`.
- **Goal: clean linear history on `main`** + a clean Releases page that lets anyone see what changed between any two versions.

## Common Traps

- **Bot Service tokens use `iss=https://api.botframework.com`** — do not write middleware that only accepts AAD tenant issuers; audience is the real boundary.
- **Single-quoted shell strings cannot contain `'`** — use heredocs for inline Python or multi-line scripts.
- **`Bots:Routes` JSON shape changes are breaking** — bump major if you rename fields or remove the `AppId` fallback.
- **SSE/streaming responses must not be buffered end-to-end** — `ChatTestController` streams browser SSE and `StreamingMessageHelper` incrementally sends Teams typing/streaming/final activities.
- **Do not rename `TeamsApp:BackendAppId` to `TeamsApp:BackendId` casually** — current code reads `BackendAppId`; changing it requires coordinated infra and code updates.

## References

- ABS ↔ Teams message flow (best overview): https://moimhossain.com/2025/05/22/azure-bot-service-microsoft-teams-architecture-and-message-flow/
- Bot Framework connector authentication and valid issuers: https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication
- Workload identity federation / FIC overview: https://learn.microsoft.com/en-us/azure/active-directory/develop/workload-identity-federation
- OAuth 2.0 on-behalf-of flow: https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow
- Foundry agents to Teams deep dive: https://journeyofthegeek.com/2026/05/20/microsoft-foundry-publishing-agents-to-teams-deep-dive-part-1/
- Foundry agents through corporate firewall: https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/foundry-agents-and-custom-engine-agents-through-the-corporate-firewall/4502218
