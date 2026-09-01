using System.Text.Json;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Mapping is where the output shape is enforced: a field that repeats the common case is nulled
/// out here, and anything filtered is counted rather than dropped.
/// </summary>
public class ProjectAndRepoMappingTests
{
    [Fact]
    public void A_normal_project_carries_neither_state_nor_visibility()
    {
        var dto = Mapping.Project(new WireProject("id", "Core", null, "wellFormed", "private", null));

        Assert.Null(dto.State);
        Assert.Null(dto.Visibility);
        Assert.Equal("Core", dto.Name);
    }

    [Fact]
    public void An_unusual_project_state_is_kept_because_it_explains_a_failure()
    {
        var dto = Mapping.Project(new WireProject("id", "Core", null, "createPending", "public", null));

        Assert.Equal("createPending", dto.State);
        Assert.Equal("public", dto.Visibility);
    }

    [Fact]
    public void A_description_that_only_repeats_the_name_is_dropped()
    {
        Assert.Null(Mapping.Project(new WireProject("id", "Core", "Core", null, null, null)).Description);
    }

    [Fact]
    public void A_long_description_is_cut_short_rather_than_carried_whole()
    {
        var dto = Mapping.Project(new WireProject("id", "Core", new string('x', 500), null, null, null));

        Assert.Equal(101, dto.Description!.Length); // 100 + the ellipsis
        Assert.EndsWith("…", dto.Description);
    }

    [Fact]
    public void Repository_default_branches_lose_the_refs_heads_prefix()
    {
        var dto = Mapping.Repo(new WireRepo("id", "core", "refs/heads/main", "https://x", false, null));

        Assert.Equal("main", dto.DefaultBranch);
        Assert.Null(dto.Disabled); // false does not earn a field
    }

    [Fact]
    public void A_disabled_repository_says_so()
    {
        Assert.True(Mapping.Repo(new WireRepo("id", "core", null, null, true, null)).Disabled);
    }
}

public class PullRequestMappingTests
{
    private static WireRepo Repo() => new("r1", "core", null, null, null, new WireProjectRef("p1", "Core"));

    private static WirePullRequest Pr(
        string? mergeStatus = "succeeded", bool? draft = null, List<WireReviewer>? reviewers = null) =>
        new(42, "Fix the retry loop", "body", "active", new WireIdentity("Mike", "mike@contoso.example", "u1"),
            DateTimeOffset.UnixEpoch, null, "refs/heads/fix/retry", "refs/heads/main", draft, mergeStatus,
            Repo(), reviewers);

    [Fact]
    public void Branches_are_shortened_and_the_browser_url_is_constructed()
    {
        var dto = Mapping.PullRequest(Pr(), true);

        Assert.Equal("fix/retry", dto.SourceBranch);
        Assert.Equal("main", dto.TargetBranch);
    }

    [Fact]
    public void Project_and_repository_names_are_escaped_into_the_url()
    {
        var pr = Pr() with
        {
            Repository = new WireRepo("r1", "web site", null, null, null, new WireProjectRef("p1", "My Project")),
        };

        // The detail shape still carries the address, and escaping is the part worth pinning.
        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_git/web%20site/pullrequest/42",
            Mapping.PullRequestUrl("https://dev.azure.com/contoso", pr));
    }

    [Fact]
    public void The_repository_name_is_omitted_when_the_caller_already_named_one()
    {
        Assert.Null(Mapping.PullRequest(Pr(), false).Repo);
        Assert.Equal("core", Mapping.PullRequest(Pr(), true).Repo);
    }

    [Fact]
    public void A_healthy_merge_status_is_omitted_and_a_blocked_one_is_not()
    {
        Assert.Null(Mapping.PullRequest(Pr(), true).MergeStatus);
        Assert.Equal("conflicts", Mapping.PullRequest(Pr("conflicts"), true).MergeStatus);
    }

    [Theory]
    [InlineData(10, "approved")]
    [InlineData(5, "approved with suggestions")]
    [InlineData(-5, "waiting for author")]
    [InlineData(-10, "rejected")]
    public void Votes_are_translated_out_of_their_numeric_scale(int vote, string expected)
    {
        Assert.Equal(expected, Mapping.Vote(vote));
    }

    [Fact]
    public void No_vote_yet_says_nothing()
    {
        Assert.Null(Mapping.Vote(0));
        Assert.Null(Mapping.Vote(null));
    }

    [Fact]
    public void An_empty_reviewer_list_becomes_null_rather_than_an_empty_array()
    {
        Assert.Null(Mapping.Reviewers([]));
        Assert.Null(Mapping.Reviewers(null));
    }
}

