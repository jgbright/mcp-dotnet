using Microsoft.Graph.Models;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The send tools' <c>format</c> argument chooses the Graph body type. Absent means plain text.
/// </summary>
public class SendFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("text")]
    [InlineData("TEXT")]
    public void Absent_or_text_means_plain_text(string? format)
        => Assert.Equal(BodyType.Text, TeamsTools.ParseFormat(format));

    [Theory]
    [InlineData("html")]
    [InlineData("HTML")]
    [InlineData("Html")]
    public void Html_is_matched_case_insensitively(string format)
        => Assert.Equal(BodyType.Html, TeamsTools.ParseFormat(format));

    [Fact]
    public void An_unknown_format_is_rejected_and_names_the_valid_values()
    {
        var error = Assert.Throws<McpException>(() => TeamsTools.ParseFormat("markdown"));

        Assert.Contains("markdown", error.Message);
        Assert.Contains("'text'", error.Message);
        Assert.Contains("'html'", error.Message);
    }
}
