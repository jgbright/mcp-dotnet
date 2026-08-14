using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The advertised outputSchema against what the serializer actually emits. Every DTO here is a
/// positional record whose nullable fields are omitted when null, so a schema that requires them
/// makes a validating client reject a good result.
/// </summary>
public class OutputSchemaTests
{
    /// <summary>The schema the SDK generates for a tool returning <typeparamref name="T"/>.</summary>
    private static ListToolsResult Listing<T>(string name = "a_tool") => new()
    {
        Tools =
        [
            new Tool
            {
                Name = name,
                OutputSchema = AIJsonUtilities.CreateJsonSchema(
                    typeof(T), serializerOptions: null, inferenceOptions: AIJsonSchemaCreateOptions.Default),
            },
        ],
    };

    private static string[] Required(Tool tool, params string[] path)
    {
        var node = tool.OutputSchema!.Value;
        foreach (var step in path)
        {
            node = node.GetProperty(step);
        }
        return node.TryGetProperty("required", out var required)
            ? [.. required.EnumerateArray().Select(e => e.GetString()!)]
            : [];
    }

    [Fact]
    public void A_nullable_field_is_not_required()
    {
        // list_teams returns these, and a team with no description is what broke a real session:
        // the payload omits `description`, the schema demanded it, the client rejected the call.
        var relaxed = OutputSchemas.Relax(Listing<List<TeamDto>>());

        Assert.Empty(Required(relaxed.Tools[0], "items"));
    }

    [Fact]
    public void A_field_that_can_never_be_null_stays_required()
    {
        // Messages is a non-nullable list and is always written. Relaxing it too would tell a model
        // the one field it can count on is optional.
        var relaxed = OutputSchemas.Relax(Listing<MessagesResult>());

        Assert.Equal(["messages"], Required(relaxed.Tools[0]));
    }

    [Fact]
    public void Nested_schemas_are_reached_too()
    {
        // The nullable fields that broke read_chat_messages sit on MessageDto, one level down
        // inside the envelope, not on the envelope itself.
        var relaxed = OutputSchemas.Relax(Listing<MessagesResult>());
        var json = JsonSerializer.Serialize(relaxed.Tools[0].OutputSchema);

        Assert.DoesNotContain("replyToId\",\"messageType", json);
        Assert.Contains("replyToId", json);
    }

    [Fact]
    public void A_tool_with_no_output_schema_is_left_alone()
    {
        var result = new ListToolsResult { Tools = [new Tool { Name = "a_tool" }] };

        Assert.Null(OutputSchemas.Relax(result).Tools[0].OutputSchema);
    }

    [Fact]
    public void Stamping_the_listing_relaxes_it()
    {
        // The filter in Program.cs only calls Stamp, so the relaxation has to happen underneath it.
        var stamped = ToolListing.Stamp(Listing<List<TeamDto>>(), requestCursor: null);

        Assert.Empty(Required(stamped.Tools[0], "items"));
    }
}
