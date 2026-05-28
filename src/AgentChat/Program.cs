using AgentChat.Bots;
using AgentChat.Middleware;
using AgentChat.Services;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Azure;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddNewtonsoftJson();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<AgentClientCache>();

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

builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();
builder.Services.AddTransient<IBot, FoundryBot>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<BotServiceJwtMiddleware>();
app.MapControllers();
app.MapHealthChecks("/health");

var svc = app.Services.GetRequiredService<AgentService>();
app.Logger.LogInformation("Configured {Count} agents: {Names}",
    svc.Descriptors.Count,
    string.Join(", ", svc.Descriptors.Select(d => $"{d.Key}→{d.Name}")));

app.Run();
