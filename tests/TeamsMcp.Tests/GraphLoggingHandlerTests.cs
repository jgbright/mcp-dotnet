using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace TeamsMcp.Tests;

/// <summary>
/// The handler answers "which Graph call failed and why": it logs the ids Microsoft support asks
/// for, and puts the error body back so the SDK can still parse it into an ODataError.
/// </summary>
public class GraphLoggingHandlerTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;

    public GraphLoggingHandlerTests() => _factory = TestLog.Factory(_sink);

    public void Dispose() => _factory.Dispose();

    private HttpMessageInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new GraphLoggingHandler(_factory.CreateLogger("GraphContext")) { InnerHandler = new StubHandler(respond) });

    [Fact]
    public async Task Successful_calls_are_logged_at_debug_with_method_path_and_status()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(Request("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);

        Assert.Contains(" DBG ", _sink.Last);
        Assert.Contains(" graph.http ", _sink.Last);
        Assert.Contains("GET /v1.0/me -> 200", _sink.Last);
        Assert.Contains("ms=", _sink.Last);
    }

    [Fact]
    public async Task The_host_is_stripped_but_the_query_is_kept()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(
            Request("https://graph.microsoft.com/v1.0/me/chats?$top=50&$expand=members"),
            CancellationToken.None);

        Assert.Contains("/v1.0/me/chats?$top=50&$expand=members", _sink.Last);
        Assert.DoesNotContain("graph.microsoft.com", _sink.Last);
    }

    [Fact]
    public async Task Failures_log_the_support_ids_the_retry_after_and_graphs_own_error_body()
    {
        const string body = """{"error":{"code":"Forbidden","message":"Missing scope"}}""";
        using var invoker = Invoker(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body),
            };
            response.Headers.TryAddWithoutValidation("request-id", "298a99a3-1111-2222-3333-444455556666");
            response.Headers.TryAddWithoutValidation("client-request-id", "aaaa1111-bbbb-cccc-dddd-eeee22223333");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        await invoker.SendAsync(Request("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);

        Assert.Contains(" WRN ", _sink.Last);
        Assert.Contains(" graph.http.fail ", _sink.Last);
        Assert.Contains("-> 429", _sink.Last);
        Assert.Contains("request-id=\"298a99a3-1111-2222-3333-444455556666\"", _sink.Last);
        Assert.Contains("client-request-id=\"aaaa1111-bbbb-cccc-dddd-eeee22223333\"", _sink.Last);
        Assert.Contains("retry-after=\"42\"", _sink.Last);
        Assert.Contains("Missing scope", _sink.Last);
    }

    [Fact]
    public async Task The_error_body_is_still_readable_by_the_sdk_after_being_logged()
    {
        const string body = """{"error":{"code":"Forbidden","message":"Missing scope"}}""";
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

        var response = await invoker.SendAsync(
            Request("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);

        // Buffering the body for the log must not consume it. ODataError parsing happens downstream.
        Assert.Equal(body, await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Missing_headers_are_omitted_rather_than_logged_as_empty()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await invoker.SendAsync(Request("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);

        Assert.DoesNotContain("request-id=", _sink.Last);
        Assert.DoesNotContain("retry-after=", _sink.Last);
    }

    [Fact]
    public async Task Transport_failures_are_logged_and_rethrown()
    {
        using var invoker = Invoker(_ => throw new HttpRequestException("no such host"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(Request("https://graph.microsoft.com/v1.0/me"), CancellationToken.None));

        Assert.Contains("transport failure", _sink.Last);
        Assert.Contains("!! System.Net.Http.HttpRequestException: no such host", _sink.Last);
    }

    [Fact]
    public async Task Post_requests_are_logged_with_their_method()
    {
        using var invoker = Invoker(_ => new HttpResponseMessage(HttpStatusCode.Created));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/chats/x/messages");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("POST /v1.0/chats/x/messages -> 201", _sink.Last);
    }

    private static HttpRequestMessage Request(string url) => new(HttpMethod.Get, url);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
