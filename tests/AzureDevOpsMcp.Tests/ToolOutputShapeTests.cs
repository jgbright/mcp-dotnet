using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Every serialized byte lands in a model's context window, so DTO fields disappear when they have
/// nothing to say. The options here mirror the serializer configuration in Program.cs. If the two
/// drift, tool output silently grows padding fields again.
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
    public void An_ordinary_project_serializes_to_id_and_name_and_nothing_else()
    {
        var json = Serialize(Mapping.Project(new WireProject("p1", "Core", "Core", "wellFormed", "private", null)));

        Assert.False(Has(json, "description"));
        Assert.False(Has(json, "state"));
        Assert.False(Has(json, "visibility"));
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Fact]
    public void An_uninteresting_work_item_carries_no_empty_fields()
    {
        var json = Serialize(new WorkItemDto(
            17, "Bug", "Retry loop spins", "Active", null, null, null, null, null, null, null));

        Assert.False(Has(json, "assignedTo"));
        Assert.False(Has(json, "areaPath"));
        Assert.False(Has(json, "iterationPath"));
        Assert.False(Has(json, "tags"));
        Assert.False(Has(json, "priority"));
        Assert.False(Has(json, "webUrl"));
        Assert.Equal(4, json.EnumerateObject().Count()); // id, type, title, state
    }

    [Fact]
    public void Flags_appear_only_when_they_are_true()
    {
        var json = Serialize(new PullRequestDto(
            42, "Fix", "active", null, null, null, null, null, true, "conflicts", null, null));

        Assert.True(json.GetProperty("draft").GetBoolean());
        Assert.Equal("conflicts", json.GetProperty("mergeStatus").GetString());
    }

    [Fact]
    public void An_empty_envelope_carries_no_has_more_and_no_generated_query()
    {
        var json = Serialize(new WorkItemsResult([], null, null));

        Assert.False(Has(json, "hasMore"));
        Assert.False(Has(json, "wiql"));
        Assert.Empty(json.GetProperty("workItems").EnumerateArray());
    }

    [Fact]
    public void Skipped_counts_are_reported_only_for_the_kind_that_fired()
    {
        var counts = new SkipCounter { System = 3 };

        var json = Serialize(counts.ToDto());

        Assert.Equal(3, json.GetProperty("system").GetInt32());
        Assert.False(Has(json, "deleted"));
        Assert.False(Has(json, "succeeded"));
    }

    [Fact]
    public void Nothing_filtered_means_no_skipped_field_at_all()
    {
        Assert.Null(new SkipCounter().ToDto());
    }

    [Fact]
    public void A_failed_step_with_no_log_requested_carries_neither_tail_nor_flag()
    {
        var json = Serialize(new FailedStepDto("Build", "Compile", "dotnet build", "failed", ["CS0246"], null, null));

        Assert.False(Has(json, "logTail"));
        Assert.False(Has(json, "truncated"));
        Assert.Equal("CS0246", json.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public void A_secret_variable_serializes_to_its_name_and_the_flag()
    {
        var json = Serialize(new ReleaseVariableDto("Stripe.WebhookSecret", null, true, null));

        Assert.False(Has(json, "value"));
        Assert.False(Has(json, "allowOverride"));
        Assert.True(json.GetProperty("isSecret").GetBoolean());
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Fact]
    public void An_ordinary_variable_carries_no_flags_at_all()
    {
        var json = Serialize(new ReleaseVariableDto("OTEL_SERVICE_NAME", "Stripe Webhook", null, null));

        Assert.False(Has(json, "isSecret"));
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Fact]
    public void An_api_response_that_fit_carries_json_and_no_text()
    {
        var json = Serialize(new ApiResponseDto(
            200, "https://vsrm.dev.azure.com/contoso/Core/_apis/release/definitions/31",
            "application/json", JsonSerializer.Deserialize<JsonElement>("""{"id":31}"""), null, null));

        Assert.False(Has(json, "text"));
        Assert.False(Has(json, "truncated"));
        Assert.Equal(31, json.GetProperty("json").GetProperty("id").GetInt32());
    }

    [Fact]
    public void An_unset_personal_access_token_is_absent_rather_than_invalid()
    {
        // A `pat: {valid: false}` where the variable is simply unset would read as a broken
        // credential somebody was meant to have set.
        var json = Serialize(new AuthStatusDto(
            true, "Entra ID", "jason@contoso.com", "Jason Bright", null, null, null, null,
            null, null, "https://dev.azure.com/contoso", "Core", null, null));

        Assert.False(Has(json, "pat"));
        Assert.False(Has(json, "error"));
        Assert.True(json.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public void A_run_that_is_still_going_omits_the_result_rather_than_sending_null()
    {
        var json = Serialize(new PipelineRunDto(77, "20260701.3", "inProgress", null, null, null, null, null, null));

        Assert.False(Has(json, "result"));
        Assert.False(Has(json, "finished"));
        Assert.Equal("inProgress", json.GetProperty("state").GetString());
    }
}
