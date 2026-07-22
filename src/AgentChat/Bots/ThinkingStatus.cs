using System.Text.RegularExpressions;

namespace AgentChat.Bots;

/// <summary>
/// Builds short, human-friendly informative-update strings for tool calls so
/// users can see what the agent is doing in real time (Teams 1:1 streaming
/// "informative" slot — the blue progress bar). Capped well below the 1 KB
/// Teams limit and stable across runs (no jitter / no timestamps).
///
/// Each category exposes a small pool of playful phrases; the caller picks
/// one via <see cref="Pick"/> using an integer index that the heartbeat loop
/// increments over time. This gives the user a sense of "activity happening"
/// even while a single tool call is in flight for many seconds.
/// </summary>
internal static class ThinkingStatus
{
    private const int MaxLen = 240;

    // ---- Rotating phrase pools ---------------------------------------------

    /// <summary>Generic "waiting on the model" phrases used when no tool is active.</summary>
    public static readonly string[] Generic =
    {
        "🧠 Thinking…",
        "💭 Working on it…",
        "🔄 Still thinking…",
        "⏳ Hang tight…",
        "🚀 Almost there…",
    };

    /// <summary>Model-side reasoning (chain-of-thought) is in progress.</summary>
    public static readonly string[] Reasoning =
    {
        "🧠 Reasoning through this…",
        "🤔 Connecting the dots…",
        "🧩 Piecing it together…",
        "🧠 Weighing the options…",
    };

    /// <summary>Web search / Bing grounding tool is running.</summary>
    public static readonly string[] WebSearch =
    {
        "🔍 Scouring the web…",
        "🌐 Chasing down sources…",
        "🔎 Following breadcrumbs online…",
        "🕵️ Investigating the internet…",
        "📡 Pulling fresh results…",
    };

    /// <summary>File search / RAG lookup is running.</summary>
    public static readonly string[] FileSearch =
    {
        "📄 Digging through documents…",
        "📚 Skimming the archives…",
        "🔍 Searching your files…",
        "📑 Cross-referencing sources…",
    };

    /// <summary>Code interpreter is running.</summary>
    public static readonly string[] CodeInterpreter =
    {
        "💻 Running the numbers…",
        "🐍 Executing Python…",
        "🧮 Crunching data…",
        "⚙️ Working through the code…",
    };

    /// <summary>Image generation is in progress.</summary>
    public static readonly string[] ImageGeneration =
    {
        "🎨 Painting pixels…",
        "🖼️ Rendering image…",
        "✨ Composing something visual…",
    };

    /// <summary>MCP tool catalog discovery.</summary>
    public static readonly string[] McpListTools =
    {
        "🧰 Discovering available tools…",
        "🔧 Loading tool catalog…",
    };

    // ---- Live "starting" phrases (single string, not rotating) --------------

    /// <summary>Status to show as a function tool is about to be dispatched.</summary>
    public static string ForFunctionCall(string toolName)
        => Trim($"{Emoji(toolName)} {Humanize(toolName)}…");

    /// <summary>Status to show after an MCP tool has completed server-side.</summary>
    public static string ForMcpCallCompleted(string toolName, string? serverLabel)
    {
        var server = string.IsNullOrWhiteSpace(serverLabel) ? "" : $" ({serverLabel})";
        return Trim($"{Emoji(toolName)} {Humanize(toolName)}{server} ✓");
    }

    /// <summary>Status to show as an MCP tool is about to be dispatched by Foundry.</summary>
    public static string ForMcpCallInProgress(string? toolName, string? serverLabel)
    {
        var name = string.IsNullOrWhiteSpace(toolName) ? "MCP tool" : Humanize(toolName!);
        var server = string.IsNullOrWhiteSpace(serverLabel) ? "" : $" ({serverLabel})";
        return Trim($"{Emoji(toolName ?? "mcp")} Calling {name}{server}…");
    }

    /// <summary>Status to show when several function calls are queued.</summary>
    public static string ForBatch(int count)
        => Trim($"🔧 Calling {count} tools…");

    /// <summary>
    /// Deterministically pick a phrase from a rotating pool. The heartbeat
    /// loop supplies a monotonically-increasing index so the visible text
    /// changes every tick without repeating too tightly.
    /// </summary>
    public static string Pick(string[] pool, int index)
    {
        if (pool.Length == 0) return "";
        var i = ((index % pool.Length) + pool.Length) % pool.Length;
        return pool[i];
    }

    /// <summary>
    /// Pick a representative emoji for common tool name patterns. Falls back
    /// to a generic wrench. Pattern matching is intentionally cheap; we lean
    /// on substring matches rather than a hardcoded list.
    /// </summary>
    internal static string Emoji(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return "🔧";
        var n = toolName.ToLowerInvariant();
        if (Contains(n, "search", "bing", "google", "web"))                      return "🔍";
        if (Contains(n, "calendar", "meeting", "event", "schedule"))             return "📅";
        if (Contains(n, "mail", "email", "outlook", "send_message", "sendmsg")) return "📧";
        if (Contains(n, "file", "doc", "drive", "sharepoint", "onedrive"))       return "📄";
        if (Contains(n, "code", "exec", "run", "shell", "bash", "python"))       return "💻";
        if (Contains(n, "db", "sql", "kusto", "cosmos", "postgres"))             return "🗄️";
        if (Contains(n, "weather", "forecast"))                                  return "🌤️";
        if (Contains(n, "image", "photo", "render", "generate"))                 return "🎨";
        if (Contains(n, "translate", "language"))                                return "🌐";
        if (Contains(n, "math", "calc", "compute"))                              return "🧮";
        if (Contains(n, "user", "people", "contact", "directory"))               return "👤";
        if (Contains(n, "github", "git", "repo", "pr", "issue"))                 return "🐙";
        if (Contains(n, "mcp"))                                                   return "🧰";
        return "🔧";
    }

    /// <summary>
    /// Convert a snake_case / camelCase / kebab-case tool name into a
    /// reader-friendly phrase, e.g. <c>get_weather</c> → <c>Get weather</c>,
    /// <c>searchDocuments</c> → <c>Search documents</c>.
    /// </summary>
    internal static string Humanize(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return "Running tool";

        // Split camelCase/PascalCase boundaries.
        var spaced = Regex.Replace(toolName, "(?<=[a-z0-9])([A-Z])", " $1");
        // Replace underscores / hyphens / dots with spaces.
        spaced = spaced.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
        // Collapse whitespace and trim.
        spaced = Regex.Replace(spaced, "\\s+", " ").Trim();
        if (spaced.Length == 0) return "Running tool";
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant();
    }

    private static bool Contains(string s, params string[] needles)
    {
        foreach (var n in needles)
            if (s.Contains(n)) return true;
        return false;
    }

    private static string Trim(string s) => s.Length <= MaxLen ? s : s.Substring(0, MaxLen - 1) + "…";
}
