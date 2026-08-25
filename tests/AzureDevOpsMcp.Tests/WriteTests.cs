using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The JSON Patch document is the whole write. What these builders emit is what reaches Azure
/// DevOps, so the shape is pinned here, op/path/value casing included.
/// </summary>
public class PatchDocumentTests
{
    private static List<Writes.PatchOp> Update(
        string? state = null, string? assignee = null, string? area = null, string? iteration = null,
        string? tags = null, int? priority = null, Writes.Estimates? estimates = null,
        string? title = null, string? description = null,
        string? reproSteps = null, string? acceptanceCriteria = null, string? comment = null) =>
        Writes.UpdatePatch(
            state, assignee, area, iteration, tags, priority, estimates, title, description,
            reproSteps, acceptanceCriteria, comment);

    [Fact]
    public void Only_the_arguments_given_become_operations()
    {
        var ops = Update(state: "Active", comment: "taking this");

        Assert.Collection(ops,
            op => Assert.Equal(("add", "/fields/System.State", "Active"), (op.Op, op.Path, (string?)op.Value)),
            op => Assert.Equal(("add", "/fields/System.History", "taking this"), (op.Op, op.Path, (string?)op.Value)));
    }

    [Fact]
    public void An_update_can_address_every_supported_field()
    {
        var ops = Update(
            "Resolved", "jason@contoso.com", "Core\\Billing", "Core\\Sprint 12", "stripe; qa", 1,
            new Writes.Estimates(4, 4, 0.5, 3, 5),
            "Retry loop spins", "the queue page hangs", "1. open the queue page", "no hang", "done");

        Assert.Equal(
            ["/fields/System.State", "/fields/System.AssignedTo", "/fields/System.AreaPath",
             "/fields/System.IterationPath", "/fields/System.Tags", "/fields/Microsoft.VSTS.Common.Priority",
             "/fields/Microsoft.VSTS.Scheduling.OriginalEstimate",
             "/fields/Microsoft.VSTS.Scheduling.RemainingWork",
             "/fields/Microsoft.VSTS.Scheduling.CompletedWork",
             "/fields/Microsoft.VSTS.Scheduling.StoryPoints",
             "/fields/Microsoft.VSTS.Scheduling.Effort",
             "/fields/System.Title", "/fields/System.Description", "/fields/Microsoft.VSTS.TCM.ReproSteps",
             "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", "/fields/System.History"],
            ops.Select(o => o.Path).ToList());
    }

