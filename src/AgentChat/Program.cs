using System.Text.Json;
using AgentChat.Auth;
using AgentChat.Bots;
using AgentChat.Middleware;
using AgentChat.Passthrough;
using AgentChat.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Agents.Authentication;
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
// Route registry — the persistent source of truth for which agent names this
// proxy serves. Seeded from `Bots:Routes` config on first run, then mutable
// at runtime via the admin registration UI. See Services/CosmosRouteRepository.
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IRouteRepository, CosmosRouteRepository>();

// -----------------------------------------------------------------------------
// Multi-bot outbound auth via Federated Identity Credentials (no per-bot secrets).
//
// DynamicConnections materializes one FicAccessTokenProvider per bot appId
// on demand, keyed off IRouteRepository. This lets us pick up newly-registered
// bots without a container restart. See Auth/DynamicConnections.cs and
// Auth/FicAccessTokenProvider.cs for the FIC flow.
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<IConnections>(sp =>
{
    var cfg           = sp.GetRequiredService<IConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var httpFactory   = sp.GetRequiredService<IHttpClientFactory>();
    var routes        = sp.GetRequiredService<IRouteRepository>();

    var tenantId = cfg["MicrosoftAppTenantId"] ?? cfg["AZURE_TENANT_ID"]
        ?? throw new InvalidOperationException("MicrosoftAppTenantId not configured.");
    var uamiClientId = cfg["AZURE_CLIENT_ID"];

    return new DynamicConnections(routes, tenantId, uamiClientId, httpFactory, loggerFactory);
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

// Hydrate the route registry from Cosmos, seeding from Bots:Routes if the
// registry is empty. This has to happen after the WebApplication is built
// so IStorage / ILogger are available, and before the first inbound
// request reaches BotServiceJwtMiddleware.
var routeRepo = (CosmosRouteRepository)app.Services.GetRequiredService<IRouteRepository>();
var seedFromConfig = builder.Configuration.GetValue("Bots:SeedFromConfig", true);
// Fall back to the default project when a Bots:Routes entry doesn't
// specify FoundryHost/ProjectName — most deployments have all agents in
// one project, and the admin UI needs *something* to show.
var defaultProjectEndpoint = builder.Configuration["Foundry:ProjectEndpoint"];
TryDeriveDefaultHostAndProject(defaultProjectEndpoint, out var defaultHost, out var defaultProject);
var seedRoutes = seedFromConfig
    ? ParseRoutes(builder.Configuration["Bots:Routes"])
        .Where(r => !string.IsNullOrEmpty(r.AgentName) && !string.IsNullOrEmpty(r.EffectiveProxyAppId))
        .Select(r => new BotRoute(
            r.AgentName!,
            r.EffectiveProxyAppId!,
            r.DirectAppId,
            FoundryHost: string.IsNullOrEmpty(r.FoundryHost) ? defaultHost : r.FoundryHost,
            ProjectName: string.IsNullOrEmpty(r.ProjectName) ? defaultProject : r.ProjectName))
        .ToList()
    : new List<BotRoute>();
await routeRepo.LoadAsync(seedRoutes);

// Backfill: rows persisted before Bots:Routes carried project metadata
// have empty FoundryHost/ProjectName. Fill them in-place from the default
// so the admin UI has something to display and manifest links resolve.
if (!string.IsNullOrEmpty(defaultHost) && !string.IsNullOrEmpty(defaultProject))
{
    foreach (var r in routeRepo.GetAll())
    {
        if (string.IsNullOrEmpty(r.FoundryHost) || string.IsNullOrEmpty(r.ProjectName))
        {
            await routeRepo.UpsertAsync(r with
            {
                FoundryHost = string.IsNullOrEmpty(r.FoundryHost) ? defaultHost : r.FoundryHost,
                ProjectName = string.IsNullOrEmpty(r.ProjectName) ? defaultProject : r.ProjectName,
            });
        }
    }
}

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

static void TryDeriveDefaultHostAndProject(string? endpoint, out string host, out string project)
{
    host = ""; project = "";
    if (string.IsNullOrWhiteSpace(endpoint)) return;
    try
    {
        var uri = new Uri(endpoint);
        var dot = uri.Host.IndexOf('.');
        host = dot > 0 ? uri.Host[..dot] : uri.Host;
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.IndexOf(segs, "projects");
        if (idx >= 0 && idx + 1 < segs.Length) project = segs[idx + 1];
    }
    catch { /* leave as empty */ }
}

internal sealed class RouteEntry
{
    public string? AgentName { get; set; }
    public string? ProxyAppId { get; set; }
    public string? DirectAppId { get; set; }
    public string? AppId { get; set; }
    public string? FoundryHost { get; set; }
    public string? ProjectName { get; set; }

    public string? EffectiveProxyAppId =>
        !string.IsNullOrEmpty(ProxyAppId) ? ProxyAppId : AppId;
}

