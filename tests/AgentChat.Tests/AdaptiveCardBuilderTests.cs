using AdaptiveCards;
using AgentChat.Bots;
using AgentChat.Services;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentChat.Tests;

/// <summary>
/// Tests for the AdaptiveCardBuilder. We assert <i>behavior</i> rather than
/// DOM structure: tests walk the whole card tree to find text/inputs/actions,
/// so they're stable across visual redesigns. Each card type has a small
/// number of cases that pin the user-visible contract — text appears, action
/// payloads carry the right keys, theming styles match the intent of the card.
/// </summary>
public class AdaptiveCardBuilderTests
{
    // ============================================================ Approval

    [Fact]
    public void ApprovalCard_uses_attention_style_to_signal_security_action()
    {
        var att = MakeApproval();
        var json = (JObject)att.Content;
        StylesIn(json).Should().Contain("attention");
    }

    [Fact]
    public void ApprovalCard_includes_all_three_actions_with_tool_metadata()
    {
        var att = AdaptiveCardBuilder.BuildApprovalCard(
            toolName: "search", serverLabel: "microsoft_learn",
            arguments: "{\"q\":\"foundry\"}",
            approvalRequestId: "appr_123", conversationId: "conv_456");

        att.ContentType.Should().Be(AdaptiveCard.ContentType);
        var actions = (JArray)((JObject)att.Content)["actions"]!;
        actions.Should().HaveCount(3);

        foreach (var act in actions)
        {
            var data = (JObject)act["data"]!;
            data["approvalRequestId"]!.ToString().Should().Be("appr_123");
            data["conversationId"]!.ToString().Should().Be("conv_456");
            data["toolName"]!.ToString().Should().Be("search");
            data["serverLabel"]!.ToString().Should().Be("microsoft_learn");
        }
    }

    [Theory]
    [InlineData("approve",        "Approve")]
    [InlineData("approve_always", "Always approve")]
    [InlineData("deny",           "Deny")]
    public void ApprovalCard_action_titles_match_action_payload(string actionId, string titleContains)
    {
        var att = MakeApproval();
        var actions = (JArray)((JObject)att.Content)["actions"]!;
        var act = actions.First(a => ((JObject)a["data"]!)["action"]!.ToString() == actionId);
        act["title"]!.ToString().Should().Contain(titleContains);
    }

    [Fact]
    public void ApprovalCard_renders_tool_name_in_the_visible_text()
    {
        var att = AdaptiveCardBuilder.BuildApprovalCard("microsoft_docs_fetch", "ms_learn", "{}", "appr", "conv");
        AllText(att).Should().Contain(t => t.Contains("microsoft_docs_fetch"));
    }

    [Fact]
    public void ApprovalCard_marks_approve_action_positive_and_deny_destructive()
    {
        var att = MakeApproval();
        var actions = (JArray)((JObject)att.Content)["actions"]!;
        var approve = actions.First(a => ((JObject)a["data"]!)["action"]!.ToString() == "approve");
        var deny    = actions.First(a => ((JObject)a["data"]!)["action"]!.ToString() == "deny");

        approve["style"]?.ToString().Should().Be("positive");
        deny["style"]?.ToString().Should().Be("destructive");
    }

    private static Microsoft.Bot.Schema.Attachment MakeApproval()
        => AdaptiveCardBuilder.BuildApprovalCard("x", "y", "{}", "appr", "conv");

    // ============================================================ Tool call

    [Fact]
    public void ToolCallCard_renders_tool_name_server_label_and_output()
    {
        var att = AdaptiveCardBuilder.BuildToolCallCard(
            toolName: "search", serverLabel: "learn",
            arguments: "{\"q\":\"x\"}", output: "result body", toolKind: "MCP");

        var text = AllText(att);
        text.Should().Contain(t => t.Contains("search"));
        text.Should().Contain(t => t.Contains("learn"));
        text.Should().Contain(t => t.Contains("result body"));
    }

    [Theory]
    [InlineData("MCP",             "🔧")]
    [InlineData("Function",        "🛠️")]
    [InlineData("CodeInterpreter", "🐍")]
    public void ToolCallCard_picks_icon_per_kind(string kind, string icon)
    {
        var att = AdaptiveCardBuilder.BuildToolCallCard("name", "", "{}", null, toolKind: kind);
        AllText(att).Should().Contain(t => t.Contains(icon));
    }

    [Fact]
    public void ToolCallCard_omits_output_section_when_output_null()
    {
        var att = AdaptiveCardBuilder.BuildToolCallCard("name", "", "{}", null, "MCP");
        AllText(att).Should().NotContain(t => t == "Output");
    }

