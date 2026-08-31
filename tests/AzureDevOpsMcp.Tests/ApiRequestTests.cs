using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The escape hatch's pure half: it must not send this server's token anywhere but this
/// organization, must not hand back a value Azure DevOps marked secret, and must refuse a
/// writing method unless writing is on.
/// </summary>
public class ApiRequestTests
{
    private const string Org = "https://dev.azure.com/contoso";

    [Theory]
    [InlineData("_apis/projects", "core")]
    [InlineData("Core/_apis/release/definitions/31", "vsrm")]
    [InlineData("/Core/_apis/release/releases/4337", "vsrm")]
    [InlineData("Core/_apis/search/codesearchresults", "search")]
    [InlineData("_apis/identities", "vssps")]
    public void The_host_is_inferred_from_the_path_when_it_is_not_given(string path, string host)
    {
        // A /_apis/release/ path on the core host answers 404, which reads as "no such
        // definition". Wrong answer, not an error.
        Assert.Equal(host, ApiRequest.ResolveHost(path, host: null));
    }

    [Fact]
    public void An_explicit_host_wins_and_an_unknown_one_is_refused()
    {
        Assert.Equal("core", ApiRequest.ResolveHost("Core/_apis/release/definitions/31", "core"));

        var e = Assert.Throws<McpException>(() => ApiRequest.ResolveHost("_apis/projects", "elsewhere"));
        Assert.Contains("vsrm", e.Message);
    }

    [Fact]
    public void A_relative_path_is_hung_off_the_resolved_host_with_an_api_version()
    {
        Assert.Equal(
            "https://vsrm.dev.azure.com/contoso/Core/_apis/release/definitions/31?api-version=7.1",
            ApiRequest.Url(Org, "Core/_apis/release/definitions/31", query: null, host: null));

        Assert.Equal(
            "https://dev.azure.com/contoso/_apis/projects?$top=10&api-version=7.1",
            ApiRequest.Url(Org, "_apis/projects", "$top=10", host: null));
    }

    [Fact]
    public void An_api_version_the_caller_chose_is_left_alone()
    {
        // Several endpoints reject a bare 7.1 (work item comments, search, identities), so a
        // caller who named a version means it.
        Assert.Equal(
            "https://vssps.dev.azure.com/contoso/_apis/identities?api-version=7.1-preview.1",
            ApiRequest.Url(Org, "_apis/identities", "api-version=7.1-preview.1", host: null));
    }

    [Fact]
    public void An_absolute_url_inside_the_organization_is_allowed()
    {
        var url = ApiRequest.Url(
            Org, "https://vsrm.dev.azure.com/contoso/Core/_apis/release/releases/1?api-version=7.1",
            query: null, host: null);

        Assert.StartsWith("https://vsrm.dev.azure.com/contoso/", url);
    }

    [Theory]
    [InlineData("https://evil.example.com/_apis/projects")]
    [InlineData("https://dev.azure.com/other-org/_apis/projects")]
    public void An_absolute_url_outside_the_organization_is_refused(string url)
    {
        // The request carries this server's bearer token. Following a caller's url anywhere else
        // would hand that token to whoever asked for it.
        var e = Assert.Throws<McpException>(() => ApiRequest.Url(Org, url, query: null, host: null));

        Assert.Contains("not part of this server's organization", e.Message);
    }

    [Fact]
    public void An_empty_path_says_what_one_looks_like()
    {
        Assert.Contains("_apis/release/definitions/31",
            Assert.Throws<McpException>(() => ApiRequest.Url(Org, "  ", null, null)).Message);
    }

    [Fact]
    public void Reading_methods_need_no_gate_and_writing_ones_do()
    {
        Assert.Equal(HttpMethod.Get, ApiRequest.Method(null));
        Assert.Equal(HttpMethod.Get, ApiRequest.Method("get"));
        Assert.Equal(HttpMethod.Head, ApiRequest.Method("HEAD"));

        var e = Assert.Throws<McpException>(() => ApiRequest.Method("POST"));
        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);

