namespace TeamsMcp.Tests;

/// <summary>
/// Markdown to Teams HTML for the send tools. The output targets the subset Teams chat renders;
/// input text is escaped, so the only markup in the output is what the converter emits.
/// </summary>
public class MarkdownToHtmlTests
{
    // Teams renders <p> with no margin, so paragraph spacing is explicit: a blank line in the
    // source becomes a double <br/>, a single newline a single one.
    [Fact]
    public void Blank_line_becomes_a_double_break_and_single_newline_a_single_one()
        => Assert.Equal(
            "<p>one<br/><br/>two<br/>still two</p>",
            Markdown.ToHtml("one\n\ntwo\nstill two"));

    [Theory]
    [InlineData("# Title", "<h1>Title</h1>")]
    [InlineData("## Section", "<h2>Section</h2>")]
    [InlineData("### Sub", "<h3>Sub</h3>")]
    [InlineData("#### Deep", "<p><b>Deep</b></p>")]
    [InlineData("###### Deepest", "<p><b>Deepest</b></p>")]
    public void Headings_map_to_h1_to_h3_and_deeper_levels_to_bold_paragraphs(string markdown, string html)
        => Assert.Equal(html, Markdown.ToHtml(markdown));

    [Fact]
    public void Emphasis_converts_but_identifiers_with_underscores_survive()
        => Assert.Equal(
            "<p><b>bold</b> and <i>italic</i> and body_limit and snake_case_name</p>",
            Markdown.ToHtml("**bold** and *italic* and body_limit and snake_case_name"));

    [Fact]
    public void A_link_becomes_an_anchor_with_emphasis_processed_in_its_label()
        => Assert.Equal(
            "<p>see <a href=\"https://example.com/7851\">Bug <b>7851</b></a> today</p>",
            Markdown.ToHtml("see [Bug **7851**](https://example.com/7851) today"));

    [Fact]
    public void A_bare_url_is_autolinked_without_trailing_punctuation()
        => Assert.Equal(
            "<p>go to <a href=\"https://example.com/a_b_c\">https://example.com/a_b_c</a>.</p>",
            Markdown.ToHtml("go to https://example.com/a_b_c."));

    [Fact]
    public void Underscores_and_query_strings_inside_urls_are_not_treated_as_markup()
        => Assert.Equal(
            "<p><a href=\"https://x.test/_a_?q=1&amp;r=2\">https://x.test/_a_?q=1&amp;r=2</a></p>",
            Markdown.ToHtml("https://x.test/_a_?q=1&r=2"));

    [Fact]
    public void Inline_code_is_kept_literal_and_shielded_from_emphasis()
        => Assert.Equal(
            "<p>run <code>dotnet test **now**</code> please</p>",
            Markdown.ToHtml("run `dotnet test **now**` please"));

    [Fact]
    public void A_fenced_block_becomes_pre_with_its_content_escaped_not_interpreted()
        => Assert.Equal(
            "<pre>SELECT *\nFROM T\nWHERE A &lt; 1 &amp;&amp; B</pre>",
            Markdown.ToHtml("```sql\nSELECT *\nFROM T\nWHERE A < 1 && B\n```"));

    [Fact]
    public void Bulleted_lines_become_an_unordered_list()
        => Assert.Equal(
            "<p>Items:</p><ul><li>one</li><li>two</li></ul><p>after</p>",
            Markdown.ToHtml("Items:\n- one\n- two\n\nafter"));

    [Fact]
    public void Numbered_lines_become_an_ordered_list()
        => Assert.Equal(
            "<ol><li>first</li><li>second</li></ol>",
            Markdown.ToHtml("1. first\n2. second"));

    [Fact]
    public void Quoted_lines_become_one_blockquote()
        => Assert.Equal(
            "<blockquote>line one<br/>line two</blockquote>",
            Markdown.ToHtml("> line one\n> line two"));

    [Fact]
    public void A_rule_becomes_hr_and_is_not_mistaken_for_a_bullet()
        => Assert.Equal("<p>a</p><hr/><p>b</p>", Markdown.ToHtml("a\n\n---\n\nb"));

    [Fact]
    public void Html_in_the_input_arrives_as_literal_text()
        => Assert.Equal(
            "<p>&lt;script&gt;alert(1)&lt;/script&gt; &amp; &lt;b&gt;not bold&lt;/b&gt;</p>",
            Markdown.ToHtml("<script>alert(1)</script> & <b>not bold</b>"));

    [Fact]
    public void A_heading_after_other_content_gets_a_spacer_paragraph_above_it()
        => Assert.Equal(
            "<p>intro</p><p>&nbsp;</p><h2>Next</h2>",
            Markdown.ToHtml("intro\n## Next"));

    [Fact]
    public void A_heading_at_the_start_or_under_another_heading_gets_no_spacer()
        => Assert.Equal(
            "<h2>A</h2><h3>B</h3><p>text</p>",
            Markdown.ToHtml("## A\n### B\n\ntext"));

    [Fact]
    public void Lists_and_code_blocks_split_paragraph_runs_without_spacers()
        => Assert.Equal(
            "<p>before</p><ul><li>x</li></ul><p>between</p><pre>code</pre><p>after</p>",
            Markdown.ToHtml("before\n\n- x\n\nbetween\n\n```\ncode\n```\n\nafter"));

    [Fact]
    public void Digits_in_text_are_not_mistaken_for_stash_placeholders()
        => Assert.Equal(
            "<p>paid $948.60 at 1:18 ET via <a href=\"https://x.test/1\">https://x.test/1</a></p>",
            Markdown.ToHtml("paid $948.60 at 1:18 ET via https://x.test/1"));
}
