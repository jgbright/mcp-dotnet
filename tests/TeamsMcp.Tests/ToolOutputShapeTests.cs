using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// DTO fields are omitted when they have nothing to say, because every serialized byte lands in a
/// model's context window. These options mirror the serializer in Program.cs. If the two drift,
/// tool output quietly grows padding fields again.
/// </summary>
public class ToolOutputShapeTests
{
    private static readonly JsonSerializerOptions Options =
        new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static JsonElement Serialize<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Options)).RootElement;

    private static bool Has(JsonElement element, string name) =>
        element.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void An_uninteresting_message_serializes_to_almost_nothing()
    {
        var json = Serialize(new MessageDto(
            "1", null, null, DateTimeOffset.UnixEpoch, "Mike", "hello", null, null, null, null, null));

        Assert.False(Has(json, "replyToId"));
        Assert.False(Has(json, "messageType"));
        Assert.False(Has(json, "truncated"));
        Assert.False(Has(json, "edited"));
        Assert.False(Has(json, "attachments"));
        Assert.False(Has(json, "reactions"));
        Assert.False(Has(json, "replies"));
        Assert.Equal(4, json.EnumerateObject().Count()); // id, created, sender, body
    }

    [Fact]
    public void Flags_appear_only_when_they_are_true()
    {
        var json = Serialize(new MessageDto(
            "1", null, null, null, null, "hel", true, true, null, null, null));

        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.True(json.GetProperty("edited").GetBoolean());
    }

    [Fact]
    public void An_empty_message_envelope_carries_no_has_more_or_skipped()
    {
        var json = Serialize(new MessagesResult([], null, null));

        Assert.False(Has(json, "hasMore"));
        Assert.False(Has(json, "skipped"));
        Assert.Empty(json.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public void Skipped_counts_are_reported_only_for_the_kind_that_fired()
    {
        var json = Serialize(new MessagesResult([], true, new SkippedDto(null, 3)));

        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, json.GetProperty("skipped").GetProperty("system").GetInt32());
        Assert.False(Has(json.GetProperty("skipped"), "deleted"));
    }

    [Fact]
    public void A_team_without_a_meaningful_description_serializes_without_the_field()
    {
        var json = Serialize(new TeamDto("id", "Platform", TeamsTools.TrimDescription("Platform", "Platform")));

        Assert.False(Has(json, "description"));
    }
}
