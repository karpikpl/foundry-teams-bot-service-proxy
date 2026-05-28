# Multi-stage Dockerfile for the Foundry → Teams bot
#
# Build:
#   docker build -t foundry-teams-bot-service-proxy:local .
#
# Run locally (needs an AAD-authed environment for Foundry/Cosmos):
#   docker run --rm -p 8080:8080 \
#     -e Foundry__ProjectEndpoint="https://aif.example.com/api/projects/p" \
#     -e Cosmos__Endpoint="https://cosmos.example.com:443/" \
#     -e MicrosoftAppId="<bot-uami-client-id>" \
#     -e MicrosoftAppType="UserAssignedMSI" \
#     -e MicrosoftAppTenantId="<tenant>" \
#     -e BOTSERVICE_UAMI_CLIENTID="<bot-uami-client-id>" \
#     -e AZURE_CLIENT_ID="<app-uami-client-id>" \
#     -v $HOME/.azure:/home/app/.azure:ro \
#     foundry-teams-bot-service-proxy:local
#
# Health check:
#   curl http://localhost:8080/health
#
# Env vars (double-underscore = ASP.NET config section nesting):
#   - Foundry__ProjectEndpoint     (required) Project URL up to .../api/projects/{name}
#   - Foundry__Agents__0__Key      (optional) Override the default agent catalog
#   - Foundry__Agents__0__Name     (optional) ... see AgentService for shape
#   - Cosmos__Endpoint             (required) Cosmos serverless URL — AAD auth
#   - Cosmos__Database             (optional) defaults to "botstate"
#   - Cosmos__Container            (optional) defaults to "conversations"
#   - MicrosoftAppId               (required) Bot Service registration UAMI client id
#   - MicrosoftAppType             (required) "UserAssignedMSI"
#   - MicrosoftAppTenantId         (required) AAD tenant id
#   - BOTSERVICE_UAMI_CLIENTID     (required) UMI client id JWT middleware validates `aud` against
#   - AZURE_CLIENT_ID              (optional) Sets ManagedIdentityCredential target UAMI
#   - APPLICATIONINSIGHTS_CONNECTION_STRING (optional) App Insights wiring

# ----------------------------------------------------------------------------
# Build stage
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/AgentChat/AgentChat.csproj src/AgentChat/
RUN dotnet restore src/AgentChat/AgentChat.csproj

COPY src/AgentChat/ src/AgentChat/
RUN dotnet publish src/AgentChat/AgentChat.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# ----------------------------------------------------------------------------
# Runtime stage
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Run as the non-root "app" user (uid 1654) that the aspnet image ships
# pre-created — least privilege without us having to manage it.
RUN mkdir -p /home/app/.azure && chown -R app:app /home/app

WORKDIR /app
COPY --from=build --chown=app:app /app/publish ./

USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Note: no Dockerfile HEALTHCHECK on purpose — the aspnet runtime image ships
# without curl/wget and adding either bloats the image. Use the orchestrator's
# native probe instead (App Service "Health check path", Kubernetes
# livenessProbe.httpGet) pointed at GET /health (returns 200 Healthy).

ENTRYPOINT ["dotnet", "AgentChat.dll"]
