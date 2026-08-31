using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// A quoted reply is refused in the self chat, where Graph accepts the call and silently drops the
/// quote. Without the refusal the message looks sent and renders as an empty box, so nothing
/// downstream reports a problem.
/// </summary>
public class SendReplyTests
{
    [Theory]
    [InlineData("48:notes")]
    [InlineData("48:NOTES")]
    public void A_quoted_reply_to_the_self_chat_is_refused(string chat)
    {
        var error = Assert.Throws<McpException>(() => TeamsTools.RequireQuotableChat(chat, "1786667767749"));

        Assert.Contains(chat, error.Message);
        Assert.Contains("reply_to", error.Message);
        Assert.Contains("19:", error.Message);
    }

    [Theory]
    [InlineData("48:notes")]
    [InlineData("19:97641583cf154265a237da28ebbde27a@thread.v2")]
    public void A_plain_send_is_allowed_anywhere(string chat)
    {
        TeamsTools.RequireQuotableChat(chat, null);
    }

    [Fact]
    public void A_quoted_reply_to_a_real_chat_is_allowed()
    {
        TeamsTools.RequireQuotableChat("19:97641583cf154265a237da28ebbde27a@thread.v2", "1786667767749");
    }
}
