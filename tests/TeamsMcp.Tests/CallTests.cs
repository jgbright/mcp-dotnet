using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The `call` verb's pure halves: command-line tokens into tool arguments against the tool's
/// input schema, and a tool result into console output. The transport wiring around them is
/// verified by hand (`-- call`).
/// </summary>
public class CallTests
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["team"] = new { type = "string" },
            ["limit"] = new { type = "integer" },
            ["ratio"] = new { type = "number" },
            ["include_replies"] = new { type = "boolean" },
            ["chats"] = new { type = "array" },
            // What the SDK generates for a nullable parameter.
            ["body_limit"] = new { type = new[] { "integer", "null" } },
        },
    });

    private static string NoStdin() => throw new InvalidOperationException("stdin was read");

    // ---------------------------------------------------------------- KEY=VALUE pairs

    [Fact]
    public void Pairs_coerce_to_what_the_schema_declares()
    {
        var arguments = Call.ParseArguments(
            Schema, ["team=Ops", "limit=5", "ratio=0.5", "include_replies=true", "body_limit=3"], NoStdin);

        Assert.Equal("Ops", Assert.IsType<string>(arguments["team"]));
        Assert.Equal(5L, Assert.IsType<long>(arguments["limit"]));
        Assert.Equal(0.5, Assert.IsType<double>(arguments["ratio"]));
        Assert.True(Assert.IsType<bool>(arguments["include_replies"]));
        Assert.Equal(3L, Assert.IsType<long>(arguments["body_limit"]));
    }

    [Fact]
    public void An_array_argument_is_taken_as_json()
    {
        var arguments = Call.ParseArguments(Schema, ["""chats=["a","b"]"""], NoStdin);

        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(arguments["chats"]).ValueKind);
    }

    [Fact]
    public void A_value_may_contain_the_separator()
    {
        var arguments = Call.ParseArguments(Schema, ["team=a=b"], NoStdin);

        Assert.Equal("a=b", arguments["team"]);
    }

    [Fact]
    public void No_tokens_means_no_arguments()
        => Assert.Empty(Call.ParseArguments(Schema, [], NoStdin));

    // ------------------------------------------------------------------- refusals

    [Fact]
    public void An_unknown_key_fails_listing_what_the_tool_takes()
    {
        var e = Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["taem=Ops"], NoStdin));

        Assert.Contains("taem", e.Message);
        Assert.Contains("team", e.Message);
        Assert.Contains("limit", e.Message);
    }

    [Fact]
    public void A_value_of_the_wrong_shape_names_the_key_and_the_expectation()
    {
        var e = Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["limit=five"], NoStdin));

        Assert.Contains("limit", e.Message);
        Assert.Contains("integer", e.Message);
    }

    [Fact]
    public void A_token_that_is_no_form_at_all_explains_the_three_forms()
    {
        var e = Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["team", "Ops"], NoStdin));

        Assert.Contains("KEY=VALUE", e.Message);
        Assert.Contains("stdin", e.Message);
    }

    [Fact]
    public void A_repeated_key_is_refused_rather_than_silently_last_wins()
    {
        var e = Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["limit=1", "limit=2"], NoStdin));

        Assert.Contains("limit", e.Message);
    }

    // ------------------------------------------------------------- JSON object forms

    [Fact]
    public void A_single_json_object_argument_passes_through_untouched()
    {
        var arguments = Call.ParseArguments(
            Schema, ["""{"team":"Ops","limit":5}"""], NoStdin);

        Assert.Equal("Ops", Assert.IsType<JsonElement>(arguments["team"]).GetString());
        Assert.Equal(5, Assert.IsType<JsonElement>(arguments["limit"]).GetInt32());
    }

    [Fact]
    public void A_dash_reads_the_json_object_from_stdin()
    {
        var arguments = Call.ParseArguments(Schema, ["-"], () => """{"limit":2}""");

        Assert.Equal(2, Assert.IsType<JsonElement>(arguments["limit"]).GetInt32());
    }

    [Fact]
    public void Broken_json_is_a_terminal_message_not_a_stack_trace()
    {
        Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["""{"team": """], NoStdin));
        Assert.Throws<FormatException>(
            () => Call.ParseArguments(Schema, ["-"], () => "[1,2]"));
    }

    // -------------------------------------------------------------------- rendering

    private static (int Code, string Out, string Err) Rendered(CallToolResult result)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Call.Render(result, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_structured_result_is_the_only_thing_on_stdout()
    {
        var (code, stdout, stderr) = Rendered(new CallToolResult
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new { teams = new[] { "Ops" } }),
        });

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        var parsed = JsonSerializer.Deserialize<JsonElement>(stdout);
        Assert.Equal("Ops", parsed.GetProperty("teams")[0].GetString());
    }

    [Fact]
    public void A_result_with_no_structured_copy_prints_its_text()
    {
        var (code, stdout, _) = Rendered(new CallToolResult
        {
            Content = [new TextContentBlock { Text = """[{"id":1}]""" }],
        });

        Assert.Equal(0, code);
        Assert.Contains("""[{"id":1}]""", stdout);
    }

    [Fact]
    public void An_error_lands_on_stderr_and_exits_nonzero()
    {
        var (code, stdout, stderr) = Rendered(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "no such team. req=3" }],
        });

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("req=3", stderr);
    }

    // ------------------------------------------------------------- name resolution

    private static readonly string[] Names =
        ["list_chats", "list_teams", "read_chat_messages", "wait_for_chat_messages"];

    [Fact]
    public void An_exact_name_wins_even_when_it_is_also_a_substring_of_another()
        => Assert.Equal("list_chats", Call.ResolveTool(["list_chats", "list_chats_x"], "list_chats"));

    [Fact]
    public void A_unique_substring_resolves_like_the_server_s_own_resolvers()
        => Assert.Equal("list_teams", Call.ResolveTool(Names, "teams"));

    [Fact]
    public void Case_never_matters()
        => Assert.Equal("list_teams", Call.ResolveTool(Names, "LIST_TEAMS"));

    [Fact]
    public void An_ambiguous_name_fails_listing_only_the_candidates()
    {
        var e = Assert.Throws<FormatException>(() => Call.ResolveTool(Names, "chat_messages"));

        Assert.Contains("read_chat_messages", e.Message);
        Assert.Contains("wait_for_chat_messages", e.Message);
        Assert.DoesNotContain("list_teams", e.Message);
    }

    [Fact]
    public void No_match_fails_listing_every_tool()
    {
        var e = Assert.Throws<FormatException>(() => Call.ResolveTool(Names, "nope"));

        Assert.Contains("list_chats", e.Message);
        Assert.Contains("list_teams", e.Message);
    }

    // ------------------------------------------------------------------- completion

    [Fact]
    public void Tool_names_back_shell_completion_without_building_the_server()
    {
        var names = Call.ToolNames();

        Assert.Contains("list_teams", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }
}
