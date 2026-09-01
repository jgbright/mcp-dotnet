using System.Text.Json;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The Search service answers on its own host and takes POST bodies with literal `$top`/`$skip`
/// property names and case-sensitive filter keys. All of that only fails at the wire, so the
/// request construction is pinned here.
/// </summary>
public class SearchRequestTests
{
    [Fact]
    public void The_modern_host_maps_to_almsearch()
    {
        Assert.Equal(
            "https://almsearch.dev.azure.com/contoso",
            Search.BaseUrl("https://dev.azure.com/contoso"));
    }

    [Fact]
    public void The_legacy_host_maps_to_its_almsearch_subdomain()
    {
        Assert.Equal(
            "https://contoso.almsearch.visualstudio.com",
            Search.BaseUrl("https://contoso.visualstudio.com"));
    }

    [Fact]
    public void An_unrecognized_host_passes_through_untouched()
    {
        Assert.Equal("https://tfs.contoso.example/tfs", Search.BaseUrl("https://tfs.contoso.example/tfs"));
    }

    [Fact]
    public void Filters_without_a_value_are_left_out_and_none_at_all_is_null()
    {
        Assert.Null(Search.BuildRequest("q", 10).Filters);
        Assert.Null(Search.BuildRequest("q", 10, ("Repository", null), ("Path", "")).Filters);

        var filters = Search.BuildRequest(
            "q", 10, ("Repository", "WebApp"), ("Path", null), ("Branch", "main")).Filters;
        Assert.NotNull(filters);
        Assert.Equal(["WebApp"], filters["Repository"]);
        Assert.Equal(["main"], filters["Branch"]);
        Assert.False(filters.ContainsKey("Path"));
    }

    [Fact]
    public void The_wire_shape_keeps_the_dollar_names_and_the_filter_key_casing()
    {
        var json = JsonSerializer.Serialize(
            Search.BuildRequest("retry AND ext:cs", 25, ("Repository", "WebApp")), AdoClient.Json);

        Assert.Contains("\"searchText\":\"retry AND ext:cs\"", json);
        Assert.Contains("\"$top\":25", json);
        Assert.Contains("\"$skip\":0", json);
        Assert.Contains("\"Repository\":[\"WebApp\"]", json);
        Assert.Contains("\"includeFacets\":false", json);
        // includeSnippet is a code-search-only property and must stay off the wire unless set.
        Assert.DoesNotContain("includeSnippet", json);
    }

    /// <summary>
    /// The service refuses a Path filter without a Repository filter. For TFVC the path already
    /// names its repository, so the tool derives it instead of making the caller repeat it.
    /// </summary>
    [Theory]
    [InlineData("$/Core/Schema/Tables", "$/Core")]
    [InlineData("$/Core", "$/Core")]
    [InlineData("/src/web", null)]
    [InlineData("$/", null)]
    public void A_tfvc_path_names_its_own_repository(string path, string? expected)
    {
        Assert.Equal(expected, Search.TfvcRepository(path));
    }

    [Fact]
    public void Code_search_can_opt_in_to_snippets()
    {
        var json = JsonSerializer.Serialize(
            Search.BuildRequest("q", 5) with { IncludeSnippet = true }, AdoClient.Json);

        Assert.Contains("\"includeSnippet\":true", json);
    }
}

public class HighlightTests
{
    [Fact]
    public void Highlight_markers_are_stripped_and_entities_decoded()
    {
        Assert.Equal(
            "public List<int> Retry(int count)",
            Text.FromHighlight("public List&lt;int&gt; <highlighthit>Retry</highlighthit>(int count)"));
    }

    [Fact]
    public void An_encoded_angle_bracket_survives_as_text_rather_than_becoming_markup()
    {
        Assert.Equal("uses <highlighthit> literally", Text.FromHighlight("uses &lt;highlighthit&gt; literally"));
    }

    [Fact]
    public void Blank_in_means_null_out()
    {
        Assert.Null(Text.FromHighlight(null));
        Assert.Null(Text.FromHighlight("   "));
        Assert.Null(Text.FromHighlight("<highlighthit></highlighthit>"));
    }
}

public class SearchSnippetTests
{
    private static WireSearchHit Hit(string field, params string[] highlights) =>
        new(field, [.. highlights]);

