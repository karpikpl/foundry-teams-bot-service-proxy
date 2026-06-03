using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;

namespace AgentChat.Services;

/// <summary>
/// Acquires a Foundry-audience user-delegated token for the current Teams user
/// via Bot Framework's token service (Bot Service OAuth Connection Setting).
///
/// Setup (operator-side, one-time):
///   1. Register an AAD app for the bot ("expose an API" scope, e.g.
///      <c>api://&lt;bot-app-id&gt;/access_as_user</c>; Teams listed as a known
///      client application). The app also needs delegated permission for the
///      Foundry resource (<c>https://ai.azure.com/user_impersonation</c>).
///   2. On the Bot Service registration, add an OAuth Connection Setting:
///      - Service Provider: Azure Active Directory v2
///      - Client id / client secret (or FIC) from the AAD app above
///      - Token Exchange URL: <c>api://&lt;bot-app-id&gt;</c>
///      - Scopes: <c>https://ai.azure.com/user_impersonation offline_access</c>
///   3. Update the Teams manifest <c>webApplicationInfo</c> with the bot AAD
///      app id + resource (handled by <see cref="Bots.ManifestBuilder"/>).
///   4. Set <c>TeamsSso__ConnectionName=&lt;OAuth connection name&gt;</c>.
///      SSO turns on automatically whenever a connection name is configured.
///
/// At runtime this service just calls <c>UserTokenClient.GetUserTokenAsync</c>;
/// Bot Service handles the SSO + OBO dance internally (cached, refreshed).
///
/// When SSO is disabled or returns no token, callers fall back to whatever
/// app-identity behavior they had before.
/// </summary>
public class TeamsSsoService
{
    private readonly ILogger<TeamsSsoService> _logger;
    private readonly string? _connectionName;

    // SSO is on whenever the operator wired up a connection name. No separate
    // feature flag — if the connection isn't configured, the calls below
    // would 400 anyway, so there's nothing to gate.
    public bool Enabled => !string.IsNullOrEmpty(_connectionName);
    public string? ConnectionName => _connectionName;

    public TeamsSsoService(IConfiguration config, ILogger<TeamsSsoService> logger)
    {
        _logger         = logger;
        _connectionName = config["TeamsSso:ConnectionName"];

        if (Enabled)
        {
            _logger.LogInformation("Teams SSO enabled (connection={Connection}).", _connectionName);
        }
    }

    /// <summary>
    /// Try to fetch a cached user-delegated Foundry token for the current Teams
    /// user. Returns null when SSO is disabled, the bot hasn't been signed in
    /// yet, or the token service can't issue one (e.g. consent missing).
    /// </summary>
    public virtual async Task<TokenResponse?> TryGetUserTokenAsync(ITurnContext turnContext, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        var userTokenClient = turnContext.TurnState.Get<UserTokenClient>();
        if (userTokenClient is null)
        {
            // CloudAdapter normally provides this. If it's missing, we're likely
            // running under TestAdapter / unit tests without auth wired up.
            _logger.LogDebug("UserTokenClient not available in turn state — SSO disabled for this turn.");
            return null;
        }

        try
        {
            return await userTokenClient.GetUserTokenAsync(
                userId:         turnContext.Activity.From.Id,
                connectionName: _connectionName!,
                channelId:      turnContext.Activity.ChannelId,
                magicCode:      null,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetUserTokenAsync failed for user {UserId}", turnContext.Activity.From.Id);
            return null;
        }
    }

    /// <summary>
    /// Build a sign-in resource (URL + token exchange state) for an OAuthCard.
    /// Called when <see cref="TryGetUserTokenAsync"/> returned null and we
    /// need to prompt the user to sign in.
    /// </summary>
    public virtual async Task<SignInResource?> GetSignInResourceAsync(ITurnContext turnContext, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        var userTokenClient = turnContext.TurnState.Get<UserTokenClient>();
        if (userTokenClient is null) return null;

        try
        {
            return await userTokenClient.GetSignInResourceAsync(
                connectionName: _connectionName!,
                activity:       turnContext.Activity,
                finalRedirect:  null,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSignInResourceAsync failed");
            return null;
        }
    }

    /// <summary>
    /// Complete a Teams SSO token exchange (the <c>signin/tokenExchange</c>
    /// invoke activity contains a token Teams obtained via silent SSO; we
    /// exchange it for an OBO'd token via the Bot Service token service).
    /// Returns null when the exchange fails (caller surfaces an error).
    /// </summary>
    public virtual async Task<TokenResponse?> ExchangeTokenAsync(ITurnContext turnContext, TokenExchangeRequest request, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        var userTokenClient = turnContext.TurnState.Get<UserTokenClient>();
        if (userTokenClient is null) return null;

        try
        {
            return await userTokenClient.ExchangeTokenAsync(
                userId:         turnContext.Activity.From.Id,
                connectionName: _connectionName!,
                channelId:      turnContext.Activity.ChannelId,
                exchangeRequest: request,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExchangeTokenAsync failed for user {UserId}", turnContext.Activity.From.Id);
            return null;
        }
    }

    /// <summary>
    /// Sign the user out of the bot's OAuth connection. Useful for a
    /// <c>/signout</c> command when consent needs to be re-requested.
    /// </summary>
    public virtual async Task SignOutAsync(ITurnContext turnContext, CancellationToken ct = default)
    {
        if (!Enabled) return;
        var userTokenClient = turnContext.TurnState.Get<UserTokenClient>();
        if (userTokenClient is null) return;
        try
        {
            await userTokenClient.SignOutUserAsync(
                userId:         turnContext.Activity.From.Id,
                connectionName: _connectionName!,
                channelId:      turnContext.Activity.ChannelId,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignOutUserAsync failed for user {UserId}", turnContext.Activity.From.Id);
        }
    }
}
