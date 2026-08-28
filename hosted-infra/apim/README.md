# APIM bridge for the hosted Teams agent

Azure Bot Service cannot call a Foundry Invocations endpoint directly because
the two services require different bearer tokens and Bot Service does not add a
hosted `agent_session_id`. APIM bridges the protocols:

```text
Teams -> Azure Bot -> APIM -> Foundry Invocations -> C# CloudAdapter
```

The production deployment template will move into an
`options-infra` sample. The policies in this directory document the tested
contract that sample must preserve.

## Request flow

1. APIM validates the original Bot Framework JWT using the Bot Framework
   OpenID configuration and the Azure Bot application ID as its audience.
2. APIM copies the original `Authorization` header to
   `x-client-bot-authorization`.
3. APIM replaces `Authorization` with a managed-identity token for
   `https://ml.azure.com`.
4. APIM adds the fixed `agent_session_id` stored in the
   `csharp-foundry-agent-session-id` named value.
5. APIM routes the request to
   `/agents/{agent}/endpoint/protocols/invocations?api-version=v1`.
6. `ActivityInvocationHandler` restores the forwarded Bot JWT before invoking
   the C# `CloudAdapter`.

Foundry forwards `x-client-*` headers into the hosted runtime. Do not rename
the forwarded header without changing `ActivityInvocationHandler`.

## Required APIM configuration

- A system-assigned managed identity.
- `Azure AI User` and `Azure AI Agent Service Agent Consumer` assignments on
  the Foundry resource scope.
- An unauthenticated APIM API for the Azure Bot messaging endpoint. The Bot
  JWT is still validated by `bot-invocations-policy.xml`.
- A named value called `csharp-foundry-agent-session-id`.
- A subscription-protected diagnostic API using `diagnostic-policy.xml` for
  creating a session before exposing the Bot endpoint.

Replace these placeholders when loading the policy:

| Placeholder | Value |
|---|---|
| `BOT_APP_ID` | Azure Bot application/client ID |
| `FOUNDRY_PROJECT_ENDPOINT` | Foundry project endpoint through `/api/projects/{project}` |
| `AGENT_NAME` | Hosted C# agent name |

## Session lifecycle

Create one session for each deployed hosted-agent version by invoking the
diagnostic API with a synthetic Bot Framework message. Store the returned
session ID in the APIM named value. Recreate and repin the session whenever the
hosted-agent version changes.

All Teams conversations can share this execution session because durable
conversation state is keyed by the Teams conversation ID in Cosmos DB. The
hosted session is an execution sandbox, not the chat-history boundary.

## Security notes

- Never log or persist `x-client-bot-authorization`.
- Remove the forwarded header after restoring `Authorization`.
- Do not reuse the inbound Bot JWT as the outbound Connector token.
- APIM uses its Foundry identity only for the APIM-to-Foundry hop.
- The hosted agent obtains its own Bot Connector token for
  `https://api.botframework.com/.default`.
