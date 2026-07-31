using Microsoft.Graph.Models;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The send tools' <c>format</c> argument chooses the Graph body. Absent means plain text;
/// markdown converts server-side to HTML; html passes through untouched.
/// </summary>
public class SendFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("text")]
    [InlineData("TEXT")]
    public void Absent_or_text_means_plain_text_passed_through(string? format)
    {
        var body = TeamsTools.BuildBody("**not** converted", format);

        Assert.Equal(BodyType.Text, body.ContentType);
        Assert.Equal("**not** converted", body.Content);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("HTML")]
    [InlineData("Html")]
    public void Html_is_matched_case_insensitively_and_passed_through(string format)
    {
        var body = TeamsTools.BuildBody("<p>as <b>written</b></p>", format);

        Assert.Equal(BodyType.Html, body.ContentType);
        Assert.Equal("<p>as <b>written</b></p>", body.Content);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("Markdown")]
    public void Markdown_is_converted_to_an_html_body(string format)
    {
        var body = TeamsTools.BuildBody("**bold** text", format);

        Assert.Equal(BodyType.Html, body.ContentType);
        Assert.Equal("<p><b>bold</b> text</p>", body.Content);
    }

    [Fact]
    public void An_unknown_format_is_rejected_and_names_the_valid_values()
    {
        var error = Assert.Throws<McpException>(() => TeamsTools.BuildBody("x", "rtf"));

        Assert.Contains("rtf", error.Message);
        Assert.Contains("'text'", error.Message);
        Assert.Contains("'markdown'", error.Message);
        Assert.Contains("'html'", error.Message);
    }
}