        using var write = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");
        Assert.Equal(HttpMethod.Post, ApiRequest.Method("post"));
    }

    [Fact]
    public void An_unknown_method_is_refused_before_the_gate_is_consulted()
    {
        Assert.Contains("Use GET", Assert.Throws<McpException>(() => ApiRequest.Method("FETCH")).Message);
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void A_value_the_service_marked_secret_never_survives_the_mask()
    {
        var masked = ApiRequest.Mask(Parse("""
            {
              "variables": { "Stripe.ApiKey": { "value": "sk_live_51abc", "isSecret": true } },
              "environments": [
                { "name": "Production",
                  "variables": {
                    "Stripe.WebhookSecret": { "value": "whsec_9zz", "isSecret": true },
                    "OTEL_SERVICE_NAME": { "value": "Stripe Webhook" } } }
              ]
            }
            """))!;

        var json = masked.ToJsonString();
        Assert.DoesNotContain("sk_live_51abc", json);
        Assert.DoesNotContain("whsec_9zz", json);
        // The name and the flag survive: "there is one and you may not have it" is the answer.
        Assert.Contains("Stripe.WebhookSecret", json);
        Assert.Contains("\"isSecret\":true", json);
        Assert.Equal("Stripe Webhook",
            masked["environments"]![0]!["variables"]!["OTEL_SERVICE_NAME"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void The_mask_follows_the_flag_rather_than_the_endpoint()
    {
        // The walk is over the parsed body, so a shape this server has never seen is masked on
        // the same rule.
        var masked = ApiRequest.Mask(Parse(
            """{"some":{"thing":[{"nested":{"value":"hunter2","isSecret":true,"other":1}}]}}"""))!;

        Assert.DoesNotContain("hunter2", masked.ToJsonString());
        Assert.Equal(1, masked["some"]!["thing"]![0]!["nested"]!["other"]!.GetValue<int>());
    }

    [Fact]
    public void A_flag_that_is_not_true_leaves_the_value_alone()
    {
        var masked = ApiRequest.Mask(Parse("""{"v":{"value":"public","isSecret":false}}"""))!;

        Assert.Equal("public", masked["v"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void The_filter_walks_names_and_maps_over_arrays()
    {
        var body = Parse("""
            {"count":2,"value":[{"name":"Dev","id":1},{"name":"Prod","id":2}]}
            """);

        Assert.Equal("2", ApiRequest.Filter(body, "count")!.ToJsonString());
        Assert.Equal("""["Dev","Prod"]""", ApiRequest.Filter(body, "value[].name")!.ToJsonString());
        Assert.Equal("""{"name":"Prod","id":2}""", ApiRequest.Filter(body, "value[1]")!.ToJsonString());
        Assert.Equal("\"Prod\"", ApiRequest.Filter(body, ".value[1].name")!.ToJsonString());
    }

    [Fact]
    public void Mapping_over_nested_arrays_flattens_to_one_list()
    {
        var body = Parse("""
            {"environments":[
               {"deployPhases":[{"workflowTasks":[{"name":"File Transform"},{"name":"Copy"}]}]},
               {"deployPhases":[{"workflowTasks":[{"name":"Start"}]}]}]}
            """);

        Assert.Equal(
            """["File Transform","Copy","Start"]""",
            ApiRequest.Filter(body, "environments[].deployPhases[].workflowTasks[].name")!.ToJsonString());
    }

    [Fact]
    public void A_filter_that_matches_nothing_is_null_rather_than_an_error()
    {
        // "No such field" is an answer the caller can act on without a second call.
        var body = Parse("""{"value":[{"name":"Dev"}]}""");

        Assert.Null(ApiRequest.Filter(body, "nope"));
        Assert.Null(ApiRequest.Filter(body, "value[9]"));
        Assert.Null(ApiRequest.Filter(body, "count[]"));
    }

    [Fact]
    public void A_patch_document_is_sent_as_json_patch_because_nothing_else_reaches_a_work_item()
    {
        // Under application/json every work item PATCH gets a 400 on the content type, so
        // without the inference the escape hatch cannot reach the endpoint at all.
        Assert.Equal(
            ApiRequest.JsonPatchMediaType,
            ApiRequest.ContentType(
                """[{"op":"add","path":"/fields/System.State","value":"Active"}]""", null));
    }

    [Theory]
    [InlineData("""{"query":"SELECT [System.Id] FROM WorkItems"}""")]  // an object, not a patch
    [InlineData("""["Dev","QA"]""")]                                   // an array of the wrong thing
    [InlineData("[]")]                                                 // an empty one says nothing
    [InlineData("""[{"path":"/fields/System.State"}]""")]              // objects, but no op
    [InlineData("[{oops")]                                             // unparseable
    [InlineData(null)]
    public void Anything_that_is_not_a_patch_document_goes_as_plain_json(string? body)
    {
        Assert.Equal(ApiRequest.JsonMediaType, ApiRequest.ContentType(body, null));
    }

    [Fact]
    public void An_explicit_content_type_wins_and_an_unusable_one_is_refused()
    {
        // Same rule as `host`: a wrong inference must not be a dead end.
        Assert.Equal(
            "application/octet-stream",
            ApiRequest.ContentType("""[{"op":"add","path":"/x","value":1}]""", "application/octet-stream"));
        Assert.Equal(
            "application/json; charset=utf-8",
            ApiRequest.ContentType(null, " application/json; charset=utf-8 "));

        var e = Assert.Throws<McpException>(() => ApiRequest.ContentType(null, "not a media type"));
        Assert.Contains("content_type", e.Message);
    }
}
