using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AzureDevOpsMcp.Tests;

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
        var prepared = ToolListing.Prepare(Result("list_projects"), requestCursor: null);

        Assert.Equal(ToolListing.Ttl, prepared.TimeToLive);
        Assert.Equal(CacheScope.Public, prepared.CacheScope);
    }

    [Fact]
    public void Prepare_orders_the_listing_so_it_is_the_same_on_every_build()
    {
        var prepared = ToolListing.Prepare(
            Result("search_wiki", "get_work_item", "list_projects", "deployment_status"),
            requestCursor: null);

        Assert.Equal(
            ["deployment_status", "get_work_item", "list_projects", "search_wiki"],
            prepared.Tools.Select(t => t.Name));
    }

    [Fact]
    public void Prepare_leaves_a_paginated_page_in_the_order_the_handler_produced_it()
    {
        // Sorting one page of several would not make the whole sequence deterministic, and the
        // cursor was issued against the handler's own order. The TTL is still prepared.
        var page = Result("search_wiki", "get_work_item");
        page.NextCursor = "next";

        var prepared = ToolListing.Prepare(page, requestCursor: null);

        Assert.Equal(["search_wiki", "get_work_item"], prepared.Tools.Select(t => t.Name));
        Assert.Equal(ToolListing.Ttl, prepared.TimeToLive);

        var second = ToolListing.Prepare(Result("search_wiki", "get_work_item"), requestCursor: "next");
        Assert.Equal(["search_wiki", "get_work_item"], second.Tools.Select(t => t.Name));
        Assert.Equal(ToolListing.Ttl, second.TimeToLive);
    }

    public static TheoryData<MethodInfo> Tools()
    {
        var data = new TheoryData<MethodInfo>();
        foreach (var m in typeof(AdoTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
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
        // A client cannot tell list_projects from create_work_item without this. A new tool that
        // sets neither hint fails here rather than shipping unannotated.
        var attribute = tool.GetCustomAttribute<McpServerToolAttribute>()!;
        var mutates = Writes.Contains(attribute.Name!);

        Assert.Equal(!mutates, attribute.ReadOnly);
        if (mutates)
        {
            Assert.Equal(Destructive.Contains(attribute.Name!), attribute.Destructive);
            Assert.False(attribute.Idempotent);
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_mutating_tool_says_so_in_its_description(MethodInfo tool)
    {
        var attribute = tool.GetCustomAttribute<McpServerToolAttribute>()!;
        var description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // The annotation is for the client, the description for the model: a gated tool has to
        // name ADO_MCP_ALLOW_WRITE or a refusal looks like a transient failure. ado_api_request
        // names it without being a write tool, because it gates its non-GET methods on the same
        // variable.
        Assert.Equal(
            Writes.Contains(attribute.Name!) || attribute.Name == "ado_api_request",
            description.Contains("ADO_MCP_ALLOW_WRITE"));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Only_the_approval_tool_names_the_approval_gate(MethodInfo tool)
    {
        // approve_release refuses on a second variable no other tool consults; a model told only
        // about ADO_MCP_ALLOW_WRITE would read that refusal as a bug.
        var attribute = tool.GetCustomAttribute<McpServerToolAttribute>()!;
        var description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.Equal(attribute.Name == "approve_release", description.Contains("ADO_MCP_ALLOW_APPROVE"));
    }

    private static readonly HashSet<string> Writes =
    [
        "create_work_item", "update_work_item", "add_pull_request_comment", "run_pipeline",
        "deploy_release", "approve_release",
    ];

    /// <summary>
    /// The mutations that replace something rather than add to it, which is what an MCP client
    /// gates a confirmation prompt on. Queueing a run or filing a work item adds; overwriting
    /// fields or deploying a release replaces. approve_release is here because approving is what
    /// lets the deployment happen.
    /// </summary>
    private static readonly HashSet<string> Destructive =
        ["update_work_item", "deploy_release", "approve_release"];

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_tool_advertises_an_output_schema(MethodInfo tool)
    {
        // The schema tells a model the shape of a result before it spends a call finding out, and
        // the SDK only generates one when this flag is set.
        Assert.True(tool.GetCustomAttribute<McpServerToolAttribute>()!.UseStructuredContent);
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
        var trimmed = ToolResults.Trim(Call(new { workItems = new[] { new { id = 1 } } }));

        Assert.Empty(trimmed.Content);
        Assert.NotNull(trimmed.StructuredContent);
    }

    [Fact]
    public void Trim_keeps_the_text_copy_of_a_result_that_is_not_a_json_object()
    {
        // structuredContent may only be a bare array from 2026-07-28 on, and these servers still
        // answer older clients, so list_projects and friends keep something readable.
        var trimmed = ToolResults.Trim(Call(new[] { new { id = 1 } }));

        Assert.Single(trimmed.Content);
    }

    [Fact]
    public void Trim_never_empties_an_error()
    {
        // An error carries a message and no structured content. Emptying it would leave a caller
        // with a failure and no way to read why.
        var trimmed = ToolResults.Trim(Call(structured: null, isError: true));
        Assert.Single(trimmed.Content);

        // An error that does carry structured content still keeps its message.
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
        // back to blocking a request for half an hour.
        var declared = typeof(AdoTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name!)
            .ToHashSet();

        Assert.Subset(declared, ToolExecution.LongRunning);
    }
}