    [Fact]
    public void ToolCallCard_truncates_long_output_with_marker()
    {
        var hugeOutput = new string('z', 5000);
        var att = AdaptiveCardBuilder.BuildToolCallCard("name", "", "{}", hugeOutput, "MCP");
        var allText = string.Join("\n", AllText(att));
        allText.Should().Contain("truncated");
    }

    // ============================================================ Agent picker

    [Fact]
    public void AgentPickerCard_lists_every_descriptor_as_a_choice()
    {
        var descriptors = new[]
        {
            new AgentService.AgentDescriptor("a", "Agent A", "first",  "https://x.example.com/a"),
            new AgentService.AgentDescriptor("b", "Agent B", "second", "https://x.example.com/b"),
            new AgentService.AgentDescriptor("c", "Agent C", "third",  "https://x.example.com/c")
        };

        var att = AdaptiveCardBuilder.BuildAgentPickerCard(descriptors, currentKey: "b");

        var input = FindFirst(att, "Input.ChoiceSet");
        input.Should().NotBeNull();
        ((JArray)input!["choices"]!).Should().HaveCount(3);
        input["value"]!.ToString().Should().Be("b");

        var actions = (JArray)((JObject)att.Content)["actions"]!;
        actions.Should().HaveCount(1);
        ((JObject)actions[0]!["data"]!)["action"]!.ToString().Should().Be("select_agent");
    }

    // ============================================================ Usage

    [Fact]
    public void UsageCard_renders_all_four_stats()
    {
        var att = AdaptiveCardBuilder.BuildUsageCard(promptTokens: 1234, completionTokens: 567, totalTokens: 1801, elapsed: TimeSpan.FromSeconds(2.5));
        var allText = string.Join("\n", AllText(att));
        allText.Should().Contain("1,234");
        allText.Should().Contain("567");
        allText.Should().Contain("1,801");
        allText.Should().Contain("2.5s");
        allText.Should().ContainAll(new[] { "Prompt", "Completion", "Total", "Elapsed" });
    }

    [Fact]
    public void UsageCard_renders_question_marks_when_usage_missing()
    {
        var att = AdaptiveCardBuilder.BuildUsageCard(null, null, null, TimeSpan.Zero);
        string.Join("\n", AllText(att)).Should().Contain("?");
    }

    // ============================================================ Cancel

    [Fact]
    public void CancelCard_has_cancel_action_with_response_metadata()
    {
        var att = AdaptiveCardBuilder.BuildCancelCard("conv_x", "resp_y");
        var actions = (JArray)((JObject)att.Content)["actions"]!;
        actions.Should().HaveCount(1);
        var data = (JObject)actions[0]!["data"]!;
        data["action"]!.ToString().Should().Be("cancel");
        data["conversationId"]!.ToString().Should().Be("conv_x");
        data["responseId"]!.ToString().Should().Be("resp_y");
        actions[0]!["style"]?.ToString().Should().Be("destructive");
    }

    // ============================================================ Connected agent

    [Fact]
    public void ConnectedAgentCard_renders_subagent_name_and_message()
    {
        var att = AdaptiveCardBuilder.BuildConnectedAgentCard("docs_assistant", "Looking up Azure docs");
        var text = AllText(att);
        text.Should().Contain(t => t.Contains("docs_assistant"));
        text.Should().Contain(t => t.Contains("Looking up Azure docs"));
    }

    [Fact]
    public void ConnectedAgentCard_handles_null_message()
    {
        var att = AdaptiveCardBuilder.BuildConnectedAgentCard("sub", null);
        AllText(att).Should().Contain(t => t.Contains("sub"));
    }

    // ============================================================ Help

    [Fact]
    public void HelpCard_renders_each_command()
    {
        var att = AdaptiveCardBuilder.BuildHelpCard(new[]
        {
            ("/agents", "Pick an agent"),
            ("/reset",  "Start over")
        });

        var text = string.Join("\n", AllText(att));
        text.Should().Contain("/agents");
        text.Should().Contain("Pick an agent");
        text.Should().Contain("/reset");
        text.Should().Contain("Start over");
    }

    // ============================================================ Info / Tokens / Agent info

    [Fact]
    public void InfoCard_includes_icon_in_title()
    {
        var att = AdaptiveCardBuilder.BuildInfoCard("Hello", "🎉", new[] { ("k", "v") });
        AllText(att).Should().Contain(t => t.Contains("🎉"));
        AllText(att).Should().Contain(t => t == "Hello");
    }

