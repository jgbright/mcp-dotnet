namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Work item references in written HTML. A REST write never gets a relation from Azure DevOps for
/// its mentions, so what these helpers find is exactly what gets linked.
/// </summary>
public class MentionsTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private static readonly Dictionary<int, string> Targets = new()
    {
        [5101] = $"{Org}/Project/_workitems/edit/5101",
        [5102] = $"{Org}/Project/_workitems/edit/5102",
    };

    private static string Mention(int id) =>
        $"<a href=\"{Org}/Project/_workitems/edit/{id}/\" data-vss-mention=\"version:1.0\">#{id}</a>";

    [Theory]
    [InlineData("see #5101", 5101)]
    [InlineData("see AB#5101.", 5101)]
    [InlineData("<p>#5101</p>", 5101)]
    [InlineData("(#5101)", 5101)]
    [InlineData("&nbsp;#5101", 5101)]
    public void A_bare_reference_is_found(string html, int id) =>
        Assert.Equal([id], Mentions.Find(html));

    [Theory]
    [InlineData("step #1 of 3")]
    [InlineData("#12 is next")]
    [InlineData("#0123")]
    [InlineData("it&#39;s done")]
    [InlineData("&#5101;")]
    [InlineData("AB#draft-slug")]
    [InlineData("x#5101")]
    [InlineData("#8012abc")]
    [InlineData("<span style=\"color:#123456\">red</span>")]
    [InlineData("<div data-x=\"#5101\">text</div>")]
    public void Nothing_else_is_a_reference(string html) =>
        Assert.Empty(Mentions.Find(html));

    [Fact]
    public void An_anchor_to_a_work_item_is_a_reference_whatever_its_text()
    {
        var html = $"<a href=\"{Org}/Project/_workitems/edit/5101\">AB#5101</a> and " +
                   $"<a href=\"{Org}/Project/_workitems/edit/5102/\">the parent story</a>";

        Assert.Equal([5101, 5102], Mentions.Find(html));
    }

    [Fact]
    public void A_reference_inside_a_link_elsewhere_is_not_a_work_item()
    {
        // A pull request or GitHub issue number reads the same as a work item id.
        Assert.Empty(Mentions.Find("<a href=\"https://github.com/o/r/pull/5101\">#5101</a>"));
    }

    [Fact]
    public void Ids_come_back_once_in_order_of_first_appearance_across_bodies()
    {
        Assert.Equal([5102, 5101], Mentions.Find("#5102 then #5101", null, "#5101 again", "AB#5102"));
    }

    [Fact]
    public void A_bare_reference_becomes_the_web_editors_mention()
    {
        Assert.Equal($"<p>see {Mention(5101)}.</p>", Mentions.Rewrite("<p>see AB#5101.</p>", Targets));
    }

    [Fact]
    public void Our_old_anchor_becomes_the_web_editors_mention()
    {
        var html = $"<div>blocked by <a href=\"{Org}/Project/_workitems/edit/5101\">AB#5101</a></div>";

        Assert.Equal($"<div>blocked by {Mention(5101)}</div>", Mentions.Rewrite(html, Targets));
    }

    [Fact]
    public void A_work_item_link_with_text_of_its_own_keeps_it()
    {
        var html = $"<a href=\"{Org}/Project/_workitems/edit/5101\">the design story</a>";

        Assert.Equal(html, Mentions.Rewrite(html, Targets));
    }

    [Fact]
    public void An_id_without_a_target_is_left_as_written()
    {
        // No target means the id does not exist, or is the item itself.
        Assert.Equal("#404 and AB#5106", Mentions.Rewrite("#404 and AB#5106", Targets));
    }

    [Fact]
    public void Rewriting_twice_changes_nothing_the_second_time()
    {
        var once = Mentions.Rewrite("#5101 and AB#5102", Targets);

        Assert.Equal(once, Mentions.Rewrite(once, Targets));
        Assert.Equal([5101, 5102], Mentions.Find(once));
    }

    [Fact]
    public void Tags_and_attributes_survive_a_rewrite()
    {
        var html = "<span style=\"color:#123456\">#5101</span>";

        Assert.Equal($"<span style=\"color:#123456\">{Mention(5101)}</span>", Mentions.Rewrite(html, Targets));
    }

    [Fact]
    public void An_id_linked_by_any_link_type_is_not_linked_again()
    {
        List<WireRelation> relations =
        [
            new("System.LinkTypes.Hierarchy-Reverse", $"{Org}/_apis/wit/workItems/5100", null),
            new("System.LinkTypes.Dependency-Reverse", $"{Org}/abc/_apis/wit/workItems/5101", null),
            new("ArtifactLink", "vstfs:///Git/Commit/x", null),
        ];

        Assert.Equal([5102], Mentions.Unlinked([5100, 5101, 5102], relations));
    }

    [Fact]
    public void The_item_itself_and_a_parent_set_in_the_same_write_are_not_linked()
    {
        Assert.Equal([5102], Mentions.Unlinked([5105, 5100, 5102, 5102], null, 5105, 5100));
    }

    [Theory]
    [InlineData(null, "System.LinkTypes.Related")]
    [InlineData("related", "System.LinkTypes.Related")]
    [InlineData("Predecessor", "System.LinkTypes.Dependency-Reverse")]
    [InlineData("duplicate of", "System.LinkTypes.Duplicate-Reverse")]
    [InlineData("System.LinkTypes.Dependency-Forward", "System.LinkTypes.Dependency-Forward")]
    public void A_link_type_is_a_name_from_the_links_tab_or_a_reference_name(string? given, string rel) =>
        Assert.Equal(rel, Mentions.Rel(given));

    [Fact]
    public void An_unknown_link_type_is_refused_naming_the_choices()
    {
        var e = Assert.Throws<ModelContextProtocol.McpException>(() => Mentions.Rel("Blocks"));

        Assert.Contains("Predecessor", e.Message);
    }
}
