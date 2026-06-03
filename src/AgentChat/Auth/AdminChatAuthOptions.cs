using Microsoft.Extensions.Configuration;

namespace AgentChat.Auth;

public sealed class AdminChatAuthOptions
{
    public const string FoundryScope = "https://ai.azure.com/user_impersonation";
    public const string OpenIdConnectCallbackPath = "/signin-oidc";
    public const string SignedOutCallbackPath = "/signout-oidc-callback";

    public bool Enabled { get; init; }
    public string Instance { get; init; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public static AdminChatAuthOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("AdminChatAuth");
        return new AdminChatAuthOptions
        {
            Enabled = section.GetValue<bool?>("Enabled") ?? false,
            Instance = section["Instance"] ?? "https://login.microsoftonline.com/",
            TenantId = section["TenantId"] ?? config["TeamsSso:TenantId"],
            ClientId = section["ClientId"] ?? config["TeamsSso:AadAppId"],
            ClientSecret = section["ClientSecret"]
        };
    }

    public void ValidateIfEnabled()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(TenantId)) throw new InvalidOperationException("AdminChatAuth:TenantId or TeamsSso:TenantId is required when AdminChatAuth is enabled.");
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("AdminChatAuth:ClientId or TeamsSso:AadAppId is required when AdminChatAuth is enabled.");
        if (string.IsNullOrWhiteSpace(ClientSecret)) throw new InvalidOperationException("AdminChatAuth:ClientSecret is required when AdminChatAuth is enabled.");
    }
}
