using System.Text.Json;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// get_work_item_history turns a work item's raw updates into a few lines per change. The payload
/// is trimmed from a real updates response, with placeholder people and paths.
/// </summary>
public class WorkItemHistoryTests
{
    // Oldest first, as the endpoint answers. Note revisedDate: it is when the next update replaced
    // this one, and 9999 on the latest; the update's own time is System.ChangedDate's new value.
    private const string Updates = """
    { "count": 6, "value": [
      { "rev": 1, "revisedBy": { "id": "a1", "displayName": "Alice", "uniqueName": "alice@contoso.com" },
        "revisedDate": "2026-09-23T12:48:33Z",
        "fields": {
          "System.Id": { "newValue": 100 },
          "System.Rev": { "newValue": 1 },
          "System.ChangedDate": { "newValue": "2026-09-22T20:49:22Z" },
          "System.Watermark": { "newValue": 65000 },
          "System.State": { "newValue": "New" },
          "System.Tags": { "newValue": "" },
          "System.AssignedTo": { "newValue": { "displayName": "Bob", "uniqueName": "bob@contoso.com" } },
          "System.Description": { "newValue": "<div>First version of a long description.</div>" }
        } },
      { "rev": 2, "revisedBy": { "id": "a1", "displayName": "Alice" },
        "revisedDate": "2026-09-28T13:33:59Z",
        "fields": {
          "System.Rev": { "oldValue": 1, "newValue": 2 },
          "System.ChangedDate": { "oldValue": "2026-09-22T20:49:22Z", "newValue": "2026-09-23T12:48:33Z" },
          "System.AssignedTo": {
            "oldValue": { "displayName": "Bob", "uniqueName": "bob@contoso.com" },
            "newValue": { "displayName": "Carol", "uniqueName": "carol@contoso.com" } }
        } },
      { "rev": 3, "revisedBy": { "id": "c3", "displayName": "Carol", "uniqueName": "carol@contoso.com" },
        "revisedDate": "2026-09-28T14:59:48Z",
        "fields": {
          "System.Rev": { "oldValue": 2, "newValue": 3 },
          "System.ChangedDate": { "oldValue": "2026-09-23T12:48:33Z", "newValue": "2026-09-28T13:33:59Z" },
          "System.State": { "oldValue": "New", "newValue": "Active" },
          "Microsoft.VSTS.Common.StateChangeDate": { "newValue": "2026-09-28T13:33:59Z" },
          "System.Description": {
            "oldValue": "<div>First version of a long description.</div>",
            "newValue": "<div>Second version of a <b>long</b> description that keeps going and going.</div>" }
        } },
      { "rev": 4, "revisedBy": { "id": "c3", "displayName": "Carol" },
        "revisedDate": "2026-09-28T15:18:26Z",
        "fields": {
          "System.Rev": { "oldValue": 3, "newValue": 4 },
          "System.ChangedDate": { "oldValue": "2026-09-28T13:33:59Z", "newValue": "2026-09-28T14:59:48Z" },
          "System.CommentCount": { "oldValue": 0, "newValue": 1 },
          "System.History": { "newValue": "<div>Associated with changeset 501: <i>fix the header</i></div>" }
        },
        "relations": { "added": [
          { "rel": "ArtifactLink", "url": "vstfs:///VersionControl/Changeset/501", "attributes": { "name": "Fixed in Changeset" } },
          { "rel": "ArtifactLink", "url": "vstfs:///Build/Build/1800", "attributes": { "name": "Integrated in build" } },
          { "rel": "ArtifactLink", "url": "vstfs:///Git/Commit/p1%2Fr1%2Fabc123def", "attributes": { "name": "Fixed in Commit" } },
          { "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/90", "attributes": { "name": "Parent" } },
          { "rel": "ArtifactLink", "url": "vstfs:///ReleaseManagement/Environment/p1:44:105", "attributes": { "name": "Integrated in release environment" } }
        ] } },
      { "rev": 5, "revisedBy": { "id": "c3", "displayName": "Carol" },
        "revisedDate": "2026-09-29T13:40:08Z",
        "fields": {
          "System.Rev": { "oldValue": 4, "newValue": 5 },
          "System.ChangedDate": { "oldValue": "2026-09-28T14:59:48Z", "newValue": "2026-09-28T15:18:26Z" },
          "System.Watermark": { "oldValue": 65100, "newValue": 65101 }
        } },
      { "rev": 6, "revisedBy": { "id": "a1", "displayName": "Alice" },
        "revisedDate": "9999-01-01T00:00:00Z",
        "fields": {
          "System.Rev": { "oldValue": 5, "newValue": 6 },
          "System.ChangedDate": { "oldValue": "2026-09-28T15:18:26Z", "newValue": "2026-09-29T13:40:08Z" },
          "System.State": { "oldValue": "Active", "newValue": "Closed" }
        },
        "relations": { "removed": [
          { "rel": "System.LinkTypes.Related", "url": "https://dev.azure.com/contoso/_apis/wit/workItems/77", "attributes": { "name": "Related" } }
        ] } }
    ] }
    """;

