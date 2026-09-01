using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsMcp.Tests;

/// <summary>
/// The tools/list surface: the SEP-2549 caching hints and each tool's annotations. No test can
/// call these tools, so what is asserted is the claim itself, and that it matches the tool.
/// </summary>
public class ToolListingTests
{
    private static ListToolsResult Result(params string[] names) =>
        new() { Tools = [.. names.Select(n => new Tool { Name = n })] };

    [Fact]
    public void Prepare_sets_the_caching_hints_a_2026_07_28_client_expects()
    {
        var prepared = ToolListing.Prepare(Result("list_teams"), requestCursor: null);

        Assert.Equal(ToolListing.Ttl, prepared.TimeToLive);
        Assert.Equal(CacheScope.Public, prepared.CacheScope);
    }

    [Fact]
    public void Prepare_orders_the_listing_so_it_is_the_same_on_every_build()
    {
        var prepared = ToolListing.Prepare(
            Result("search_messages", "list_teams", "list_channels"), requestCursor: null);

        Assert.Equal(
            ["list_channels", "list_teams", "search_messages"], prepared.Tools.Select(t => t.Name));
    }

    [Fact]
    public void Prepare_leaves_a_paginated_page_in_the_order_the_handler_produced_it()
    {
        // Sorting one page of several would not make the whole sequence deterministic, and the
        // cursor was issued against the handler's order. The TTL is still prepared.
        var page = Result("search_messages", "list_teams");
        page.NextCursor = "next";

        var prepared = ToolListing.Prepare(page, requestCursor: null);

        Assert.Equal(["search_messages", "list_teams"], prepared.Tools.Select(t => t.Name));
        Assert.Equal(ToolListing.Ttl, prepared.TimeToLive);

        var second = ToolListing.Prepare(Result("search_messages", "list_teams"), requestCursor: "next");
        Assert.Equal(["search_messages", "list_teams"], second.Tools.Select(t => t.Name));
        Assert.Equal(ToolListing.Ttl, second.TimeToLive);
    }

    public static TheoryData<MethodInfo> Tools()
    {
        var data = new TheoryData<MethodInfo>();
        foreach (var m in typeof(TeamsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            data.Add(m);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_tool_declares_whether_it_changes_anything(MethodInfo tool)
    {
        // A client cannot tell list_teams from send_chat_message without this. A new tool that sets
        // neither hint fails here rather than shipping unannotated.
        var attribute = tool.GetCustomAttribute<McpServerToolAttribute>()!;
        var sends = Sends.Contains(attribute.Name!);
        var reacts = Reacts.Contains(attribute.Name!);

        Assert.Equal(!sends && !reacts, attribute.ReadOnly);
        if (sends)
        {
            // A send adds a message and edits nothing, but it posts again every time it is called.
            Assert.False(attribute.Destructive);
            Assert.False(attribute.Idempotent);
        }
        if (reacts)
        {
            // A reaction is self-scoped: setting one that is set, or removing one's own again,
            // lands on the same state, so a retry is safe where a re-send is not.
            Assert.False(attribute.Destructive);
            Assert.True(attribute.Idempotent);
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_gated_tool_says_so_in_its_description(MethodInfo tool)
    {
        var attribute = tool.GetCustomAttribute<McpServerToolAttribute>()!;
        var description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // The annotation is read by the client, the description by the model. A gated tool has to
        // name TEAMS_MCP_ALLOW_SEND, or its refusal reads as a transient failure.
        Assert.Equal(
            Sends.Contains(attribute.Name!) || Reacts.Contains(attribute.Name!),
            description.Contains("TEAMS_MCP_ALLOW_SEND"));
    }

    private static readonly HashSet<string> Sends = ["send_channel_message", "send_chat_message"];

    private static readonly HashSet<string> Reacts = ["react_to_chat_message", "react_to_channel_message"];

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_tool_advertises_an_output_schema(MethodInfo tool)
    {
        // The output schema is only generated when this flag is set, and it tells a model the
        // shape of a result before it spends a call finding out.
        Assert.True(tool.GetCustomAttribute<McpServerToolAttribute>()!.UseStructuredContent);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Message_content_is_called_the_same_thing_in_both_directions(MethodInfo tool)
    {
        // Reads return `body` and take `body_limit`, so a send takes `body` too: a caller that
        // has just read a conversation will supply that name. Message content is never called
        // `text`, which is a value of `format`.
        var parameters = tool.GetParameters().Select(p => p.Name).ToList();

        Assert.DoesNotContain("text", parameters);
        if (Sends.Contains(tool.GetCustomAttribute<McpServerToolAttribute>()!.Name!))
        {
            Assert.Contains("body", parameters);
        }
    }

    private static CallToolResult Call(object? structured, bool isError = false)
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "the same payload, escaped" }],
            IsError = isError,
        };
        if (structured is not null)
        {
            result.StructuredContent = JsonSerializer.SerializeToElement(structured);
        }
        return result;
    }

    [Fact]
    public void Trim_drops_the_text_copy_when_the_structured_copy_says_the_same_thing()
    {
        var trimmed = ToolResults.Trim(Call(new { messages = new[] { new { id = "1" } } }));

        Assert.Empty(trimmed.Content);
        Assert.NotNull(trimmed.StructuredContent);
    }

    [Fact]
    public void Trim_keeps_the_text_copy_of_a_result_that_is_not_a_json_object()
    {
        // structuredContent may only be a bare array from 2026-07-28 on, and these servers still
        // answer older clients, so an array result keeps a text copy they can read.
        var trimmed = ToolResults.Trim(Call(new[] { new { id = "1" } }));

        Assert.Single(trimmed.Content);
    }

    [Fact]
    public void Trim_never_empties_an_error()
    {
        // An error carries a message and no structured content. Emptying it would leave a caller
        // with a failure and no way to read why.
        var trimmed = ToolResults.Trim(Call(structured: null, isError: true));
        Assert.Single(trimmed.Content);

        // An error that does carry structured content keeps its message too.
        var both = ToolResults.Trim(Call(new { message = "nope" }, isError: true));
        Assert.Single(both.Content);
    }

    [Fact]
    public void Trim_leaves_a_result_that_has_no_structured_copy_alone()
    {
        Assert.Single(ToolResults.Trim(Call(structured: null)).Content);
    }

    [Fact]
    public void Every_long_running_tool_is_a_tool_that_exists()
    {
        // The Tasks execution mode is selected by name, so a rename would silently drop a waiter
        // back to blocking a request for an hour.
        var declared = typeof(TeamsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name!)
            .ToHashSet();

        Assert.Subset(declared, ToolExecution.LongRunning);
    }
}
