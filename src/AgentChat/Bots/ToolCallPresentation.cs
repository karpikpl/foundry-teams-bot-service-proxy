using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Responses;

namespace AgentChat.Bots;

internal static class ToolCallPresentation
{
    internal static string? ExtractWebSearchQuery(WebSearchCallResponseItem item)
        => ExtractWebSearchQuery(ModelReaderWriter.Write(item));

    internal static string? ExtractWebSearchQuery(BinaryData data)
    {
        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("action", out var action)) return null;
        if (action.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
            return query.GetString();
        if (action.TryGetProperty("search_query", out var searchQuery) && searchQuery.ValueKind == JsonValueKind.String)
            return searchQuery.GetString();
        return null;
    }

    internal static (string? Code, string? Output) ExtractCodeInterpreterDetails(CodeInterpreterCallResponseItem item)
        => ExtractCodeInterpreterDetails(ModelReaderWriter.Write(item));

    internal static (string? Code, string? Output) ExtractCodeInterpreterDetails(BinaryData data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString()
            : null;
        var output = root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array
            ? Truncate(outputs.GetRawText(), 2000)
            : null;
        return (code, output);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + $"…(+{value.Length - max} chars)";
}
