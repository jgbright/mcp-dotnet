namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Work item fields arrive as HTML. The conversion does more than strip tags: links, alt text, and
/// list and table structure have to survive, and everything else has to go.
/// </summary>
public class HtmlToTextTests
{
    [Fact]
    public void Links_keep_both_their_text_and_their_url()
    {
        Assert.Equal("the build (https://dev.azure.com/contoso/_build/results?buildId=42)",
            Text.HtmlToText("""<a href="https://dev.azure.com/contoso/_build/results?buildId=42">the build</a>"""));
    }

    [Fact]
    public void A_link_whose_text_is_its_url_is_not_written_twice()
    {
        Assert.Equal("https://contoso.example",
            Text.HtmlToText("""<a href="https://contoso.example">https://contoso.example</a>"""));
    }

    [Fact]
    public void A_link_with_no_text_degrades_to_the_bare_url()
    {
        Assert.Equal("https://contoso.example",
            Text.HtmlToText("""<a href="https://contoso.example"><span></span></a>"""));
    }

    [Fact]
    public void Images_survive_as_their_alt_text()
    {
        Assert.Equal("repro screenshot", Text.HtmlToText("""<img src="blob.png" alt="repro screenshot">"""));
    }

    [Fact]
    public void List_items_become_dashes()
    {
        Assert.Equal("- first\n- second", Text.HtmlToText("<ul><li>first</li><li>second</li></ul>"));
    }

    [Fact]
    public void Table_cells_are_separated_and_rows_broken()
    {
        Assert.Equal("env | region | \nprod | westus |",
            Text.HtmlToText("<table><tr><td>env</td><td>region</td></tr><tr><td>prod</td><td>westus</td></tr></table>"));
    }

    [Fact]
    public void Entities_are_decoded_and_nbsp_becomes_a_plain_space()
    {
        Assert.Equal("a & b, x < y", Text.HtmlToText("a&nbsp;&amp; b, x &lt; y"));
    }

    [Fact]
    public void Runs_of_blank_lines_are_collapsed()
    {
        Assert.Equal("one\n\ntwo", Text.HtmlToText("<p>one</p><p></p><p></p><p>two</p>"));
    }

    [Fact]
    public void Blank_input_maps_to_null_so_the_field_disappears()
    {
        Assert.Null(Text.FromHtml(null));
        Assert.Null(Text.FromHtml(""));
        Assert.Null(Text.FromHtml("   "));
        Assert.Null(Text.FromHtml("<div><p></p></div>"));
    }
}

/// <summary>
/// Pull request descriptions and comments are Markdown, and routinely a mixture of Markdown and
/// HTML, since Azure DevOps accepts pasted HTML inside them.
/// </summary>
public class MarkdownToTextTests
{
    [Fact]
    public void Links_keep_both_their_text_and_their_url()
    {
        Assert.Equal("AB#1234 (https://dev.azure.com/contoso/_workitems/edit/1234)",
            Text.MarkdownToText("[AB#1234](https://dev.azure.com/contoso/_workitems/edit/1234)"));
    }

    [Fact]
    public void Images_are_reduced_to_their_alt_text_and_never_leave_a_dangling_link()
    {
        Assert.Equal("failing test", Text.MarkdownToText("![failing test](https://contoso.example/a.png)"));
    }

    [Fact]
    public void An_image_with_no_alt_text_disappears_entirely()
    {
        Assert.Equal("", Text.MarkdownToText("![](https://contoso.example/a.png)"));
    }

    [Fact]
    public void Autolinks_are_unwrapped_rather_than_eaten_by_the_html_stripper()
    {
        Assert.Equal("see https://contoso.example/docs", Text.MarkdownToText("see <https://contoso.example/docs>"));
    }

    [Fact]
    public void Headings_lose_their_hashes_but_keep_their_text()
    {
        Assert.Equal("Summary\n\nfixed the retry loop", Text.MarkdownToText("## Summary\n\nfixed the retry loop"));
    }

    [Fact]
    public void Code_fences_go_away_and_the_code_stays()
    {
        Assert.Equal("var x = 1;", Text.MarkdownToText("```csharp\nvar x = 1;\n```"));
    }

    [Fact]
    public void Inline_code_loses_only_its_backticks()
    {
        Assert.Equal("call GetClientAsync first", Text.MarkdownToText("call `GetClientAsync` first"));
    }