public class ThreadMappingTests
{
    private static WireComment Comment(
        int id, string? content = "looks good", string? type = "text", bool? deleted = null) =>
        new(id, null, new WireIdentity("Mike", null, null), content, type, DateTimeOffset.UnixEpoch, deleted);

    [Fact]
    public void A_normal_thread_keeps_its_comments_and_omits_the_default_comment_type()
    {
        var counts = new SkipCounter();

        var dto = Mapping.Thread(new WireThread(1, "active", [Comment(1)], null, null), false, 2000, counts);

        Assert.Equal("looks good", Assert.Single(dto!.Comments).Body);
        Assert.Null(dto.Comments[0].Type);
        Assert.Null(counts.ToDto());
    }

    [Fact]
    public void System_comments_are_counted_not_returned()
    {
        var counts = new SkipCounter();

        var dto = Mapping.Thread(
            new WireThread(1, "closed", [Comment(1, "Mike voted", "system")], null, null), false, 2000, counts);

        Assert.Null(dto); // nothing is left once the system comment is filtered
        Assert.Equal(1, counts.ToDto()!.System);
        Assert.Null(counts.ToDto()!.Deleted);
    }

    [Fact]
    public void System_comments_can_be_asked_for_and_then_carry_their_type()
    {
        var counts = new SkipCounter();

        var dto = Mapping.Thread(
            new WireThread(1, "closed", [Comment(1, "Mike voted", "system")], null, null), true, 2000, counts);

        Assert.Equal("system", Assert.Single(dto!.Comments).Type);
        Assert.Null(counts.ToDto());
    }

    [Fact]
    public void Deleted_comments_are_counted_and_a_deleted_thread_is_counted_once()
    {
        var counts = new SkipCounter();

        Mapping.Thread(new WireThread(1, null, [Comment(1, deleted: true)], null, null), false, 2000, counts);
        Mapping.Thread(new WireThread(2, null, [Comment(2)], null, true), false, 2000, counts);

        Assert.Equal(2, counts.ToDto()!.Deleted);
    }

    [Fact]
    public void File_and_line_come_from_the_thread_context_when_it_is_a_code_comment()
    {
        var context = new WireThreadContext("/src/AdoTools.cs", new WireFilePosition(120, 1), null);

        var dto = Mapping.Thread(new WireThread(1, "active", [Comment(1)], context, null), false, 2000, new SkipCounter());

        Assert.Equal("/src/AdoTools.cs", dto!.FilePath);
        Assert.Equal(120, dto.Line);
    }

    [Fact]
    public void Comment_bodies_are_converted_from_markdown_and_truncated()
    {
        var dto = Mapping.Thread(
            new WireThread(1, null, [Comment(1, "**ship** it")], null, null), false, 4, new SkipCounter());

        Assert.Equal("ship", Assert.Single(dto!.Comments).Body);
        Assert.True(dto.Comments[0].Truncated);
    }
}

