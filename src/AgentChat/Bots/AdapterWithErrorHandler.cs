using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Connector.Authentication;

namespace AgentChat.Bots;

public class AdapterWithErrorHandler : CloudAdapter
{
    public AdapterWithErrorHandler(
        BotFrameworkAuthentication auth,
        IStorage storage,
        IConfiguration configuration,
        ILogger<IBotFrameworkHttpAdapter> logger,
        ILogger<LoggingMiddleware> activityLogger)
        : base(auth, logger)
    {
        // Dedup signin/tokenExchange invokes across Teams clients. Without this,
        // when a user has Teams open on multiple devices each one races to send
        // the same invoke; the first wins, the rest fail and Teams reports the
        // failure as signin/failure with the generic invokeerror code.
        // The middleware uses IStorage (Cosmos here) for cross-replica dedup.
        var connectionName = configuration["TeamsSso:ConnectionName"];
        if (!string.IsNullOrEmpty(connectionName))
        {
            base.Use(new TeamsSSOTokenExchangeMiddleware(storage, connectionName));
        }
        base.Use(new LoggingMiddleware(activityLogger));

        OnTurnError = async (turnContext, exception) =>
        {
            logger.LogError(exception, "[OnTurnError] {Message}", exception.Message);
            await turnContext.SendActivityAsync("⚠️ The bot encountered an error. Please try again.");
        };
    }
}
