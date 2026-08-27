using AgentChat.Bots;
using FluentAssertions;
using Xunit;

namespace AgentChat.Tests;

public class ToolCallPresentationTests
{
    [Theory]
    [InlineData("""{"action":{"query":"dogs"}}""", "dogs")]
    [InlineData("""{"action":{"search_query":"cats"}}""", "cats")]
    public void Web_search_query_uses_the_same_normalization_for_all_channels(string json, string expected)
    {
        ToolCallPresentation.ExtractWebSearchQuery(BinaryData.FromString(json)).Should().Be(expected);
    }

    [Fact]
    public void Code_interpreter_details_use_the_same_normalization_for_all_channels()
    {
        var json = BinaryData.FromString(
            """{"code":"print('dogs')","outputs":[{"type":"logs","logs":"dogs\n"}]}""");

        var (code, output) = ToolCallPresentation.ExtractCodeInterpreterDetails(json);

        code.Should().Be("print('dogs')");
        output.Should().Contain("\"logs\":\"dogs\\n\"");
    }
}