    private static List<WireWorkItemUpdate> Parsed() =>
        JsonSerializer.Deserialize<ListResponse<WireWorkItemUpdate>>(Updates, AdoClient.Json)!.Value!;

    private static (List<WorkItemChangeDto> Changes, bool HasMore, SkippedDto? Skipped) History(
        DateTimeOffset? since = null, Func<WireIdentity?, bool>? by = null, string[]? fields = null,
        int bodyLimit = 300, int limit = 50)
    {
        var counts = new SkipCounter();
        var (changes, hasMore) = Mapping.WorkItemHistory(Parsed(), since, by, fields, bodyLimit, limit, counts);
        return (changes, hasMore, counts.ToDto());
    }

    [Fact]
    public void Changes_come_newest_first_dated_by_when_they_were_made()
    {
        var (changes, _, _) = History();

        Assert.Equal([6, 4, 3, 2, 1], changes.Select(c => c.Rev)); // rev 5 held only bookkeeping
        // Not revisedDate, which is the next update's time and 9999 on the latest.
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T13:40:08Z"), changes[0].Date);
        Assert.Equal(DateTimeOffset.Parse("2026-09-22T20:49:22Z"), changes[^1].Date);
    }

    [Fact]
    public void Bookkeeping_is_dropped_and_counted_and_an_update_of_nothing_else_goes_with_it()
    {
        var (changes, _, skipped) = History();

        Assert.DoesNotContain(changes, c => c.Fields?.Keys.Any(k => k is "System.Rev" or "System.ChangedDate" or "System.Watermark" or "System.Id") == true);
        Assert.Equal(1, skipped!.Updates);
        Assert.True(skipped.Fields > 10);
    }

    [Fact]
    public void A_field_change_reads_as_from_and_to_with_people_by_name()
    {
        var reassigned = History().Changes.Single(c => c.Rev == 2);

        Assert.Equal("Alice", reassigned.By);
        Assert.Equal(new FieldChangeDto("Bob", "Carol", null), reassigned.Fields!["System.AssignedTo"]);
    }

    [Fact]
    public void An_empty_value_set_to_an_empty_value_is_not_a_change()
    {
        Assert.DoesNotContain("System.Tags", History().Changes.Single(c => c.Rev == 1).Fields!.Keys);
    }

    [Fact]
    public void A_rich_text_field_reports_only_its_new_value_as_plain_text_cut_short()
    {
        var edit = History(bodyLimit: 20).Changes.Single(c => c.Rev == 3).Fields!["System.Description"];

        Assert.Null(edit.From);
        Assert.DoesNotContain("<", edit.To);
        Assert.StartsWith("Second version of a", edit.To);
        Assert.True(edit.Truncated);
    }

    [Fact]
    public void The_discussion_entry_is_a_comment_in_plain_text()
    {
        var change = History().Changes.Single(c => c.Rev == 4);

        Assert.Equal("Associated with changeset 501: fix the header", change.Comment);
        Assert.Null(change.Fields); // CommentCount is bookkeeping
    }

    [Fact]
    public void Links_name_the_changeset_build_commit_or_work_item_and_fall_back_to_the_url()
    {
        var change = History().Changes.Single(c => c.Rev == 4);

        Assert.Equal(
        [
            new LinkChangeDto("Fixed in Changeset", null, 501, null, null, null),
            new LinkChangeDto("Integrated in build", null, null, 1800, null, null),
            new LinkChangeDto("Fixed in Commit", null, null, null, "abc123def", null),
            new LinkChangeDto("Parent", 90, null, null, null, null),
            new LinkChangeDto("Integrated in release environment", null, null, null, null, "vstfs:///ReleaseManagement/Environment/p1:44:105"),
        ], change.Linked);
        Assert.Equal(77, Assert.Single(History().Changes.Single(c => c.Rev == 6).Unlinked!).WorkItemId);
    }

    [Fact]
    public void Since_and_by_narrow_the_window_without_counting_what_they_leave_out()
    {
        var since = History(since: DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var byCarol = History(by: who => who?.DisplayName == "Carol");

        Assert.Equal([6, 4, 3], since.Changes.Select(c => c.Rev));
        Assert.Equal([4, 3], byCarol.Changes.Select(c => c.Rev));
        Assert.Null(since.Skipped!.Deleted);
    }

    [Fact]
    public void Fields_keeps_only_the_named_ones_but_never_the_comments_or_links()
    {
        var (changes, _, _) = History(fields: ["System.State"]);

        Assert.All(changes.Where(c => c.Fields is not null), c => Assert.Equal(["System.State"], c.Fields!.Keys));
        Assert.Contains(changes, c => c.Comment is not null);
        Assert.DoesNotContain(changes, c => c.Rev == 2); // only a reassignment, which was not asked for
    }

    [Fact]
    public void The_limit_cuts_the_newest_first_list_and_says_so()
    {
        var (changes, hasMore, _) = History(limit: 2);

        Assert.Equal([6, 4], changes.Select(c => c.Rev));
        Assert.True(hasMore);
    }
}
