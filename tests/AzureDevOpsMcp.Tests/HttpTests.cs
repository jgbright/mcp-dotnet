using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// This handler answers "which REST call failed and why" without adding code. Two behaviors matter:
/// it logs the ids Azure DevOps support asks for, and it puts the error body back so the client can
/// still parse the service's own message out of it.
/// </summary>
public class AdoLoggingHandlerTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;

    public AdoLoggingHandlerTests() => _factory = TestLog.Factory(_sink);

    public void Dispose() => _factory.Dispose();

    private HttpMessageInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new AdoLoggingHandler(_factory.CreateLogger("AdoContext")) { InnerHandler = new StubHandler(respond) });

    [Fact]
    public async Task Successful_calls_are_logged_at_debug_with_method_path_and_status()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(Request("https://dev.azure.com/contoso/_apis/projects"), CancellationToken.None);

        Assert.Contains(" DBG ", _sink.Last);
        Assert.Contains(" http ", _sink.Last);
        Assert.Contains("GET /contoso/_apis/projects -> 200", _sink.Last);
        Assert.Contains("ms=", _sink.Last);
    }

    [Fact]
    public async Task The_host_is_stripped_but_the_query_is_kept()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(
            Request("https://dev.azure.com/contoso/Core/_apis/build/builds?definitions=4&$top=21"),
            CancellationToken.None);

        Assert.Contains("/contoso/Core/_apis/build/builds?definitions=4&$top=21", _sink.Last);
        Assert.DoesNotContain("dev.azure.com", _sink.Last);
    }

    [Fact]
    public async Task Failures_log_the_support_ids_the_throttling_hints_and_the_services_own_body()
    {
        const string body = """{"message":"TF400813: Resource not available","typeKey":"UnauthorizedRequestException"}""";
        using var invoker = Invoker(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body),
            };
            response.Headers.TryAddWithoutValidation("ActivityId", "298a99a3-1111-2222-3333-444455556666");
            response.Headers.TryAddWithoutValidation("x-vss-e2eid", "aaaa1111-bbbb-cccc-dddd-eeee22223333");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Delay", "30");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        await invoker.SendAsync(Request("https://dev.azure.com/contoso/_apis/projects"), CancellationToken.None);

        Assert.Contains(" WRN ", _sink.Last);
        Assert.Contains(" http.fail ", _sink.Last);
        Assert.Contains("-> 429", _sink.Last);
        Assert.Contains("activity-id=\"298a99a3-1111-2222-3333-444455556666\"", _sink.Last);
        Assert.Contains("request-id=\"aaaa1111-bbbb-cccc-dddd-eeee22223333\"", _sink.Last);
        Assert.Contains("retry-after=\"42\"", _sink.Last);
        Assert.Contains("rate-limit-delay=\"30\"", _sink.Last);
        Assert.Contains("TF400813", _sink.Last);
    }

    [Fact]
    public async Task The_error_body_is_still_readable_downstream_after_being_logged()
    {
        const string body = """{"message":"TF401019","typeKey":"GitRepositoryNotFoundException"}""";
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        var response = await invoker.SendAsync(
            Request("https://dev.azure.com/contoso/_apis/projects"), CancellationToken.None);

        // Buffering the body for the log must not consume it. The error message is parsed downstream.
        Assert.Equal(body, await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_headers_are_omitted_rather_than_logged_as_empty()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(Request("https://dev.azure.com/contoso/_apis/projects"), CancellationToken.None);

        Assert.DoesNotContain("activity-id=", _sink.Last);
        Assert.DoesNotContain("retry-after=", _sink.Last);
    }

    [Fact]
    public async Task Transport_failures_are_logged_and_rethrown()
    {
        using var invoker = Invoker(_ => throw new HttpRequestException("no such host"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(Request("https://dev.azure.com/contoso/_apis/projects"), CancellationToken.None));

        Assert.Contains("transport failure", _sink.Last);
        Assert.Contains("!! System.Net.Http.HttpRequestException: no such host", _sink.Last);
    }

    [Fact]
    public async Task Post_requests_are_logged_with_their_method()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://dev.azure.com/contoso/Core/_apis/wit/wiql");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("POST /contoso/Core/_apis/wit/wiql -> 200", _sink.Last);
    }

    private static HttpRequestMessage Request(string url) => new(HttpMethod.Get, url);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

/// <summary>
/// The REST wrapper turns every non-answer into an <see cref="AdoApiException"/> that says
/// something useful, including the awkward case where Azure DevOps answers an unauthenticated
/// request with a sign-in page and a success status.
/// </summary>
public class AdoClientTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly List<Uri> _requested = [];

    public AdoClientTests() => _factory = TestLog.Factory(_sink);

    public void Dispose() => _factory.Dispose();

    private AdoClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(r =>
            {
                _requested.Add(r.RequestUri!);
                return respond(r);
            })),
            "https://dev.azure.com/contoso",
            _factory.CreateLogger("AdoContext"));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Relative_paths_are_resolved_against_the_organization()
    {
        var client = Client(_ => Json("""{"count":0,"value":[]}"""));

        await client.GetAsync<ListResponse<WireProject>>("_apis/projects?api-version=7.1", default);

        Assert.Equal("https://dev.azure.com/contoso/_apis/projects?api-version=7.1", _requested[0].ToString());
    }

    [Fact]
    public async Task An_absolute_url_is_used_as_given_so_log_links_can_be_followed()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("build output", Encoding.UTF8, "text/plain"),
        });

        var text = await client.GetTextAsync("https://vsblob.dev.azure.com/contoso/logs/7", default);

        Assert.Equal("build output", text);
        Assert.Equal("https://vsblob.dev.azure.com/contoso/logs/7", _requested[0].ToString());
    }

    [Fact]
    public async Task Camel_cased_json_deserializes_into_the_wire_records()
    {
        var client = Client(_ => Json("""
            {"count":1,"value":[{"id":"p1","name":"Core","state":"wellFormed","lastUpdateTime":"2026-07-01T00:00:00Z"}]}
            """));

        var response = await client.GetAsync<ListResponse<WireProject>>("_apis/projects", default);

        var project = Assert.Single(response.Value!);
        Assert.Equal("Core", project.Name);
        Assert.Equal("wellFormed", project.State);
    }

    [Fact]
    public async Task A_sign_in_page_is_reported_as_a_rejected_token_rather_than_a_parse_error()
    {
        // Azure DevOps answers an unauthenticated request with 203 and HTML rather than a 401.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
        {
            Content = new StringContent("<html><body>Sign in</body></html>", Encoding.UTF8, "text/html"),
        });

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetAsync<ListResponse<WireProject>>("_apis/projects", default));

        Assert.Equal("SignInPage", e.TypeKey);
        Assert.Contains("no access to this organization", e.Message);
    }

    [Fact]
    public async Task A_sign_in_page_on_a_text_fetch_is_an_error_rather_than_the_content()
    {
        // The same HTML answer as the JSON endpoints. Without the check it would be returned as if
        // it were the build log.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Sign in</body></html>", Encoding.UTF8, "text/html"),
        });

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetTextAsync("https://vsblob.dev.azure.com/contoso/logs/7", default));

        Assert.Equal("SignInPage", e.TypeKey);
    }

    [Fact]
    public async Task An_error_body_becomes_the_exception_message_and_type_key()
    {
        var client = Client(_ => Json(
            """{"message":"TF401019: The Git repository does not exist.","typeKey":"GitRepositoryNotFoundException"}""",
            HttpStatusCode.NotFound));

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetAsync<ListResponse<WireRepo>>("Core/_apis/git/repositories", default));

        Assert.Equal(404, e.Status);
        Assert.Equal("GitRepositoryNotFoundException", e.TypeKey);
        Assert.Contains("TF401019", e.Message);
        Assert.Equal("Core/_apis/git/repositories", e.Path);
    }

    [Fact]
    public async Task A_failure_with_no_parseable_body_still_reports_its_status()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(""),
        });

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetAsync<ListResponse<WireProject>>("_apis/projects", default));

        Assert.Equal(502, e.Status);
        Assert.Contains("502", e.Message);
    }

    [Fact]
    public async Task A_plain_text_failure_says_what_it_said()
    {
        // A path sent to the wrong host answers in plain text, and what it says is the whole
        // diagnosis: "Not Found (404)" would leave a caller looking for a definition that exists.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                "The controller for path '/_apis/release/definitions/31' was not found."),
        });

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetAsync<ListResponse<WireProject>>("_apis/release/definitions/31", default));

        Assert.Contains("The controller for path", e.Message);
    }

    [Fact]
    public async Task An_html_error_page_is_reduced_to_the_sentence_inside_it()
    {
        // The failure this exists for: an expired credential is answered with a whole page, and
        // the one line that says so arrives buried in markup several steps from the cause.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "<!DOCTYPE html><html><head><title>Azure DevOps Services</title>" +
                "<style>body { font-family: sans-serif; }</style>" +
                "<script>window.location='/signin';</script></head>" +
                "<body><div class=\"error\">Access Denied: The Personal Access Token used has " +
                "expired.</div></body></html>",
                Encoding.UTF8, "text/html"),
        });

        var e = await Assert.ThrowsAsync<AdoApiException>(
            () => client.GetAsync<ListResponse<WireProject>>("_apis/projects", default));

        Assert.Equal(401, e.Status);
        Assert.Equal("HtmlErrorPage", e.TypeKey);
        Assert.Contains("Personal Access Token used has expired", e.Message);
        // The stylesheet and the script are text too, and would otherwise be the first thing quoted.
        Assert.DoesNotContain("font-family", e.Message);
        Assert.DoesNotContain("window.location", e.Message);
    }

    [Fact]
    public async Task The_continuation_token_is_read_from_the_response_header()
    {
        var client = Client(_ =>
        {
            var response = Json("""{"count":0,"value":[]}""");
            response.Headers.TryAddWithoutValidation("x-ms-continuationtoken", "eyJ0b3AiOjEwMH0");
            return response;
        });

        var (_, token) = await client.GetPageAsync<ListResponse<WireProject>>("_apis/projects", default);

        Assert.Equal("eyJ0b3AiOjEwMH0", token);
    }

    [Fact]
    public async Task The_last_page_reports_no_continuation()
    {
        var client = Client(_ => Json("""{"count":0,"value":[]}"""));

        var (_, token) = await client.GetPageAsync<ListResponse<WireProject>>("_apis/projects", default);

        Assert.Null(token);
    }

    [Fact]
    public async Task A_post_sends_its_body_as_json()
    {
        string? sent = null;
        var client = Client(r =>
        {
            sent = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"workItems":[{"id":17}]}""");
        });

        var result = await client.PostAsync<WiqlResult>(
            "Core/_apis/wit/wiql", new { query = "SELECT [System.Id] FROM WorkItems" }, default);

        Assert.Equal("""{"query":"SELECT [System.Id] FROM WorkItems"}""", sent);
        Assert.Equal(17, Assert.Single(result.WorkItems!).Id);
    }

    [Fact]
    public async Task A_json_patch_goes_out_under_its_own_content_type()
    {
        string? sent = null;
        string? contentType = null;
        var client = Client(r =>
        {
            contentType = r.Content!.Headers.ContentType?.MediaType;
            sent = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"id":17,"fields":{"System.State":"Active"}}""");
        });

        var result = await client.PatchAsync<WireWorkItem>(
            HttpMethod.Patch, "_apis/wit/workitems/17",
            Writes.UpdatePatch("Active", null, null, null, null, null, null, null, null, null, null), default);

        // Azure DevOps rejects a patch document sent as plain application/json.
        Assert.Equal("application/json-patch+json", contentType);
        Assert.Equal("""[{"op":"add","path":"/fields/System.State","value":"Active"}]""", sent);
        Assert.Equal(17, result.Id);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