public class WorkItemMappingTests
{
    private static Dictionary<string, JsonElement> Fields(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private const string Typical = """
        {
          "System.TeamProject": "Core",
          "System.WorkItemType": "Bug",
          "System.Title": "Retry loop spins",
          "System.State": "Active",
          "System.AssignedTo": { "displayName": "Mike", "uniqueName": "mike@contoso.example" },
          "System.ChangedDate": "2026-07-01T12:00:00Z",
          "System.AreaPath": "Core\\Platform",
          "System.IterationPath": "Core",
          "System.Tags": "sql; regression ; ",
          "Microsoft.VSTS.Common.Priority": 2
        }
        """;

    [Fact]
    public void Fields_are_flattened_and_the_assignee_reduced_to_a_display_name()
    {
        var dto = Mapping.WorkItem(new WireWorkItem(17, Fields(Typical), null, null));

        Assert.Equal(17, dto.Id);
        Assert.Equal("Bug", dto.Type);
        Assert.Equal("Retry loop spins", dto.Title);
        Assert.Equal("Mike", dto.AssignedTo);
        Assert.Equal(2, dto.Priority);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), dto.Changed);
    }

    [Fact]
    public void A_query_row_carries_no_address_because_the_id_and_the_organization_are_the_address()
    {
        // Every row of every listing paid for a string the caller can rebuild from the id and the
        // organization this server is pinned to. The detail shapes still carry it.
        var dto = Mapping.WorkItem(new WireWorkItem(17, Fields(Typical), null, null));

        Assert.DoesNotContain("WebUrl", dto.GetType().GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void An_area_path_that_is_just_the_project_carries_no_information()
    {
        var dto = Mapping.WorkItem(new WireWorkItem(17, Fields(Typical), null, null));

        Assert.Equal("Core\\Platform", dto.AreaPath);
        Assert.Null(dto.IterationPath); // "Core" is the project itself
    }

    [Fact]
    public void Tags_are_split_and_trimmed()
    {
        var dto = Mapping.WorkItem(new WireWorkItem(17, Fields(Typical), null, null));

        Assert.Equal(["sql", "regression"], dto.Tags);
    }

    [Fact]
    public void No_tags_means_no_field_at_all()
    {
        Assert.Null(Mapping.Tags(null));
        Assert.Null(Mapping.Tags(""));
        Assert.Null(Mapping.Tags("  ;  "));
    }

    [Fact]
    public void A_work_item_with_almost_no_fields_maps_without_throwing()
    {
        var dto = Mapping.WorkItem(new WireWorkItem(9, null, null, null));

        Assert.Equal(9, dto.Id);
        Assert.Null(dto.Title);
    }

    [Fact]
    public void Html_body_fields_are_converted_and_a_truncation_anywhere_is_flagged_once()
    {
        var fields = Fields("""
            {
              "System.TeamProject": "Core",
              "System.Description": "<p>see <a href=\"https://contoso.example\">the doc</a></p>",
              "Microsoft.VSTS.TCM.ReproSteps": "<ol><li>run it</li></ol>"
            }
            """);

        var dto = Mapping.WorkItemDetail(new WireWorkItem(3, fields, null, null), 8, "https://x", null, null);

        Assert.Equal("see the ", dto.Description); // "see the doc (https://contoso.example)", cut at 8
        Assert.Equal("- run it", dto.ReproSteps);  // exactly 8, so this field did not set the flag
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void The_scheduling_fields_are_read_back_as_the_numbers_they_are()
    {
        // Every one of these is writable by the two work item tools, so a model that reads one
        // can also fix it. Fractions survive because they are doubles.
        var fields = Fields("""
            {
              "Microsoft.VSTS.Scheduling.OriginalEstimate": 4,
              "Microsoft.VSTS.Scheduling.RemainingWork": 3.5,
              "Microsoft.VSTS.Scheduling.CompletedWork": 0.5,
              "Microsoft.VSTS.Scheduling.StoryPoints": 8,
              "Microsoft.VSTS.Scheduling.Effort": 13
            }
            """);

        var dto = Mapping.WorkItemDetail(new WireWorkItem(3, fields, null, null), 0, "https://x", null, null);

        Assert.Equal(4, dto.OriginalEstimate);
        Assert.Equal(3.5, dto.RemainingWork);
        Assert.Equal(0.5, dto.CompletedWork);
        Assert.Equal(8, dto.StoryPoints);
        Assert.Equal(13, dto.Effort);
    }

    [Fact]
    public void A_type_that_defines_no_estimate_says_nothing_rather_than_zero()
    {
        // Omitted, not 0. A Task with no estimate and a Bug that cannot have one both report
        // nothing; a zero would read as "estimated at none".
        var dto = Mapping.WorkItemDetail(
            new WireWorkItem(3, Fields("""{ "System.State": "New" }"""), null, null),
            0, "https://x", null, null);

        Assert.Null(dto.OriginalEstimate);
        Assert.Null(dto.StoryPoints);
    }

    [Fact]
    public void A_reason_that_merely_repeats_the_state_is_dropped()
    {
        var fields = Fields("""{ "System.State": "New", "System.Reason": "New" }""");

        Assert.Null(Mapping.WorkItemDetail(new WireWorkItem(3, fields, null, null), 0, "https://x", null, null).Reason);
    }

    [Fact]
    public void Empty_comment_lists_are_omitted_rather_than_serialized_as_an_empty_array()
    {
        var dto = Mapping.WorkItemDetail(new WireWorkItem(3, null, null, null), 0, "https://x", [], null);

        Assert.Null(dto.Comments);
    }

    [Fact]
    public void A_deleted_comment_is_counted_and_not_returned()
    {
        var counts = new SkipCounter();

        var kept = Mapping.WorkItemComment(
            new WireWorkItemComment(1, "<p>looks fine</p>", new WireIdentity("Mike", null, null),
                DateTimeOffset.UnixEpoch, null, null), 2000, counts);
        var dropped = Mapping.WorkItemComment(
            new WireWorkItemComment(2, "oops", null, null, null, true), 2000, counts);

        Assert.Equal("looks fine", kept!.Body);
        Assert.Null(dropped);
        Assert.Equal(1, counts.ToDto()!.Deleted);
    }

    [Fact]
    public void A_named_projection_returns_those_fields_and_nothing_else()
    {
        // A caller naming its own fields is answering a question the typed shape does not cover,
        // so the typed properties stay null and serialize away rather than padding the result.
        var dto = Mapping.WorkItemFields(
            new WireWorkItem(17, Fields(Typical), null, null),
            ["System.State", "System.AssignedTo"]);

        Assert.Equal(17, dto.Id);
        Assert.Null(dto.Title);
        Assert.Null(dto.WebUrl);
        Assert.Equal("Active", dto.Fields!["System.State"]);
        // An identity flattens to its display name here too: the caller wants the value, not the
        // service's envelope around it.
        Assert.Equal("Mike", dto.Fields["System.AssignedTo"]);
        Assert.Equal(2, dto.Fields.Count);
    }

    [Fact]
    public void A_field_is_found_whatever_casing_the_caller_used()
    {
        // The service accepts any casing in the request but answers with canonical reference
        // names, so an exact-case lookup would drop a field the service returned — and the miss
        // would read exactly like "the item does not carry this field".
        var dto = Mapping.WorkItemFields(
            new WireWorkItem(17, Fields(Typical), null, null), ["system.state"]);

        var field = Assert.Single(dto.Fields!);
        Assert.Equal("system.state", field.Key); // keyed as the caller named it
        Assert.Equal("Active", field.Value);
    }

    [Fact]
    public void A_field_the_item_does_not_carry_is_left_out_rather_than_returned_empty()
    {
        var dto = Mapping.WorkItemFields(
            new WireWorkItem(17, Fields(Typical), null, null),
            ["System.State", "Microsoft.VSTS.CodeReview.ClosedStatus"]);

        Assert.False(dto.Fields!.ContainsKey("Microsoft.VSTS.CodeReview.ClosedStatus"));
        Assert.Single(dto.Fields);
    }

    [Fact]
    public void A_projection_that_matches_no_field_at_all_is_null_not_an_empty_map()
    {
        var dto = Mapping.WorkItemFields(
            new WireWorkItem(17, Fields(Typical), null, null), ["Custom.Nope"]);

        Assert.Null(dto.Fields);
    }

    [Fact]
    public void A_numeric_field_keeps_its_own_text()
    {
        var dto = Mapping.WorkItemFields(
            new WireWorkItem(17, Fields(Typical), null, null), ["Microsoft.VSTS.Common.Priority"]);

        Assert.Equal("2", dto.Fields!["Microsoft.VSTS.Common.Priority"]);
    }

    [Fact]
    public void A_projected_query_row_carries_the_id_and_the_named_fields_only()
    {
        var dto = Mapping.WorkItemRowFields(
            new WireWorkItem(17, Fields(Typical), null, null), ["System.State"]);

        Assert.Equal(17, dto.Id);
        Assert.Equal("Active", dto.Fields!["System.State"]);
        Assert.Single(dto.Fields);
        // The summary columns are not a second answer to a question that named its own fields.
        Assert.Null(dto.Title);
        Assert.Null(dto.Type);
        Assert.Null(dto.AreaPath);
        Assert.Null(dto.Tags);
        Assert.Null(dto.Priority);
    }

    [Fact]
    public void A_write_echo_carries_identity_and_what_was_written()
    {
        var dto = Mapping.WorkItemWritten(
            new WireWorkItem(17, Fields(Typical), null, null), ["System.State"], "https://dev.azure.com/contoso");

        // Enough to recognize the item...
        Assert.Equal(17, dto.Id);
        Assert.Equal("Bug", dto.Type);
        Assert.Equal("Retry loop spins", dto.Title);
        Assert.Equal("Active", dto.State);
        Assert.Equal("https://dev.azure.com/contoso/Core/_workitems/edit/17", dto.WebUrl);
        // ...plus the value that landed, read back from the service's own response.
        Assert.Equal("Active", dto.Fields!["System.State"]);
        // ...and nothing else. The rest of the item was not asked about.
        Assert.Null(dto.AreaPath);
        Assert.Null(dto.Description);
        Assert.Null(dto.Tags);
        Assert.Null(dto.Priority);
    }

    [Fact]
    public void A_reparent_echo_carries_the_relations_that_confirm_it()
    {
        // The reparent PATCH asks for $expand=relations because the parent link cannot be
        // confirmed any other way, so the echo must not drop them. Every other write leaves
        // relations off the wire and off the echo alike.
        var attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{ "name": "Parent" }""")!;
        var dto = Mapping.WorkItemWritten(
            new WireWorkItem(17, Fields(Typical),
                [new WireRelation("System.LinkTypes.Hierarchy-Reverse",
                    "https://dev.azure.com/contoso/_apis/wit/workItems/7834", attributes)],
                null),
            [], "https://dev.azure.com/contoso");

        var relation = Assert.Single(dto.Relations!);
        Assert.Equal(7834, relation.WorkItemId);
    }

    [Fact]
    public void The_detail_field_list_covers_what_the_detail_shape_reads()
    {
        // A batched read has to name its fields up front where a single read gets them through
        // $expand, so a body missing from this list comes back empty for no visible reason.
        Assert.Contains("System.Description", Mapping.DetailFields);
        Assert.Contains("Microsoft.VSTS.TCM.ReproSteps", Mapping.DetailFields);
        Assert.Contains("Microsoft.VSTS.Common.AcceptanceCriteria", Mapping.DetailFields);
        Assert.Contains("System.Title", Mapping.DetailFields);
        Assert.Equal(Mapping.DetailFields.Length, Mapping.DetailFields.Distinct().Count());
    }
}

public class WorkItemIdParsingTests
{
    [Fact]
    public void Ids_parse_as_the_comma_separated_list_the_batch_endpoint_takes()
    {
        Assert.Equal([7877, 7834, 7740], AdoTools.ParseIds("7877,7834,7740"));
        Assert.Equal([7877, 7834], AdoTools.ParseIds(" 7877 , 7834 "));
        // The same id twice would ask the service for it twice and answer it once.
        Assert.Equal([7877], AdoTools.ParseIds("7877,7877"));
    }

    [Fact]
    public void A_non_numeric_id_is_named_rather_than_dropped()
    {
        // Dropping it would answer with fewer items than were asked for, which is the exact
        // failure this tool exists to remove.
        var error = Assert.Throws<McpException>(() => AdoTools.ParseIds("7877,AB#7834,oops"));

        Assert.Contains("'AB#7834'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'oops'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_oversized_list_is_refused_with_the_limit_named()
    {
        Assert.Throws<McpException>(() => AdoTools.ParseIds(""));
        Assert.Throws<McpException>(() => AdoTools.ParseIds(" , "));

        var tooMany = string.Join(",", Enumerable.Range(1, 201));
        var error = Assert.Throws<McpException>(() => AdoTools.ParseIds(tooMany));
        Assert.Contains("200", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comma_separated_argument_splits_and_trims()
    {
        Assert.Equal(["System.State", "System.Title"], AdoTools.Split("System.State, System.Title"));
        Assert.Empty(AdoTools.Split(null));
        Assert.Empty(AdoTools.Split(""));
    }
}

public class RelationMappingTests
{
    private static Dictionary<string, JsonElement> Attributes(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""{ "name": "{{name}}" }""")!;

    [Fact]
    public void A_work_item_link_is_reduced_to_the_id_and_drops_the_api_url()
    {
        var relations = Mapping.Relations([
            new WireRelation("System.LinkTypes.Hierarchy-Reverse",
                "https://dev.azure.com/contoso/_apis/wit/workItems/1234", Attributes("Parent")),
        ]);

        var dto = Assert.Single(relations!);
        Assert.Equal("Hierarchy-Reverse", dto.Type);
        Assert.Equal("Parent", dto.Name);
        Assert.Equal(1234, dto.WorkItemId);
        Assert.Null(dto.Url); // the id says everything the link says
    }

    [Fact]
    public void A_link_to_something_that_is_not_a_work_item_keeps_its_url()
    {
        var relations = Mapping.Relations([
            new WireRelation("ArtifactLink", "vstfs:///Git/Commit/p%2Fr%2Fabc123", Attributes("Fixed in Commit")),
        ]);

        var dto = Assert.Single(relations!);
        Assert.Null(dto.WorkItemId);
        Assert.Equal("vstfs:///Git/Commit/p%2Fr%2Fabc123", dto.Url);
    }

    [Fact]
    public void No_relations_means_no_field()
    {
        Assert.Null(Mapping.Relations(null));
        Assert.Null(Mapping.Relations([]));
    }
}

public class PipelineMappingTests
{
    [Fact]
    public void The_root_folder_is_not_worth_reporting()
    {
        Assert.Null(Mapping.Pipeline(new WirePipeline(1, "ci", "\\", null)).Folder);
        Assert.Equal("\\nightly", Mapping.Pipeline(new WirePipeline(1, "ci", "\\nightly", null)).Folder);
    }

    [Fact]
    public void A_finished_run_reports_its_result_and_not_its_status()
    {
        var build = new WireBuild(
            77, "20260701.3", "completed", "failed", DateTimeOffset.UnixEpoch, null, null,
            "refs/heads/main", new WireBuildDefinition(1, "ci"), new WireIdentity("Mike", null, null),
            new WireProjectRef("p1", "Core"));

        var dto = Mapping.Run(build);

        Assert.Null(dto.State);
        Assert.Equal("failed", dto.Result);
        Assert.Equal("main", dto.Branch);
        Assert.Equal("Mike", dto.RequestedFor);
    }

    [Fact]
    public void A_run_says_what_it_was_built_from()
    {
        // The standing question in a TFVC organization is which changeset is deployed, and the
        // answer was already on the wire. It just never reached the result.
        var tfvc = Mapping.Run(Built("34521"));
        Assert.Equal("34521", tfvc.SourceVersion);
        Assert.Equal(34521, tfvc.Changeset);

        // In git the same field is a commit, and a commit is not a changeset number.
        var git = Mapping.Run(Built("9f1c2ab7d3e4f5061728394a5b6c7d8e9f012345"));
        Assert.Equal("9f1c2ab7d3e4f5061728394a5b6c7d8e9f012345", git.SourceVersion);
        Assert.Null(git.Changeset);

        var unknown = Mapping.Run(Built(null));
        Assert.Null(unknown.SourceVersion);
        Assert.Null(unknown.Changeset);
    }

    [Theory]
    [InlineData("34521", 34521)]
    [InlineData("C34521", null)]
    [InlineData("9f1c2ab", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_version_that_is_a_number_is_a_changeset(string? version, int? expected)
    {
        Assert.Equal(expected, Mapping.Changeset(version));
    }

    private static WireBuild Built(string? sourceVersion) => new(
        77, "20260701.3", "completed", "succeeded", DateTimeOffset.UnixEpoch, null, null,
        "refs/heads/main", new WireBuildDefinition(1, "ci"), new WireIdentity("Mike", null, null),
        new WireProjectRef("p1", "Core"), sourceVersion);

    [Fact]
    public void A_running_build_reports_its_status_because_there_is_no_result_yet()
    {
        var build = new WireBuild(77, "x", "inProgress", null, null, null, null, null, null, null, null);

        var dto = Mapping.Run(build);

        Assert.Equal("inProgress", dto.State);
        Assert.Null(dto.Result);
    }
}

public class TimelineMappingTests
{
    private static WireTimelineRecord Record(
        string id, string? parent, string type, string name, string? result, int order,
        List<WireIssue>? issues = null, WireLogRef? log = null) =>
        new(id, parent, type, name, "completed", result, null, null, null, null, issues, log, order);

    private static WireTimeline Pipeline() => new([
        Record("s1", null, "Stage", "Build", "failed", 1),
        Record("j1", "s1", "Job", "Compile", "failed", 2),
        Record("t1", "j1", "Task", "Restore", "succeeded", 3),
        Record("t2", "j1", "Task", "dotnet build", "failed", 4,
            [new WireIssue("error", null, "CS0246: type not found"), new WireIssue("warning", null, "noisy")]),
        Record("s2", null, "Stage", "Deploy", "skipped", 5),
    ]);

    [Fact]
    public void Only_failed_tasks_are_reported_and_they_name_their_stage_and_job()
    {
        var counts = new SkipCounter();

        var failed = Mapping.FailedSteps(Pipeline(), 5, counts);

        var step = Assert.Single(failed).Step;
        Assert.Equal("Build", step.Stage);
        Assert.Equal("Compile", step.Job);
        Assert.Equal("dotnet build", step.Task);
        Assert.Equal(["CS0246: type not found"], step.Errors); // warnings did not fail the step
    }

    [Fact]
    public void Two_failed_tasks_with_the_same_name_each_keep_their_own_log()
    {
        // The same step name in two jobs is routine in matrix builds. Pairing logs by name would
        // either crash or attach one job's log to the other's failure.
        var timeline = new WireTimeline([
            Record("j1", null, "Job", "Linux", "failed", 1),
            Record("j2", null, "Job", "Windows", "failed", 2),
            Record("t1", "j1", "Task", "Run tests", "failed", 3, log: new WireLogRef(1, "https://logs/1")),
            Record("t2", "j2", "Task", "Run tests", "failed", 4, log: new WireLogRef(2, "https://logs/2")),
        ]);

        var failed = Mapping.FailedSteps(timeline, 5, new SkipCounter());

        Assert.Equal(["Linux", "Windows"], failed.Select(f => f.Step.Job));
        Assert.Equal(["https://logs/1", "https://logs/2"], failed.Select(f => f.LogUrl));
    }

    [Fact]
    public void The_stage_and_job_roll_ups_are_not_reported_as_separate_failures()
    {
        // Otherwise one broken task is reported three times: as a task, as its job, as its stage.
        Assert.Single(Mapping.FailedSteps(Pipeline(), 5, new SkipCounter()));
    }

    [Fact]
    public void Records_that_passed_are_counted_rather_than_listed()
    {
        var counts = new SkipCounter();

        Mapping.FailedSteps(Pipeline(), 5, counts);

        Assert.Equal(1, counts.ToDto()!.Succeeded); // Restore. The skipped Deploy stage did not pass.
        Assert.Null(counts.ToDto()!.Deleted);
    }

    [Fact]
    public void A_skipped_stage_is_reported_as_neither_failed_nor_passed()
    {
        // Counting it as passing would say the pipeline did work it never did.
        Assert.False(Mapping.IsRunTaskFailure("skipped"));
        Assert.False(Mapping.IsRunTaskSuccess("skipped"));
        Assert.False(Mapping.IsRunTaskSuccess(null));
        Assert.True(Mapping.IsRunTaskSuccess("succeededWithIssues"));
    }

    [Fact]
    public void The_error_list_is_bounded()
    {
        var many = new WireTimeline([
            Record("t1", null, "Task", "build", "failed", 1,
                Enumerable.Range(0, 20).Select(i => new WireIssue("error", null, $"e{i}")).ToList()),
        ]);

        Assert.Equal(3, Mapping.FailedSteps(many, 3, new SkipCounter())[0].Step.Errors!.Count);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("abandoned")]
    public void Cancelled_and_abandoned_count_as_failure(string result)
    {
        Assert.True(Mapping.IsRunTaskFailure(result));
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("succeededWithIssues")]
    [InlineData("skipped")]
    [InlineData(null)]
    public void Everything_else_does_not(string? result)
    {
        Assert.False(Mapping.IsRunTaskFailure(result));
    }

    [Fact]
    public void An_empty_timeline_produces_no_failures_and_no_exception()
    {
        Assert.Empty(Mapping.FailedSteps(new WireTimeline(null), 5, new SkipCounter()));
    }

    [Fact]
    public void A_record_whose_parent_is_missing_still_maps()
    {
        var orphan = new WireTimeline([Record("t1", "gone", "Task", "build", "failed", 1)]);

        var step = Assert.Single(Mapping.FailedSteps(orphan, 5, new SkipCounter())).Step;
        Assert.Null(step.Stage);
        Assert.Null(step.Job);
    }
}

public class LogTailTests
{
    [Fact]
    public void The_end_of_the_log_is_kept_because_that_is_where_the_error_is()
    {
        var log = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var (tail, truncated) = Mapping.LogTail(log, 3);

        Assert.Equal("line 98\nline 99\nline 100", tail);
        Assert.True(truncated);
    }

    [Fact]
    public void A_short_log_is_returned_whole_and_unflagged()
    {
        var (tail, truncated) = Mapping.LogTail("only\nline", 40);

        Assert.Equal("only\nline", tail);
        Assert.Null(truncated);
    }

    [Fact]
    public void Windows_line_endings_do_not_produce_stray_carriage_returns()
    {
        var (tail, _) = Mapping.LogTail("a\r\nb\r\n", 40);

        Assert.Equal("a\nb", tail);
    }

    [Fact]
    public void Asking_for_no_lines_or_having_no_log_yields_nothing()
    {
        Assert.Equal((null, null), Mapping.LogTail("something", 0));
        Assert.Equal((null, null), Mapping.LogTail("   ", 40));
    }
}

public class FieldHelperTests
{
    private static Dictionary<string, JsonElement> Fields(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void A_field_of_the_wrong_shape_reads_as_absent_rather_than_throwing()
    {
        var fields = Fields("""{ "System.Title": 42, "Microsoft.VSTS.Common.Priority": "high" }""");

        Assert.Null(Mapping.Str(fields, "System.Title"));
        Assert.Null(Mapping.Int(fields, "Microsoft.VSTS.Common.Priority"));
    }

    [Fact]
    public void An_identity_field_reads_whether_it_is_an_object_or_already_a_string()
    {
        Assert.Equal("Mike", Mapping.Person(Fields("""{ "a": { "displayName": "Mike" } }"""), "a"));
        Assert.Equal("Mike", Mapping.Person(Fields("""{ "a": "Mike" }"""), "a"));
        Assert.Null(Mapping.Person(Fields("""{ "a": 1 }"""), "a"));
        Assert.Null(Mapping.Person(null, "a"));
    }

    [Fact]
    public void An_unparseable_date_reads_as_absent()
    {
        Assert.Null(Mapping.Date(Fields("""{ "d": "not a date" }"""), "d"));
    }

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/heads/feature/a/b", "feature/a/b")]
    [InlineData("refs/pull/12/merge", "refs/pull/12/merge")]
    [InlineData(null, null)]
    public void Branch_names_lose_only_the_heads_prefix(string? input, string? expected)
    {
        Assert.Equal(expected, Mapping.ShortBranch(input));
    }

    [Theory]
    [InlineData("System.LinkTypes.Hierarchy-Forward", "Hierarchy-Forward")]
    [InlineData("Microsoft.VSTS.Common.TestedBy-Forward", "TestedBy-Forward")]
    [InlineData("ArtifactLink", "ArtifactLink")]
    public void Relation_names_lose_their_namespace(string input, string expected)
    {
        Assert.Equal(expected, Mapping.ShortRelation(input));
    }

    [Theory]
    [InlineData("https://dev.azure.com/x/_apis/wit/workItems/99", 99)]
    [InlineData("https://dev.azure.com/x/_apis/wit/workitems/99", 99)]
    [InlineData("https://dev.azure.com/x/_apis/git/repositories/abc", null)]
    [InlineData("vstfs:///Git/PullRequestId/a%2Fb%2F3", null)]
    [InlineData(null, null)]
    public void Only_work_item_urls_yield_a_work_item_id(string? url, int? expected)
    {
        Assert.Equal(expected, Mapping.LinkedWorkItemId(url));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("Completed", true)]
    [InlineData(null, true)]
    // `cancelling` reads like an ending, but the run becomes `completed` once the cancellation
    // lands. A waiter that stopped here would report a run that had not stopped.
    [InlineData("cancelling", false)]
    [InlineData("inProgress", false)]
    [InlineData("notStarted", false)]
    [InlineData("postponed", false)]
    public void Only_completed_ends_a_run(string? status, bool expected)
    {
        Assert.Equal(expected, Mapping.IsTerminalRunStatus(status));
    }

    [Theory]
    [InlineData("active", false)]
    [InlineData("Active", false)]
    [InlineData("completed", true)]
    [InlineData("abandoned", true)]
    // Unknown and absent are terminal: better to return what the service said than to poll a
    // state this server does not understand until the timeout.
    [InlineData("notSet", true)]
    [InlineData(null, true)]
    public void Only_active_keeps_a_pull_request_waiter_polling(string? status, bool expected)
    {
        Assert.Equal(expected, Mapping.IsTerminalPullRequestStatus(status));
    }
}
