using Microsoft.Graph.Models;

namespace TeamsMcp.Tests;

/// <summary>
/// The HTML pipeline drops markup but keeps what an agent can act on: links, alt text, and table
/// and list structure.
/// </summary>
public class HtmlToTextTests
{
    [Fact]
    public void Anchor_keeps_text_and_url()
    {
        Assert.Equal(
            "the build (https://example.com/build)",
            TeamsTools.HtmlToText("""<a href="https://example.com/build">the build</a>"""));
    }

    [Fact]
    public void Anchor_whose_text_is_the_url_is_not_duplicated()
    {
        Assert.Equal(
            "https://example.com/x",
            TeamsTools.HtmlToText("""<a href="https://example.com/x">https://example.com/x</a>"""));
    }

    [Fact]
    public void Anchor_with_empty_text_falls_back_to_the_url()
    {
        Assert.Equal(
            "https://example.com/x",
            TeamsTools.HtmlToText("""<a href="https://example.com/x"><span></span></a>"""));
    }

    [Fact]
    public void Anchor_inner_markup_is_stripped_but_its_text_kept()
    {
        Assert.Equal(
            "click me (https://example.com)",
            TeamsTools.HtmlToText("""<a href="https://example.com"><b>click</b> me</a>"""));
    }

    [Fact]
    public void Anchor_spanning_newlines_is_matched()
    {
        Assert.Equal(
            "deploy\nnotes (https://example.com)",
            TeamsTools.HtmlToText("<a href=\"https://example.com\">deploy\nnotes</a>"));
    }

    [Fact]
    public void Image_survives_as_its_alt_text()
    {
        Assert.Equal("shipped :rocket:", TeamsTools.HtmlToText("""shipped <img src="e.png" alt=":rocket:">"""));
    }

    [Fact]
    public void Table_cells_are_separated_and_rows_broken()
    {
        Assert.Equal(
            "a | b | \nc | d |",
            TeamsTools.HtmlToText("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>"));
    }

    [Fact]
    public void List_items_become_dashes()
    {
        Assert.Equal("- one\n- two", TeamsTools.HtmlToText("<ul><li>one</li><li>two</li></ul>"));
    }

    [Fact]
    public void Block_tags_become_newlines()
    {
        Assert.Equal("one\ntwo\nthree", TeamsTools.HtmlToText("<div>one</div><p>two</p>three<br/>"));
    }

    [Fact]
    public void Remaining_tags_are_removed()
    {
        Assert.Equal("bold and code", TeamsTools.HtmlToText("<span><b>bold</b> and <code>code</code></span>"));
    }

    [Fact]
    public void Entities_are_decoded_after_tags_are_stripped()
    {
        // Decoding last keeps a literal "&lt;ok&gt;" in a body from being stripped as markup.
        Assert.Equal("a&b <ok>", TeamsTools.HtmlToText("<p>a&amp;b &lt;ok&gt;</p>"));
    }

    [Fact]
    public void Nbsp_is_normalized_to_an_ordinary_space()
    {
        var text = TeamsTools.HtmlToText("hello&nbsp;world");
        Assert.Equal("hello world", text);
        Assert.DoesNotContain('\u00A0', text);
    }

    [Fact]
    public void Runs_of_blank_lines_collapse_to_one_and_the_result_is_trimmed()
    {
        // A single break stays a single break. Only runs of three or more collapse.
        Assert.Equal("a\n\nb", TeamsTools.HtmlToText("<p></p><p></p>a<br/><br/><br/><br/>b<br/><br/>"));
    }

    [Fact]
    public void Plain_text_passes_through_unchanged()
    {
        Assert.Equal("just words", TeamsTools.HtmlToText("just words"));
    }

    [Fact]
    public void Realistic_teams_message_is_readable()
    {
        const string html =
            """<div>Deploy is <b>done</b>.<br>See <a href="https://ado/build/42">build 42</a> for logs.</div>""";
        Assert.Equal("Deploy is done.\nSee build 42 (https://ado/build/42) for logs.", TeamsTools.HtmlToText(html));
    }

    // ---------------------------------------------------------------- ToPlainText / StripHtml

    [Fact]
    public void ToPlainText_returns_null_for_missing_or_empty_bodies()
    {
        Assert.Null(TeamsTools.ToPlainText(null));
        Assert.Null(TeamsTools.ToPlainText(new ItemBody { ContentType = BodyType.Text, Content = null }));
        Assert.Null(TeamsTools.ToPlainText(new ItemBody { ContentType = BodyType.Text, Content = "" }));
    }

    [Fact]
    public void ToPlainText_converts_html_bodies_and_only_trims_text_bodies()
    {
        Assert.Equal("hi", TeamsTools.ToPlainText(new ItemBody { ContentType = BodyType.Html, Content = "<p>hi</p>" }));
        Assert.Equal("<p>hi</p>", TeamsTools.ToPlainText(new ItemBody { ContentType = BodyType.Text, Content = "  <p>hi</p>  " }));
    }

    [Fact]
    public void StripHtml_returns_null_for_null_or_empty()
    {
        Assert.Null(TeamsTools.StripHtml(null));
        Assert.Null(TeamsTools.StripHtml(""));
        Assert.Equal("summary", TeamsTools.StripHtml("<c0>summary</c0>"));
    }
}
