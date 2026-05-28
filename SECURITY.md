# Security policy

## Reporting a vulnerability

If you believe you've found a security issue, please **do not file a public GitHub Issue**. Instead:

- Open a [private security advisory](https://github.com/karpikpl/foundry-teams-bot-service-proxy/security/advisories/new)
- Or email the maintainer directly (see the repo's profile page)

You can expect an acknowledgement within a few days. This is a community-maintained sample with no SLA, but I take security reports seriously.

## What's in scope

- Vulnerabilities in the bot code itself (auth bypass, request forgery, secret leaks, etc.)
- Container image vulnerabilities introduced by the Dockerfile (e.g., a misconfigured user, an exposed secret)

## What's out of scope

- Vulnerabilities in the .NET runtime, OpenAI SDK, Bot Framework SDK, or other third-party dependencies — please report to the upstream maintainers
- Misconfigurations in your deployment (open RBAC roles, leaked managed identity credentials, network rules) — these are operator responsibility
- Vulnerabilities in Microsoft Foundry, Bot Service, or Teams itself — report to Microsoft via [MSRC](https://msrc.microsoft.com/)

## Hardening notes for operators

This sample assumes a few things; verify before deploying to production:

1. **JWT validation** uses `BOTSERVICE_UAMI_CLIENTID` to validate the `aud` claim on incoming Bot Service requests. The middleware is in `src/AgentChat/Middleware/BotServiceJwtMiddleware.cs`. If you change the `aud` validation logic, make sure you're still rejecting unsigned and externally-signed tokens.
2. **No secrets in env vars** — the deployment uses two UMIs and AAD-only access to Cosmos and Foundry. If you re-introduce keys, scope them tightly and use Key Vault references.
3. **URL safety** — `/upload` (currently disabled) and any future URL-fetching code should go through `Bots/UrlSafety.cs` which blocks private/link-local IPs to prevent SSRF.
4. **Container runs as non-root** — uid 1654 (`app` user). Don't override with `USER root` without good reason.