    [Fact]
    public void Bullets_of_every_flavour_normalize_to_one()
    {
        Assert.Equal("- a\n- b\n- c", Text.MarkdownToText("* a\n+ b\n- c"));
    }

    [Fact]
    public void Emphasis_markers_are_removed()
    {
        Assert.Equal("do not merge yet, really",
            Text.MarkdownToText("**do not** merge *yet*, __really__"));
    }

    [Fact]
    public void Underscores_inside_identifiers_survive()
    {
        // Emphasis is matched at word boundaries because snake_case is everywhere in a pull
        // request description, and mangling it changes what the text says.
        Assert.Equal("set body_limit and include_system to defaults",
            Text.MarkdownToText("set body_limit and include_system to defaults"));
    }

    [Fact]
    public void Block_quotes_and_horizontal_rules_are_dropped()
    {
        Assert.Equal("quoted line\n\nafter", Text.MarkdownToText("> quoted line\n\n---\n\nafter"));
    }

    [Fact]
    public void Inline_html_inside_markdown_is_handled_too()
    {
        Assert.Equal("before\nafter", Text.MarkdownToText("before<br>after"));
    }

    [Fact]
    public void Blank_input_maps_to_null_so_the_field_disappears()
    {
        Assert.Null(Text.FromMarkdown(null));
        Assert.Null(Text.FromMarkdown(""));
        Assert.Null(Text.FromMarkdown("   "));
    }
}

public class TruncateTests
{
    [Fact]
    public void Short_bodies_are_returned_untouched_and_unflagged()
    {
        var (body, truncated) = Text.Truncate("short", 2000);

        Assert.Equal("short", body);
        Assert.Null(truncated); // false would be serialized as a padding field
    }

    [Fact]
    public void Long_bodies_are_cut_and_flagged()
    {
        var (body, truncated) = Text.Truncate(new string('x', 50), 10);

        Assert.Equal(new string('x', 10), body);
        Assert.True(truncated);
    }

    [Fact]
    public void A_body_exactly_at_the_limit_is_not_flagged()
    {
        var (_, truncated) = Text.Truncate(new string('x', 10), 10);

        Assert.Null(truncated);
    }

    [Fact]
    public void A_limit_of_zero_means_unlimited()
    {
        var (body, truncated) = Text.Truncate(new string('x', 5000), 0);

        Assert.Equal(5000, body!.Length);
        Assert.Null(truncated);
    }

    [Fact]
    public void Null_stays_null()
    {
        Assert.Equal((null, null), Text.Truncate(null, 10));
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // "ab😀cd" is six chars. The emoji is a surrogate pair at indexes 2 and 3, so a limit of 3
        // lands in the middle of it, and keeping half would emit an invalid character.
        var (body, truncated) = Text.Truncate("ab😀cd", 3);

        Assert.Equal("ab", body);
        Assert.True(truncated);
    }
}

/// <summary>
/// An auth failure arrives as a whole HTML page. Reducing it to the sentence inside keeps "the
/// token expired" readable instead of a stylesheet in an error message.
/// </summary>
public class HtmlErrorTests
{
    [Fact]
    public void The_sentence_survives_and_the_page_does_not()
    {
        var message = Text.ErrorFromHtml(
            "<html><head><style>.a{color:red}</style><script>var x=1;</script></head>" +
            "<body><p>Access Denied: The Personal Access Token used has expired.</p>" +
            "<p>Contact your administrator.</p></body></html>");

        Assert.StartsWith("Access Denied: The Personal Access Token used has expired.", message);
        Assert.DoesNotContain("color:red", message);
        Assert.DoesNotContain("var x", message);
    }

    [Theory]
    [InlineData("""{"message":"nope"}""")]
    [InlineData("The controller for path was not found.")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_page_is_left_to_the_caller(string? body)
    {
        // A JSON error has its own field, and a plain-text one is already the message. Claiming
        // either as extracted HTML would only make the caller's branch wrong.
        Assert.Null(Text.ErrorFromHtml(body));
    }

    [Fact]
    public void A_page_of_nothing_but_markup_extracts_nothing()
    {
        Assert.Null(Text.ErrorFromHtml("<html><head><style>.a{}</style></head><body><br/></body></html>"));
    }

    [Fact]
    public void A_long_page_is_cut_rather_than_quoted_whole()
    {
        var message = Text.ErrorFromHtml("<p>" + new string('x', 900) + "</p>");

        Assert.EndsWith("…", message);
        Assert.True(message!.Length <= 301);
    }
}