    [Fact]
    public void A_create_always_starts_with_the_title()
    {
        var ops = Writes.CreatePatch(
            "Retry loop spins", null, "1. open the queue page", null, null, null, null, null, null, null);

        Assert.Equal("/fields/System.Title", ops[0].Path);
        Assert.Equal("Retry loop spins", ops[0].Value);
        Assert.Equal("/fields/Microsoft.VSTS.TCM.ReproSteps", ops[1].Path);
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void The_document_serializes_lowercase_as_json_patch_requires()
    {
        var json = JsonSerializer.Serialize(Update(state: "Active"), AdoClient.Json);

        Assert.Equal("""[{"op":"add","path":"/fields/System.State","value":"Active"}]""", json);
    }

    [Fact]
    public void Priority_reaches_the_wire_as_a_number_because_the_field_is_an_integer()
    {
        var json = JsonSerializer.Serialize(Update(priority: 1), AdoClient.Json);

        Assert.Equal("""[{"op":"add","path":"/fields/Microsoft.VSTS.Common.Priority","value":1}]""", json);
    }

    [Fact]
    public void Clearing_the_last_tag_writes_an_empty_value_rather_than_skipping_the_field()
    {
        var ops = Update(tags: Writes.MergeTags("stripe", null, "stripe"));

        var op = Assert.Single(ops);
        Assert.Equal("replace", op.Op);
        Assert.Equal("/fields/System.Tags", op.Path);
        Assert.Equal("", op.Value);
    }

    /// <summary>
    /// Azure DevOps unions an "add" on System.Tags with the tags already on the item, so a removal
    /// sent that way comes back 200 having changed nothing. The op verb is as load-bearing as the
    /// merged value, which is why it is pinned on the wire rather than on the object.
    /// </summary>
    [Fact]
    public void Tags_are_written_with_replace_because_an_add_can_only_grow_the_list()
    {
        var json = JsonSerializer.Serialize(Update(tags: "billing; qa"), AdoClient.Json);

        Assert.Equal("""[{"op":"replace","path":"/fields/System.Tags","value":"billing; qa"}]""", json);
    }

    [Fact]
    public void An_estimate_is_enough_on_its_own_to_produce_an_operation()
    {
        var ops = Update(estimates: new Writes.Estimates(RemainingWork: 4));

        var op = Assert.Single(ops);
        Assert.Equal("/fields/Microsoft.VSTS.Scheduling.RemainingWork", op.Path);
    }

    [Fact]
    public void An_original_estimate_does_not_carry_remaining_work_with_it()
    {
        // A sprint burndown reads RemainingWork. Writing only the estimate leaves it at zero, so
        // the two are set independently and a caller who means both passes both.
        var ops = Update(estimates: new Writes.Estimates(OriginalEstimate: 4));

        Assert.Equal("/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", Assert.Single(ops).Path);
    }

    [Fact]
    public void An_estimate_reaches_the_wire_as_a_number_so_half_an_hour_survives()
    {
        var json = JsonSerializer.Serialize(
            Update(estimates: new Writes.Estimates(RemainingWork: 0.5)), AdoClient.Json);

        Assert.Equal(
            """[{"op":"add","path":"/fields/Microsoft.VSTS.Scheduling.RemainingWork","value":0.5}]""",
            json);
    }

    [Fact]
    public void A_create_carries_the_estimates_it_is_given()
    {
        var ops = Writes.CreatePatch(
            "Rotate the key", null, null, null, null, null, null, null, null,
            new Writes.Estimates(OriginalEstimate: 4, RemainingWork: 4));

        Assert.Equal(
            ["/fields/System.Title", "/fields/Microsoft.VSTS.Scheduling.OriginalEstimate",
             "/fields/Microsoft.VSTS.Scheduling.RemainingWork"],
            ops.Select(o => o.Path).ToList());
    }

    [Fact]
    public void Tags_are_the_only_field_that_is_not_an_add()
    {
        var ops = Update(
            "Resolved", "jason@contoso.com", "Core\\Billing", "Core\\Sprint 12", "stripe; qa", 1,
            new Writes.Estimates(4, 4, 0.5, 3, 5),
            "Retry loop spins", "the queue page hangs", "1. open the queue page", "no hang", "done");

        Assert.Equal("replace", Assert.Single(ops, o => o.Path == "/fields/System.Tags").Op);
        Assert.All(ops.Where(o => o.Path != "/fields/System.Tags"), op => Assert.Equal("add", op.Op));
    }
}

/// <summary>
/// The parent link is a relation addressed by its index, not a field addressed by name, so getting
/// it wrong removes the wrong link rather than failing. A work item has at most one parent, which
/// is what makes re-parenting a remove followed by an add.
/// </summary>
public class ParentLinkTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private static WireRelation Parent(int id) =>
        new("System.LinkTypes.Hierarchy-Reverse", $"{Org}/_apis/wit/workItems/{id}", null);

    private static WireRelation Other(string rel) => new(rel, $"{Org}/_apis/wit/workItems/99", null);

    [Fact]
    public void An_item_with_no_parent_yet_is_one_bare_add()
    {
        var ops = Writes.SetParent(null, 7847, Org);

        var op = Assert.Single(ops);
        Assert.Equal(("add", "/relations/-"), (op.Op, op.Path));
        Assert.Equal(
            new Writes.RelationRef(
                "System.LinkTypes.Hierarchy-Reverse", "https://dev.azure.com/contoso/_apis/wit/workItems/7847"),
            op.Value);
    }

    [Fact]
    public void Re_parenting_removes_the_existing_link_first_and_addresses_it_by_index()
    {
        List<WireRelation> relations = [Other("System.LinkTypes.Related"), Parent(100), Other("ArtifactLink")];

        var ops = Writes.SetParent(relations, 7847, Org);

        Assert.Collection(ops,
            op => Assert.Equal(("remove", "/relations/1", (object?)null), (op.Op, op.Path, op.Value)),
            op => Assert.Equal("/relations/-", op.Path));
    }

    [Fact]
    public void A_parent_that_is_already_the_parent_is_no_operations_at_all()
    {
        Assert.Empty(Writes.SetParent([Parent(7847)], 7847, Org));
    }