    [Fact]
    public void Highlights_are_deduplicated_and_joined()
    {
        var (snippet, truncated) = Mapping.Snippet(
            [
                Hit("system.description", "the <highlighthit>retry</highlighthit> loop", "the <highlighthit>retry</highlighthit> loop"),
                Hit("system.history", "second <highlighthit>retry</highlighthit> note"),
            ],
            bodyLimit: 0);

        Assert.Equal("the retry loop … second retry note", snippet);
        Assert.Null(truncated);
    }

    [Fact]
    public void Excluded_fields_do_not_repeat_what_the_result_already_carries()
    {
        var (snippet, _) = Mapping.Snippet(
            [Hit("system.title", "the <highlighthit>title</highlighthit>"), Hit("system.description", "body")],
            bodyLimit: 0, "system.title");

        Assert.Equal("body", snippet);
    }

    [Fact]
    public void Exclusion_covers_the_pattern_variants_the_service_reports_for_the_same_field()
    {
        // The wire carries "fileNames" and "fileNames.pattern" with the same text. Excluding the
        // field silences both, and leaves an unrelated field that shares the prefix alone.
        var (snippet, _) = Mapping.Snippet(
            [
                Hit("fileNames", "Deploys"),
                Hit("fileNames.pattern", "Deploys"),
                Hit("fileNamesExtra", "kept"),
            ],
            bodyLimit: 0, "fileNames");

        Assert.Equal("kept", snippet);
    }

    [Fact]
    public void No_surviving_highlights_means_no_snippet_at_all()
    {
        Assert.Equal((null, null), Mapping.Snippet(null, 0));
        Assert.Equal((null, null), Mapping.Snippet([Hit("system.title", "t")], 0, "system.title"));
    }

    [Fact]
    public void The_snippet_is_truncated_at_the_body_limit_like_any_other_body()
    {
        var (snippet, truncated) = Mapping.Snippet([Hit("f", new string('x', 50))], bodyLimit: 10);

        Assert.Equal(10, snippet!.Length);
        Assert.True(truncated);
    }
}

public class CodeSearchMappingTests
{
    private static WireCodeResult Result(
        string path = "/src/Retry.cs",
        string? repo = "WebApp",
        string? branch = "main",
        params string?[] snippets) => new(
        "Retry.cs",
        path,
        new WireCodeMatches(
            [.. snippets.Select(s => new WireCodeHit(1, 5, 10, 3, s))],
            null),
        new WireProjectRef("pid", "Core"),
        new WireSearchRepository(repo, "rid", "git"),
        branch is null ? [] : [new WireSearchVersion(branch, "abc123")]);

