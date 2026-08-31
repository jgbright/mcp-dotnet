using System.Text;
using TeamsMcp;

namespace TeamsMcp.Tests;

/// <summary>
/// The scope set is computed from the send gate. These cover that computation and the consent
/// record that catches the failure it allows: a gate turned on after a sign-in that never consented
/// to sending.
/// </summary>
public class ScopePolicyTests
{
    [Fact]
    public void A_read_only_deployment_does_not_ask_for_the_send_scopes()
    {
        var scopes = GraphContext.ScopesFor(sendEnabled: false);

        Assert.Equal(GraphContext.ReadScopes, scopes);
        Assert.DoesNotContain("ChannelMessage.Send", scopes);
        Assert.DoesNotContain("ChatMessage.Send", scopes);
    }

    [Fact]
    public void The_gate_only_ever_widens_the_ask()
    {
        var reading = GraphContext.ScopesFor(sendEnabled: false);
        var sending = GraphContext.ScopesFor(sendEnabled: true);

        Assert.All(reading, s => Assert.Contains(s, sending));
        Assert.Equal(GraphContext.SendScopes, sending.Except(reading).ToArray());
        Assert.Equal(sending.Length, sending.Distinct().Count());
    }

    [Fact]
    public void The_requested_set_is_read_from_the_environment_when_it_is_asked_for()
    {
        using (new EnvVar("TEAMS_MCP_ALLOW_SEND", "true"))
        {
            Assert.Contains("ChatMessage.Send", GraphContext.Scopes);
        }

        using (new EnvVar("TEAMS_MCP_ALLOW_SEND", null))
        {
            Assert.DoesNotContain("ChatMessage.Send", GraphContext.Scopes);
        }
    }
}

public class ScopeConsentTests
{
    [Fact]
    public void An_unrecorded_consent_is_unknown_and_never_reported_as_missing()
    {
        // A missing record means unknown. Warning on every startup of a server that works would be
        // worse than saying nothing.
        Assert.Empty(ScopeConsent.Missing(null, GraphContext.ScopesFor(sendEnabled: true)));
    }

    [Fact]
    public void A_consent_covering_the_request_is_missing_nothing()
    {
        var consented = GraphContext.ScopesFor(sendEnabled: true);

        Assert.Empty(ScopeConsent.Missing(consented, GraphContext.ScopesFor(sendEnabled: false)));
        Assert.Empty(ScopeConsent.Missing(consented, consented));
    }

    [Fact]
    public void The_gate_turned_on_after_a_read_only_sign_in_names_the_send_scopes()
    {
        var missing = ScopeConsent.Missing(
            GraphContext.ScopesFor(sendEnabled: false),
            GraphContext.ScopesFor(sendEnabled: true));

        Assert.Equal(GraphContext.SendScopes, missing);
    }

    [Fact]
    public void Scope_names_compare_case_insensitively()
    {
        Assert.Empty(ScopeConsent.Missing(["chatmessage.send"], ["ChatMessage.Send"]));
    }

    [Fact]
    public void A_recorded_consent_round_trips()
    {
        var path = TempPath();
        string[] scopes = ["User.Read", "Chat.Read"];

        ScopeConsent.Write(path, scopes);

        Assert.Equal(scopes, ScopeConsent.Read(path));
    }

    [Fact]
    public void Rewriting_replaces_the_previous_set_rather_than_merging_it()
    {
        var path = TempPath();
        ScopeConsent.Write(path, GraphContext.ScopesFor(sendEnabled: true));

        ScopeConsent.Write(path, GraphContext.ScopesFor(sendEnabled: false));

        Assert.Equal(GraphContext.ScopesFor(sendEnabled: false), ScopeConsent.Read(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[\"User.Read\"]")]
    public void An_unreadable_record_reads_as_unknown_rather_than_as_empty(string content)
    {
        var path = TempPath();
        File.WriteAllText(path, content);

        // An unreadable record means unknown. Reading it as "consented to nothing" would demand a
        // re-auth that is not needed.
        Assert.Empty(ScopeConsent.Missing(ScopeConsent.Read(path), GraphContext.Scopes));
    }

    [Fact]
    public void A_missing_record_reads_as_unknown()
    {
        Assert.Null(ScopeConsent.Read(TempPath()));
    }

    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "teams-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "auth-scopes.json");
    }
}

/// <summary>
/// What Entra granted is read off the token's scp claim. It differs from the requested set: a scope
/// consented to earlier comes back in the token even when this sign-in did not ask for it, and
/// the token's set decides whether a later send works.
/// </summary>
public class TokenScopeTests
{
    [Fact]
    public void The_scp_claim_is_read_as_a_space_delimited_string()
    {
        var token = Jwt("""{"aud":"https://graph.microsoft.com","scp":"User.Read Chat.Read ChatMessage.Send"}""");

        Assert.Equal(
            new[] { "User.Read", "Chat.Read", "ChatMessage.Send" },
            ScopeConsent.FromToken(token));
    }

    [Fact]
    public void An_array_valued_scp_claim_is_read_too()
    {
        var token = Jwt("""{"scp":["User.Read","Chat.Read"]}""");

        Assert.Equal(new[] { "User.Read", "Chat.Read" }, ScopeConsent.FromToken(token));
    }

    [Theory]
    [InlineData("""{"aud":"https://graph.microsoft.com"}""")]
    [InlineData("""{"scp":42}""")]
    public void A_token_without_readable_scopes_yields_null(string payload)
    {
        Assert.Null(ScopeConsent.FromToken(Jwt(payload)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("opaque-token")]
    [InlineData("a.!!!not-base64!!!.c")]
    public void Anything_that_is_not_a_jwt_yields_null_rather_than_throwing(string token)
    {
        // An unparsable token costs a diagnostic. It must never fail the sign-in.
        Assert.Null(ScopeConsent.FromToken(token));
    }

    /// <summary>An unsigned JWT shell: only the payload is ever looked at.</summary>
    private static string Jwt(string payload) =>
        $"{Base64Url("""{"typ":"JWT","alg":"none"}""")}.{Base64Url(payload)}.signature";

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