    [Fact]
    public void The_existing_link_is_matched_by_id_rather_than_by_url_spelling()
    {
        // Azure DevOps answers with the url it has, which need not be spelled the way this server
        // spells one — a different host casing or a project segment must not read as a different parent.
        List<WireRelation> relations =
            [new("System.LinkTypes.Hierarchy-Reverse", "https://DEV.azure.com/contoso/proj/_apis/wit/workItems/7847", null)];

        Assert.Empty(Writes.SetParent(relations, 7847, Org));
    }

    [Fact]
    public void Only_the_parent_link_is_a_candidate_for_removal()
    {
        // Hierarchy-Forward is a child, not a parent. Removing one because the rel looked close
        // enough would silently unparent somebody else's work item.
        List<WireRelation> relations = [Other("System.LinkTypes.Hierarchy-Forward"), Other("System.LinkTypes.Related")];

        var ops = Writes.SetParent(relations, 7847, Org);

        Assert.Equal("add", Assert.Single(ops).Op);
    }

    [Fact]
    public void Removing_the_parent_is_one_remove_at_its_index()
    {
        var op = Assert.Single(Writes.RemoveParent([Other("System.LinkTypes.Related"), Parent(100)]));

        Assert.Equal(("remove", "/relations/1"), (op.Op, op.Path));
    }

    [Fact]
    public void Removing_a_parent_that_is_not_there_is_no_operations_at_all()
    {
        Assert.Empty(Writes.RemoveParent([Other("System.LinkTypes.Related")]));
        Assert.Empty(Writes.RemoveParent(null));
    }

    [Fact]
    public void A_remove_carries_no_value_because_json_patch_has_none_to_carry()
    {
        var json = JsonSerializer.Serialize(Writes.RemoveParent([Parent(100)]), AdoClient.Json);

        Assert.Equal("""[{"op":"remove","path":"/relations/0"}]""", json);
    }

    [Fact]
    public void The_relation_value_serializes_as_the_wire_rel_url_pair()
    {
        var json = JsonSerializer.Serialize(Writes.SetParent(null, 7847, Org), AdoClient.Json);

        Assert.Equal(
            """[{"op":"add","path":"/relations/-","value":{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/7847"}}]""",
            json);
    }

    [Fact]
    public void A_trailing_slash_on_the_organization_url_does_not_double_up()
    {
        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/wit/workItems/7847",
            Writes.WorkItemApiUrl("https://dev.azure.com/contoso/", 7847));
    }
}

/// <summary>
/// System.Tags is one semicolon-joined field, so add/remove is a merge. A wrong merge silently
/// destroys tags other people put there.
/// </summary>
public class TagMergeTests
{
    [Fact]
    public void Additions_append_in_order_after_the_existing_tags()
    {
        Assert.Equal("stripe; billing; qa", Writes.MergeTags("stripe; billing", "qa", null));
    }

    [Fact]
    public void An_already_present_tag_is_not_duplicated_and_keeps_its_original_casing()
    {
        Assert.Equal("Stripe; Billing; QA", Writes.MergeTags("Stripe; Billing", "stripe,QA", null));
    }

    [Fact]
    public void Removal_matches_case_insensitively_and_preserves_the_order_of_the_rest()
    {
        Assert.Equal("stripe; qa", Writes.MergeTags("stripe; Billing; qa", null, "billing"));
    }

    [Fact]
    public void Removing_a_tag_that_is_not_there_changes_nothing()
    {
        Assert.Equal("stripe", Writes.MergeTags("stripe", null, "billing"));
    }

    [Fact]
    public void Whitespace_around_entries_is_trimmed_on_both_sides_of_the_merge()
    {
        Assert.Equal("stripe; qa", Writes.MergeTags(" stripe ;  ", " qa , ", null));
    }

    [Fact]
    public void Starting_from_nothing_builds_the_semicolon_joined_value()
    {
        Assert.Equal("stripe; qa", Writes.MergeTags(null, "stripe,qa", null));
    }
}

public class IdentityResolutionTests
{
    [Fact]
    public void The_identity_host_replaces_dev_azure_com()
    {
        Assert.Equal("https://vssps.dev.azure.com/contoso", Writes.VsspsBaseUrl("https://dev.azure.com/contoso"));
    }

    [Fact]
    public void A_legacy_host_gets_its_own_vssps_subdomain()
    {
        Assert.Equal("https://contoso.vssps.visualstudio.com", Writes.VsspsBaseUrl("https://contoso.visualstudio.com"));
    }

