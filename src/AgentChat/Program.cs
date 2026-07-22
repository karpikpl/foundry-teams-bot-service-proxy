using System.Text.Json;
using AgentChat.Auth;
using AgentChat.Bots;
using AgentChat.Middleware;
using AgentChat.Passthrough;
using AgentChat.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Model;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Storage.CosmosDb;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);
var adminChatAuth = AdminChatAuthOptions.FromConfiguration(builder.Configuration);
adminChatAuth.ValidateIfEnabled();

builder.Services.AddSingleton(adminChatAuth);
builder.Services.AddScoped<AdminChatAuthFilter>();
builder.Services.AddControllers().AddNewtonsoftJson();
if (adminChatAuth.Enabled)
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
        {
            options.Instance = adminChatAuth.Instance;
            options.TenantId = adminChatAuth.TenantId;
            options.ClientId = adminChatAuth.ClientId;
            options.ClientSecret = adminChatAuth.ClientSecret;
            options.CallbackPath = AdminChatAuthOptions.OpenIdConnectCallbackPath;
            options.SignedOutCallbackPath = AdminChatAuthOptions.SignedOutCallbackPath;
        })
        .EnableTokenAcquisitionToCallDownstreamApi(new[] { AdminChatAuthOptions.FoundryScope })
        .AddInMemoryTokenCaches();
    builder.Services.AddAuthorization();
    builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
}
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<AgentClientCache>();
builder.Services.AddSingleton<TeamsSsoService>();
// IStorage — Cosmos serverless via AAD (no keys).
builder.Services.AddSingleton<IStorage>(sp =>
{
    var cfg      = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["Cosmos:Endpoint"] ?? throw new InvalidOperationException("Cosmos:Endpoint not configured");
    var dbId     = cfg["Cosmos:Database"]  ?? "botstate";
    var contId   = cfg["Cosmos:Container"] ?? "conversations";

    var cred = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = cfg["AZURE_CLIENT_ID"]
    });

    return new CosmosDbPartitionedStorage(new CosmosDbPartitionedStorageOptions
    {
        CosmosDbEndpoint = endpoint,
        TokenCredential  = cred,
        DatabaseId       = dbId,
        ContainerId      = contId,
        CompatibilityMode = false
    });
});

builder.Services.AddSingleton<ConversationStore>();

// -----------------------------------------------------------------------------
// Multi-bot outbound auth via Federated Identity Credentials (no per-bot secrets).
//
// We register one FicAccessTokenProvider PER bot appId (parsed from Bots:Routes)
// and wire them into a programmatic ConfigurationConnections. The SDK's
// per-outbound-call dispatch resolves the right provider from the claims
// identity's appId via ConnectionMapItem.Audience matching. See
// Auth/FicAccessTokenProvider.cs for the FIC flow itself.
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IConnections>(sp =>
{
    var cfg           = sp.GetRequiredService<IConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var httpFactory   = sp.GetRequiredService<IHttpClientFactory>();

    var tenantId = cfg["MicrosoftAppTenantId"] ?? cfg["AZURE_TENANT_ID"]
        ?? throw new InvalidOperationException("MicrosoftAppTenantId not configured.");
    var uamiClientId = cfg["AZURE_CLIENT_ID"];

    var routes = ParseRoutes(cfg["Bots:Routes"]);
    var providers = new Dictionary<string, IAccessTokenProvider>(StringComparer.OrdinalIgnoreCase);
    var mapItems  = new List<ConnectionMapItem>();
    foreach (var r in routes)
    {
        var appId = r.EffectiveProxyAppId;
        if (string.IsNullOrEmpty(appId) || providers.ContainsKey(appId)) continue;

        providers[appId] = new FicAccessTokenProvider(
            appId,
            tenantId,
            uamiClientId,
            httpFactory.CreateClient(nameof(FicAccessTokenProvider)),
            loggerFactory.CreateLogger<FicAccessTokenProvider>());

        mapItems.Add(new ConnectionMapItem { Audience = appId, Connection = appId });
    }

    return new ConfigurationConnections(
        providers,
        mapItems,
        loggerFactory.CreateLogger<ConfigurationConnections>());
});

// Wire the M365 Agents SDK auth pipeline (JWT validation for inbound + token
// service client factory for outbound). Reads TokenValidation from IConfiguration.
builder.Services.AddDefaultMsalAuth(builder.Configuration);

// Registers CloudAdapter + IAgent → FoundryBot + IAgentHttpAdapter → CloudAdapter.
// AdapterOptions/IActivityTaskQueue/IChannelServiceClientFactory come from
// AddCloudAdapter transitively. Our AdapterWithErrorHandler subclasses CloudAdapter
// so we replace the CloudAdapter registration below.
builder.AddAgent<FoundryBot>();
// AdapterOptions is a plain POCO required by the CloudAdapter ctor but not
// auto-registered by AddAgent/AddCloudAdapter. Register defaults here.
builder.Services.AddSingleton(new AdapterOptions());
builder.Services.AddSingleton<CloudAdapter, AdapterWithErrorHandler>();
builder.Services.AddSingleton<IAgentHttpAdapter>(sp => sp.GetRequiredService<CloudAdapter>());
builder.Services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<CloudAdapter>());

// Transparent reverse-proxy route for Foundry Activity Protocol
// (/api/passthrough/{foundry}/{project}/{agent}). See PassthroughEndpoints.
builder.Services.AddActivityProtocolPassthrough();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
if (adminChatAuth.Enabled)
{
    app.UseAuthentication();
}
app.UseMiddleware<BotServiceJwtMiddleware>();
if (adminChatAuth.Enabled)
{
    app.UseAuthorization();
}
app.MapControllers();
app.MapActivityProtocolPassthrough();
if (adminChatAuth.Enabled)
{
    app.MapRazorPages();
}
app.MapHealthChecks("/health");

var svc = app.Services.GetRequiredService<AgentService>();
app.Logger.LogInformation("Configured Foundry project: {Endpoint}. Agent catalog will be discovered on first authenticated request.", svc.DefaultProjectEndpoint);

app.Run();

static List<RouteEntry> ParseRoutes(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return new();
    try
    {
        return JsonSerializer.Deserialize<List<RouteEntry>>(json) ?? new();
    }
    catch
    {
        return new();
    }
}

internal sealed class RouteEntry
{
    public string? AgentName { get; set; }
    public string? ProxyAppId { get; set; }
    public string? DirectAppId { get; set; }
    public string? AppId { get; set; }

    public string? EffectiveProxyAppId =>
        !string.IsNullOrEmpty(ProxyAppId) ? ProxyAppId : AppId;
}

