# Teams bot tokens and Teams-managed apps

This note captures what we observed while testing a Teams-managed bot through a devtunnel, and how it maps to this repository's auth model. Read it when you need to understand what Microsoft Teams / Azure Bot Service sends to a bot's `/api/messages` endpoint, when Bot Framework credentials are actually needed, or how `teams app create` can point a Teams-managed bot at any HTTPS endpoint without using this repo's proxy container.

This repo has three separate auth flows: inbound Bot Service JWT checks, outbound Bot Framework reply tokens via federated identity credentials (FIC), and admin-browser OBO for Foundry user calls. The summary in [`.github/copilot-instructions.md`](../.github/copilot-instructions.md#auth-model-three-flows) is the best high-level map.

## What Teams sends to `/api/messages`

A local test used `@microsoft/teams.cli` to create a Teams-managed app named `joe-dump`, pointed at a devtunnel URL ending in `/api/messages`. The listener only logged requests and returned `200`; it did not use this repository's container.

The incoming HTTP request looked like this after redaction:

```http
POST /api/messages HTTP/1.1
User-Agent: Microsoft-SkypeBotApi (Microsoft-BotFramework/3.0)
Authorization: Bearer <redacted>
x-ms-conversation-id: a:<conversation-id>
x-ms-tenant-id: <tenantId>
Content-Type: application/json; charset=utf-8
```

The decoded JWT header and payload from a local `message` activity were:

```json
{
  "header": {
    "alg": "RS256",
    "kid": "SERixAMWrs46-gqrTrtMrkfbnuE",
    "x5t": "SERixAMWrs46-gqrTrtMrkfbnuE",
    "typ": "JWT"
  },
  "payload": {
    "serviceurl": "https://smba.trafficmanager.net/amer/<tenantId>/",
    "nbf": 1784858820,
    "exp": 1784862420,
    "iss": "https://api.botframework.com",
    "aud": "c7bc7490-90e1-442c-be38-ab9541700c14"
  }
}
```

Notes from that capture:

- `aud` is the bot's own app id. It is not a Foundry app id and it is not an AAD resource URI.
- `iss` is `https://api.botframework.com`. It is not `https://login.microsoftonline.com/{tenantId}/v2.0` in this capture.
- `nbf` to `exp` was one hour.
- The JWT payload had no `tid`, no `oid`, no `upn`, and no user claims. The tenant appeared in `x-ms-tenant-id`, in the JWT `serviceurl` claim, and in the activity's `serviceUrl` / conversation tenant fields.

That issuer detail matters. A common bug is writing bot middleware that only accepts AAD tenant issuers. Bot Framework channel-to-bot tokens can legitimately use the Bot Framework issuer.

This repo's route-bound guard accepts three issuers:

1. `https://sts.windows.net/{tenantId}/`
2. `https://login.microsoftonline.com/{tenantId}/v2.0`
3. `https://api.botframework.com`

See [`BotServiceJwtMiddleware.cs` lines 145-163](../src/AgentChat/Middleware/BotServiceJwtMiddleware.cs#L145-L163). The same middleware then requires the JWT `aud` claim to equal the app id configured for that route, not merely any app id owned by the deployment ([lines 165-173](../src/AgentChat/Middleware/BotServiceJwtMiddleware.cs#L165-L173)). It resolves the expected app id from the `/api/messages/{foundry}/{project}/{agent}` path and `Bots:Routes` ([lines 104-122](../src/AgentChat/Middleware/BotServiceJwtMiddleware.cs#L104-L122)). Signature and JWKS validation are left to the Bot Framework adapter, as documented in the middleware comments ([lines 31-35](../src/AgentChat/Middleware/BotServiceJwtMiddleware.cs#L31-L35)).

The activity bodies we saw included these types:

```json
{
  "type": "conversationUpdate",
  "channelId": "msteams",
  "serviceUrl": "https://smba.trafficmanager.net/amer/<tenantId>/",
  "recipient": { "id": "28:<bot-appId>", "name": "joe-dump" },
  "from": { "id": "29:<teams-user-id>", "aadObjectId": "<user-aad-oid>" }
}
```

```json
{
  "type": "installationUpdate",
  "action": "add",
  "channelId": "msteams",
  "serviceUrl": "https://smba.trafficmanager.net/amer/<tenantId>/"
}
```

```json
{
  "type": "message",
  "text": "hi",
  "channelId": "msteams",
  "serviceUrl": "https://smba.trafficmanager.net/amer/<tenantId>/",
  "recipient": { "id": "28:<bot-appId>" },
  "from": { "id": "29:<teams-user-id>", "aadObjectId": "<user-aad-oid>" }
}
```

Useful fields:

| Field | Meaning |
|---|---|
| `channelId` | `msteams` for Teams channel traffic. |
| `serviceUrl` | Bot Framework connector base URL to use for outbound replies. |
| `recipient.id` | Starts with `28:` and identifies the bot as a Teams user. |
| `from.id` | Starts with `29:` and identifies the Teams user in Bot Framework/Teams form. |
| `from.aadObjectId` | The user's Entra object id when Teams provides it. |
| `conversation.tenantId` / `channelData.tenant.id` | Tenant context in the activity body. |

## When do you need bot credentials?

The inbound JWT and outbound reply token are different concerns.

| Operation | Bot credentials needed? | Why |
|---|---:|---|
| Inbound JWT validation | No | Bot Framework signing keys are public. Middleware/adapters can validate issuer, audience, and signature without proving they own the bot app id. |
| Reply on the same HTTP response | No | For protocol shapes that support returning an activity inline, no separate call to Bot Framework is made. |
| Outbound POST to `serviceUrl` | Yes | Typing, streaming, proactive sends, and normal connector replies call `https://.../v3/conversations/.../activities` and require a Bot Framework access token from AAD. |

This repo's FIC path is for the outbound Bot Framework reply path. The current implementation is [`FicAccessTokenProvider`](../src/AgentChat/Auth/FicAccessTokenProvider.cs), the M365 Agents SDK replacement for the old `FicServiceClientCredentialsFactory`. Its comments describe the flow: the container UAMI gets an `api://AzureADTokenExchange` token, posts it to AAD as a client assertion with `client_id=<bot-appId>` and `scope=https://api.botframework.com/.default`, then receives a Bot Framework token cached until near expiry ([lines 10-26](../src/AgentChat/Auth/FicAccessTokenProvider.cs#L10-L26), [lines 90-107](../src/AgentChat/Auth/FicAccessTokenProvider.cs#L90-L107)).

That FIC flow is not how the bot calls Foundry as a user. Foundry user OBO is a separate admin-browser flow: `AdminChatAuthFilter` challenges unauthenticated `/admin/*` users ([`AdminChatAuthFilter.cs` lines 7-15](../src/AgentChat/Auth/AdminChatAuthFilter.cs#L7-L15)), and `FoundryUserAuthScope` carries a per-request user token for Foundry calls ([`FoundryClient.cs` lines 7-24](../src/AgentChat/Foundry/FoundryClient.cs#L7-L24)). Outside a user scope, Foundry calls fall back to the configured app/managed-identity token ([lines 201-214](../src/AgentChat/Foundry/FoundryClient.cs#L201-L214)). Teams-driven turns normally call Foundry as the container identity unless explicit Teams SSO/OBO logic is added for that path.

## Proxy leg versus passthrough leg

This repo has two Teams-facing patterns:

| Path | Who processes the activity? | JWT audience expected | Reply token path |
|---|---|---|---|
| `/api/messages/{foundry}/{project}/{agent}` | This ASP.NET bot (`FoundryBot`) translates Teams turns to Foundry OpenAI/Responses calls. | The proxy bot app id from `Bots:Routes`. | This repo sends replies through Bot Framework, using FIC. |
| `/api/passthrough/{foundry}/{project}/{agent}` | YARP forwards the request to Foundry Activity Protocol. | The Foundry agent service principal, because Foundry validates the unchanged token. | Foundry handles the Bot Framework activity protocol response. |

`PassthroughEndpoints` documents the second pattern: it forwards `POST /api/passthrough/{foundry}/{project}/{agent}` to Foundry's Activity Protocol endpoint and leaves the inbound JWT untouched so Foundry validates it as if Bot Service had called Foundry directly ([`PassthroughEndpoints.cs` lines 12-23](../src/AgentChat/Passthrough/PassthroughEndpoints.cs#L12-L23)). The route only contributes a network hop and path rewrite.

## Creating a Teams-managed bot with `@microsoft/teams.cli` (no proxy container needed)

Use this when you want Teams / Bot Service to send activities to any HTTPS endpoint you control, such as a local dump listener or a Teams SDK app that calls Foundry itself. You do not need this repository's proxy container for that shape.

Prerequisites:

- `@microsoft/teams.cli` 3.x or later
- `devtunnel` CLI
- `az login` completed for the tenant where you are testing
- `teams login` completed, or an otherwise authenticated Teams Developer CLI session

### 1. Start a local HTTP listener

For a throwaway diagnostic listener, use any server that accepts `POST /api/messages`, logs headers and JSON, and returns `200`. Do not log raw bearer tokens in shared output. A minimal Node.js sketch looks like this:

```js
import http from "node:http";

http.createServer(async (req, res) => {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const body = Buffer.concat(chunks).toString("utf8");

  const auth = req.headers.authorization || "";
  const token = auth.toLowerCase().startsWith("bearer ") ? auth.slice(7) : null;
  const [encodedHeader, encodedPayload] = token ? token.split(".") : [];
  const decode = (part) => part
    ? JSON.parse(Buffer.from(part, "base64url").toString("utf8"))
    : null;

  console.log(JSON.stringify({
    method: req.method,
    url: req.url,
    headers: { ...req.headers, authorization: auth ? "Bearer <redacted>" : undefined },
    jwt: token ? { header: decode(encodedHeader), payload: decode(encodedPayload) } : null,
    body: body ? JSON.parse(body) : null
  }, null, 2));

  res.writeHead(200, { "content-type": "application/json" });
  res.end("{}\n");
}).listen(3978, () => console.log("listening on http://localhost:3978"));
```

### 2. Create and expose a devtunnel

```bash
devtunnel create teams-dump --allow-anonymous
devtunnel port create teams-dump -p 3978 --protocol http
devtunnel host teams-dump
```

Use `--protocol http` when your local listener is plain HTTP. We saw `502` responses when the tunnel port was configured as HTTPS but the local listener was not serving TLS.

Copy the public `https://<tunnel>.devtunnels.ms` URL from `devtunnel host`.

### 3. Create a Teams-managed bot and app

```bash
teams app create \
  --name joe-dump \
  --endpoint https://<tunnel>.devtunnels.ms/api/messages \
  --no-secret \
  --env .env
```

The Teams Developer CLI infrastructure guide describes `teams app create` as creating a Teams-managed bot, writing credentials to the selected env file, and producing app details/install information. It also notes that the endpoint must be publicly reachable over HTTPS.

With `--no-secret`, you get a `BOT_ID` in `.env` but no `BOT_PASSWORD`. This only skips *scaffolding* a client secret; it does not remove the runtime requirement. Bot Service still requires a bearer token whose subject is the bot app id for any outbound POST to `serviceUrl` (typing, streaming, proactive, or normal reply). To actually reply you must add one of:

- a client secret or certificate to the bot's AAD app registration, or
- a federated identity credential (FIC) trusting some other identity you can run as (typical for containerized production), or
- a user-assigned managed identity bound to the Azure Bot resource (`msaAppType = UserAssignedMSI`).

The listener example in this doc returns `200` without calling back, so it never needs credentials. The moment you want to send a real reply you pick one of the above.

Package and sideload the app through your normal Teams path:

```bash
teams app package
```

Then upload the generated package in Teams or through your Teams admin/test sideload workflow.

### 4. If you later route the same bot through this repo

If the endpoint becomes this repo's proxy leg, for example:

```bash
teams app update <teamsAppId> \
  --endpoint https://<container>/api/messages/<foundry>/<project>/<agent>
```

then the container must know which app id is allowed for that route, and it must be able to mint outbound Bot Framework reply tokens as that app id.

That means:

1. Add the bot app id to `Bots:Routes` for the target agent.
2. Configure FIC on the bot registration so the container UAMI is trusted to exchange its managed-identity token for a Bot Framework token.

The route config shape is documented in [`.github/copilot-instructions.md` lines 57-61](../.github/copilot-instructions.md#L57-L61): current entries use `AgentName`, `ProxyAppId`, and `DirectAppId`, with `AppId` retained as a backwards-compatible fallback. `EffectiveProxyAppId` is both the inbound JWT audience and the app id used for outbound Bot Framework tokens.

## Why per-agent route entries (and where they live)

`Bots:Routes` does two jobs at once:

1. **Inbound audience isolation.** For each URL like `/api/messages/{foundry}/{project}/{agent}`, the middleware requires `jwt.aud == route.EffectiveProxyAppId`. That means a token issued for bot A cannot be replayed against bot B's URL, even if both bots live in the same tenant.
2. **Outbound reply identity.** For each entry, `Program.cs` registers one `FicAccessTokenProvider` keyed by the app id, so the SDK can pick the right FIC exchange when it needs to reply as that specific bot.

Both jobs could in principle be answered from data already in the request:

- The bot app id is in the activity itself as `recipient.id = 28:<appId>`, so outbound token minting could infer it without config.
- The tenant is in the JWT `serviceurl` claim (signed by Bot Service, not spoofable at the transport layer). A hypothetical `Bots:AllowedTenants` mode could accept any bot whose signed `serviceurl` resolves to an allowed tenant, at the cost of losing per-agent isolation: any bot in the tenant could then invoke any agent path.

The current design keeps explicit per-agent entries because per-agent audience isolation is a real security property and because operations already provisions one AAD app per agent for direct-bot use. A tenant-scoped mode remains an option for future simplification and would be an opt-in flag rather than the default.

Routes live in Cosmos, seeded from `Bots:Routes` on first run:

- On startup, `Program.cs` parses `cfg["Bots:Routes"]` into an initial seed and calls `CosmosRouteRepository.LoadAsync(seed)`. If the Cosmos document at key `routes/all` is empty, the seed is written and becomes the initial registry. If it already exists, the seed is ignored — Cosmos is the source of truth from that point on.
- `BotServiceJwtMiddleware` consults `IRouteRepository.TryGet(agent)` per request, so routes added after startup take effect immediately (no container restart).
- Outbound reply providers are lazily materialized by `DynamicConnections` (a custom `IConnections` impl): the first outbound reply for a new bot appId spins up a fresh `FicAccessTokenProvider` under a `ConcurrentDictionary.GetOrAdd` and caches it for the process lifetime.

### Runtime registration UI

The admin site at `/admin/register` (guarded by `AdminChatAuthFilter`) lets an authenticated operator add a new agent without touching config. The flow is deliberately two-step:

1. `GET /admin/register` renders the form (agent name, proxy AppId, optional direct AppId, Foundry host/project).
2. `POST /admin/register/preview` renders a checklist of the manual AAD/Foundry/FIC operations the operator must run themselves (via `az`). No state is changed.
3. `POST /admin/register/confirm` writes the route to Cosmos via `IRouteRepository.UpsertAsync`, at which point the middleware and `DynamicConnections` start honoring it on the very next request.

Registration is a checklist rather than automation because the container UAMI generally does not — and should not — hold `Application.ReadWrite.All`. The two mandatory prerequisites the preview page walks the operator through are:

- **Foundry access.** The proxy UAMI must hold Azure AI User (or equivalent) on the target Foundry project. Turns invoked through the proxy call Foundry as the container identity.
- **FIC trust from the new bot app registration.** The new bot's AAD app registration must have a federated identity credential trusting this UAMI (`issuer = login.microsoftonline.com/{tenantId}/v2.0`, `subject = <UAMI object id>`, `audiences = [api://AzureADTokenExchange]`). Without it, `FicAccessTokenProvider` cannot mint outbound Bot Framework reply tokens for the new bot, and Teams will see the bot silently fail to respond.

The admin landing page has a "Register new agent" button that links to this flow.



- `devtunnel port create --protocol https` requires the local server to speak HTTPS. Use `--protocol http` for a plain local HTTP listener.
- Bot Framework issuer is not always an AAD tenant issuer. Middleware must accept `https://api.botframework.com` for real Teams channel traffic.
- The bot JWT audience is the bot's app id, not the Foundry service principal and not an AAD resource URI.
- Teams-managed bot means no secret to leak for the Teams SDK path, but it also does not give you Foundry user OBO. A Bot Service JWT has no user token and no Foundry scopes.
- `x-ms-tenant-id` and `serviceUrl` provide tenant context, but the JWT itself may not contain a `tid` claim.
- FIC is for outbound Bot Framework token exchange in this repo. It is not a replacement for Foundry user OBO.

## References

- [Repo auth model summary](../.github/copilot-instructions.md#auth-model-three-flows)
- [Bot Framework connector authentication](https://learn.microsoft.com/en-us/azure/bot-service/rest-api/bot-framework-rest-connector-authentication)
- [Workload identity federation / federated identity credentials](https://learn.microsoft.com/en-us/azure/active-directory/develop/workload-identity-federation)
- [OAuth 2.0 on-behalf-of flow](https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow)
- [`BotServiceJwtMiddleware.cs`](../src/AgentChat/Middleware/BotServiceJwtMiddleware.cs)
- [`FicAccessTokenProvider.cs`](../src/AgentChat/Auth/FicAccessTokenProvider.cs)
- [`PassthroughEndpoints.cs`](../src/AgentChat/Passthrough/PassthroughEndpoints.cs)