    [Fact]
    public void A_git_result_carries_branch_match_count_and_a_browser_link()
    {
        var dto = Mapping.CodeSearchHit(
            Result(snippets: ["var <highlighthit>retry</highlighthit> = 1;"]),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Equal("/src/Retry.cs", dto.Path);
        Assert.Equal("WebApp", dto.Repo);
        Assert.Equal("main", dto.Branch);
        Assert.Equal(1, dto.Matches);
        Assert.Equal("var retry = 1;", dto.Snippet);
        Assert.Equal(
            "https://dev.azure.com/contoso/Core/_git/WebApp?path=%2Fsrc%2FRetry.cs", dto.WebUrl);
    }

    [Fact]
    public void A_tfvc_result_browses_under_version_control()
    {
        var dto = Mapping.CodeSearchHit(
            Result(path: "$/Core/Websites/web.config", branch: null),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Null(dto.Branch);
        Assert.Equal(
            "https://dev.azure.com/contoso/Core/_versionControl?path=%24%2FCore%2FWebsites%2Fweb.config",
            dto.WebUrl);
    }

    [Fact]
    public void Snippets_are_capped_but_the_match_count_still_says_how_many_places_matched()
    {
        var dto = Mapping.CodeSearchHit(
            Result(snippets: ["one", "two", "three", "four", "five"]),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Equal(5, dto.Matches);
        Assert.Equal("one\n…\ntwo\n…\nthree", dto.Snippet);
    }

    [Fact]
    public void A_filename_only_match_reports_no_match_count_rather_than_zero()
    {
        var dto = Mapping.CodeSearchHit(
            new WireCodeResult("Retry.cs", "/src/Retry.cs", new WireCodeMatches(null, [new WireCodeHit(0, 5, null, null, null)]),
                null, new WireSearchRepository("WebApp", "rid", "git"), null),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Null(dto.Matches);
        Assert.Null(dto.Snippet);
    }
}

public class WorkItemSearchMappingTests
{
    private static WireWorkItemSearchResult Result(Dictionary<string, string?> fields, params WireSearchHit[] hits) =>
        new(new WireProjectRef("pid", "Core"), fields, [.. hits]);

    [Fact]
    public void Fields_map_into_the_usual_work_item_shape()
    {
        var dto = Mapping.WorkItemSearchHit(
            Result(
                new Dictionary<string, string?>
                {
                    ["system.id"] = "4711",
                    ["system.workitemtype"] = "Bug",
                    ["system.title"] = "Retry loop never stops",
                    ["system.state"] = "Active",
                    ["system.assignedto"] = "Mike Rivera <mike@contoso.example>",
                    ["system.changeddate"] = "2026-07-01T12:00:00Z",
                    ["system.tags"] = "retry; hotfix",
                },
                new WireSearchHit("system.description", ["the <highlighthit>retry</highlighthit> loop"])),
            bodyLimit: 0);

        Assert.Equal(4711, dto.Id);
        Assert.Equal("Bug", dto.Type);
        Assert.Equal("Retry loop never stops", dto.Title);
        Assert.Equal("Active", dto.State);
        Assert.Equal("Mike Rivera", dto.AssignedTo);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), dto.Changed);
        Assert.Equal(["retry", "hotfix"], dto.Tags);
        Assert.Equal("the retry loop", dto.Snippet);
    }

    [Fact]
    public void A_title_highlight_is_not_repeated_as_the_snippet()
    {
        var dto = Mapping.WorkItemSearchHit(
            Result(
                new Dictionary<string, string?> { ["system.id"] = "1", ["system.title"] = "Retry" },
                new WireSearchHit("system.title", ["<highlighthit>Retry</highlighthit>"])),
            bodyLimit: 0);

        Assert.Null(dto.Snippet);
    }

    [Fact]
    public void Field_lookup_does_not_depend_on_the_reference_name_casing()
    {
        Assert.Equal("Bug", Mapping.SearchField(
            new Dictionary<string, string?> { ["System.WorkItemType"] = "Bug" }, "system.workitemtype"));
    }

    [Theory]
    [InlineData("Mike Rivera <mike@contoso.example>", "Mike Rivera")]
    [InlineData("Mike Rivera", "Mike Rivera")]
    [InlineData("<mike@contoso.example>", "<mike@contoso.example>")]
    [InlineData(null, null)]
    public void The_identity_keeps_its_display_name(string? wire, string? expected)
    {
        Assert.Equal(expected, Mapping.PersonName(wire));
    }
}

public class WikiSearchMappingTests
{
    [Fact]
    public void A_page_carries_its_wiki_snippet_and_a_browser_link()
    {
        var dto = Mapping.WikiSearchHit(
            new WireWikiResult(
                "Deploys.md", "/Operations/Deploys", new WireProjectRef("pid", "Core"),
                new WireWikiRef("wid", "Core.wiki", "/", "main"),
                [new WireSearchHit("content", ["run the <highlighthit>deploy</highlighthit> script"])]),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Equal("/Operations/Deploys", dto.Path);
        Assert.Equal("Core.wiki", dto.Wiki);
        Assert.Equal("run the deploy script", dto.Snippet);
        Assert.Equal(
            "https://dev.azure.com/contoso/Core/_wiki/wikis/Core.wiki?pagePath=%2FOperations%2FDeploys",
            dto.WebUrl);
    }

    [Fact]
    public void A_filename_highlight_is_not_repeated_as_the_snippet()
    {
        var dto = Mapping.WikiSearchHit(
            new WireWikiResult(
                "Deploys.md", "/Deploys", null, new WireWikiRef("wid", "Core.wiki", "/", "main"),
                [new WireSearchHit("fileNames", ["<highlighthit>Deploys</highlighthit>.md"])]),
            bodyLimit: 0, "https://dev.azure.com/contoso", "Core");

        Assert.Null(dto.Snippet);
    }
}