    [Fact]
    public void An_unrecognized_host_passes_through_unchanged()
    {
        Assert.Equal("https://tfs.internal:8080/tfs", Writes.VsspsBaseUrl("https://tfs.internal:8080/tfs"));
    }

    [Fact]
    public void The_account_property_is_preferred_over_the_identity_id()
    {
        var identity = new WireIdentitySearchResult(
            "11111111-2222-3333-4444-555555555555", "Jason Bright", null,
            new Dictionary<string, JsonElement>
            {
                ["Account"] = JsonSerializer.Deserialize<JsonElement>(
                    """{"$type":"System.String","$value":"jason@contoso.com"}"""),
            },
            true);

        Assert.Equal("jason@contoso.com", Writes.IdentityValue(identity));
    }

    [Fact]
    public void An_identity_without_an_account_falls_back_to_its_id()
    {
        var identity = new WireIdentitySearchResult(
            "11111111-2222-3333-4444-555555555555", "Jason Bright", null, null, true);

        Assert.Equal("11111111-2222-3333-4444-555555555555", Writes.IdentityValue(identity));
    }

    [Fact]
    public void A_custom_display_name_wins_over_the_provider_one()
    {
        var identity = new WireIdentitySearchResult("id", "Bright, Jason (Contractor)", "Jason Bright", null, null);

        Assert.Equal("Jason Bright", Writes.IdentityDisplayName(identity));
    }
}

/// <summary>
/// Every write tool refuses before touching the network when the gate is unset. The refusal is the
/// same regardless of arguments, and it logs as a rejection rather than an error.
/// </summary>
public class WriteToolTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly AdoTools _tools;

    public WriteToolTests()
    {
        _factory = TestLog.Factory(_sink);
        _tools = new AdoTools(
            new AdoContext(_factory.CreateLogger<AdoContext>()),
            _factory.CreateLogger<AdoTools>());
    }

    public void Dispose()
    {
        _factory.Dispose();
        AdoMcpLog.CurrentRequest = null;
    }

    [Fact]
    public async Task Update_refuses_without_the_gate()
    {
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateWorkItem(17, state: "Active"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("update_work_item rejected"));
    }

    [Fact]
    public async Task Create_refuses_without_the_gate()
    {
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.CreateWorkItem("Bug", "Retry loop spins"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
    }

    [Fact]
    public async Task Commenting_refuses_without_the_gate()
    {
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.AddPullRequestComment(42, "lgtm"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
    }

    [Fact]
    public async Task An_update_with_nothing_to_change_is_rejected_before_any_request()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateWorkItem(17));

        Assert.Contains("Nothing to change", e.Message);
    }

    [Fact]
    public async Task An_update_that_only_sets_an_estimate_has_something_to_change()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        // It fails for want of an organization, which comes after the guard. The point is that an
        // estimate on its own is not read as "nothing to change".
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateWorkItem(17, remaining_work: 4));

        Assert.DoesNotContain("Nothing to change", e.Message);
    }

    [Fact]
    public async Task Asking_to_set_and_to_clear_the_parent_at_once_is_rejected_before_any_request()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        var e = await Assert.ThrowsAsync<McpException>(
            () => _tools.UpdateWorkItem(17, parent: 100, remove_parent: true));

        Assert.Contains("opposite", e.Message);
    }

    [Fact]
    public async Task An_item_cannot_be_made_its_own_parent()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.UpdateWorkItem(17, parent: 17));

        Assert.Contains("its own parent", e.Message);
    }

    [Fact]
    public async Task A_reply_target_without_its_thread_is_rejected_before_any_request()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        var e = await Assert.ThrowsAsync<McpException>(
            () => _tools.AddPullRequestComment(42, "lgtm", parent_comment_id: 3));

        Assert.Contains("thread_id", e.Message);
    }

    [Fact]
    public void The_created_comment_is_described_by_length_only()
    {
        var described = AdoTools.Describe(new PullRequestCommentResult(
            42, 7, new CommentDto(1, "Jason Bright", null, null, "do not merge until friday", null), null));

        Assert.Contains(" pullRequest=42", described);
        Assert.Contains(" thread=7", described);
        Assert.Contains(" comment.len=25", described);
        Assert.DoesNotContain("friday", described);
    }
}