    [Fact]
    public void InfoCard_renders_facts()
    {
        var att = AdaptiveCardBuilder.BuildInfoCard("Title", null, new[]
        {
            ("Foo", "111"),
            ("Bar", "222")
        });
        var text = AllText(att);
        text.Should().Contain(t => t == "Foo");
        text.Should().Contain(t => t == "111");
        text.Should().Contain(t => t == "Bar");
        text.Should().Contain(t => t == "222");
    }

    [Fact]
    public void InfoCard_renders_section_headers_for_dash_prefixed_labels()
    {
        var att = AdaptiveCardBuilder.BuildInfoCard("T", null, new[]
        {
            ("--- Group A", ""),
            ("k1", "v1"),
            ("--- Group B", ""),
            ("k2", "v2")
        });
        var text = AllText(att);
        text.Should().Contain(t => t.Contains("Group A"));
        text.Should().Contain(t => t.Contains("Group B"));
        text.Should().Contain(t => t == "v1");
        text.Should().Contain(t => t == "v2");
    }

    // ============================================================ Code block

    [Fact]
    public void CodeBlockCard_renders_language_and_code()
    {
        var att = AdaptiveCardBuilder.BuildCodeBlockCard("python", "print('hi')");
        var text = AllText(att);
        text.Should().Contain(t => t.Contains("python"));
        text.Should().Contain(t => t.Contains("print('hi')"));
    }

    // ============================================================ Cross-cutting

    [Fact]
    public void All_cards_use_the_AdaptiveCard_content_type()
    {
        var att1 = AdaptiveCardBuilder.BuildApprovalCard("x", "y", "{}", "appr", "conv");
        var att2 = AdaptiveCardBuilder.BuildCancelCard("conv", "resp");
        var att3 = AdaptiveCardBuilder.BuildToolCallCard("n", "", "{}", null);
        var att4 = AdaptiveCardBuilder.BuildUsageCard(1, 1, 1, TimeSpan.Zero);
        var att5 = AdaptiveCardBuilder.BuildHelpCard(Array.Empty<(string, string)>());
        var att6 = AdaptiveCardBuilder.BuildInfoCard("t", null, Array.Empty<(string, string)>());
        var att7 = AdaptiveCardBuilder.BuildConnectedAgentCard("n", null);
        var att8 = AdaptiveCardBuilder.BuildCodeBlockCard("py", "");

        foreach (var att in new[] { att1, att2, att3, att4, att5, att6, att7, att8 })
        {
            att.ContentType.Should().Be(AdaptiveCard.ContentType);
            att.Content.Should().NotBeNull();
            ((JObject)att.Content)["type"]!.ToString().Should().Be("AdaptiveCard");
        }
    }

    // ============================================================ helpers

    /// <summary>Recursively collects every visible text string from a card.</summary>
    private static List<string> AllText(Microsoft.Bot.Schema.Attachment att)
    {
        var results = new List<string>();
        Walk((JToken)att.Content, results);
        return results;

        static void Walk(JToken node, List<string> sink)
        {
            switch (node)
            {
                case JObject obj:
                    if (obj["type"]?.ToString() == "TextBlock" && obj["text"] is { } txt)
                        sink.Add(txt.ToString());
                    foreach (var p in obj.Properties()) Walk(p.Value, sink);
                    break;
                case JArray arr:
                    foreach (var c in arr) Walk(c, sink);
                    break;
            }
        }
    }

    /// <summary>Finds the first element of the given AdaptiveCard type.</summary>
    private static JObject? FindFirst(Microsoft.Bot.Schema.Attachment att, string adaptiveType)
    {
        JObject? hit = null;
        Walk((JToken)att.Content);
        return hit;

        void Walk(JToken node)
        {
            if (hit is not null) return;
            switch (node)
            {
                case JObject obj when obj["type"]?.ToString() == adaptiveType:
                    hit = obj;
                    return;
                case JObject obj:
                    foreach (var p in obj.Properties()) Walk(p.Value);
                    break;
                case JArray arr:
                    foreach (var c in arr) Walk(c);
                    break;
            }
        }
    }

    /// <summary>Collects every "style" string on every Container in the card.</summary>
    private static List<string> StylesIn(JToken node)
    {
        var sink = new List<string>();
        Walk(node);
        return sink;

        void Walk(JToken n)
        {
            switch (n)
            {
                case JObject obj:
                    if (obj["type"]?.ToString() == "Container" && obj["style"] is { } s)
                        sink.Add(s.ToString());
                    foreach (var p in obj.Properties()) Walk(p.Value);
                    break;
                case JArray arr:
                    foreach (var c in arr) Walk(c);
                    break;
            }
        }
    }
}
