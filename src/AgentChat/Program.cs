using AgentChat.Auth;
using AgentChat.Bots;
using AgentChat.Middleware;
using AgentChat.Passthrough;
using AgentChat.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Azure;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
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

// Multi-bot outbound auth via Federated Identity Credentials (no per-bot secrets).
// The factory mints a Bot Framework token per-bot using the container UAMI as
// a federated client assertion. Registering the factory in DI causes
// ConfigurationBotFrameworkAuthentication to use it for outbound replies.
builder.Services.AddSingleton<ServiceClientCredentialsFactory, FicServiceClientCredentialsFactory>();
builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();
builder.Services.AddTransient<IBot, FoundryBot>();

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
app.Logger.LogInformation("Configured Foundry project: {Endpoint}. Agent catalog will be discovered on first /agents request.", svc.DefaultProjectEndpoint);

app.Run();
