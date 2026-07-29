using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using TeamsMcp;

// `teams-mcp install [directory]` : find the repository around the working directory and register
// this server in the MCP client config it uses, preserving whatever else that file holds. Run
// this first in a new checkout, then `auth`.
if (args.Length > 0 && string.Equals(args[0], "install", StringComparison.OrdinalIgnoreCase))
{
    return Install.Run(args[1..]);
}

// `dotnet run -- auth` : interactive sign-in that primes the persisted token cache,
// so the MCP server never has to prompt over stdio.
if (args.Length > 0 && string.Equals(args[0], "auth", StringComparison.OrdinalIgnoreCase))
{
    using var authLoggerFactory = TeamsMcpLog.CreateFactory();
    var authLog = authLoggerFactory.CreateLogger("auth");
    Console.WriteLine($"Logging to {TeamsMcpLog.FilePath}");
    try
    {
        await GraphContext.AuthenticateInteractiveAsync(authLog);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Sign-in failed: {e.Message}");
        Console.Error.WriteLine($"Full detail: {TeamsMcpLog.FilePath}");
        return 1;
    }
    return 0;
}

// `dotnet run -- selftest` : console-mode silent-auth + Graph round-trip with raw errors.
// This is the fastest way to tell an auth problem apart from a tool problem, and it writes to
// the same log file the server does.
if (args.Length > 0 && string.Equals(args[0], "selftest", StringComparison.OrdinalIgnoreCase))
{
    using var selfTestLoggerFactory = TeamsMcpLog.CreateFactory();
    Console.WriteLine($"Logging to {TeamsMcpLog.FilePath}");
    Diagnostics.LogEnvironment(selfTestLoggerFactory.CreateLogger("selftest"), "selftest");
    try
    {
        var ctx = new GraphContext(selfTestLoggerFactory.CreateLogger<GraphContext>());
        var client = await ctx.GetClientAsync();
        // Printed after the client is built, so this is the scope set of a token that was
        // actually acquired.
        Console.WriteLine($"scopes requested: {string.Join(" ", GraphContext.Scopes)}");
        Console.WriteLine($"scopes in token:  {string.Join(" ", ScopeConsent.Read() ?? ["<unknown>"])}");
        var me = await client.Me.GetAsync();
        Console.WriteLine($"me: {me?.DisplayName} <{me?.Mail ?? me?.UserPrincipalName}>");
        var teams = await client.Me.JoinedTeams.GetAsync();
        foreach (var t in teams?.Value ?? [])
        {
            Console.WriteLine($"team: {t.Id} {t.DisplayName}");
        }
        Console.WriteLine("selftest ok");
        return 0;
    }
    catch (Exception e)
    {
        selfTestLoggerFactory.CreateLogger("selftest").Line(LogLevel.Error, Ev.Crash, "selftest failed", e);
        Console.Error.WriteLine($"selftest failed: {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine($"Full detail: {TeamsMcpLog.FilePath}");
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// The stdio transport owns stdout, so all logging goes to stderr and to the log file. The file
// is what survives: MCP clients routinely discard a server's stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(TeamsMcpLog.Level);
builder.Logging.AddProvider(new CompactLoggerProvider(new FileLineSink(TeamsMcpLog.FilePath), TeamsMcpLog.Level));
builder.Logging.AddProvider(new CompactLoggerProvider(new StderrLineSink(), TeamsMcpLog.Level));

builder.Services.AddSingleton<GraphContext>();

// Omit null fields from tool results, since every serialized byte lands in a model's context.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// The instructions are sized like a system prompt. They carry only what a tool description
// cannot: how this server fails and what its silences mean.
const string instructions = """
    Reads Microsoft Teams conversations, and sends messages, as the signed-in user.

    Teams and channels are named or given by id, whichever the caller has: an ambiguous or unknown
    name fails with the candidates listed, so re-call with one of them rather than guessing.

    Message bodies arrive as plain text, not HTML — links are kept as "text (url)". A body cut at
    `body_limit` is marked `truncated`; raise the limit and re-read rather than inferring the rest.

    The search tools (search_messages, list_mentions, and the two waiters over them) read an index,
    not the conversations: they reach every chat and channel at once, but a hit trails what was just
    said by seconds or longer and carries a summary rather than a body. Treat one as an address — read
    the conversation it names for the text, and do not conclude "nothing was said" from a search
    that came back empty seconds after the fact.

    Absent fields are absent on purpose — results omit anything null or uninteresting, so a missing
    field means "nothing to say", not "unknown". `skipped` counts system events and deleted
    messages that were filtered out; no `skipped` and no results means nothing matched.

    A watch is a loop of waiter calls, not a single call: each one returns a `nextCursor` that the
    next one passes back as `cursor`, and that round trip is the whole of resuming. Do not
    reconstruct a cursor from a timestamp you saw in a result — the boundary is inclusive, so that
    re-delivers the last message every time. A wait that timed out still returns a cursor and is
    still the right thing to call again.

    The sending tools (send_channel_message, send_chat_message) refuse unless
    TEAMS_MCP_ALLOW_SEND=true in this server's environment. That refusal is configuration and will
    not change on retry — report it and stop.

    Every error carries a req=N and the path of this server's log file. Quote both when reporting a
    failure; they are what makes it diagnosable.
    """;

builder.Services
    // The SDK advertises the MCP `logging` capability unconditionally and McpServerOptions cannot
    // switch it off. This server never emits notifications/message. It logs to stderr and its own
    // file, which is the 2026-07-28 migration path off the deprecated logging feature, so the
    // advertisement overstates what a client gets.
    .AddMcpServer(options => options.ServerInstructions = instructions)
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: serializerOptions)
    // Tasks (SEP-2663) exists here for the waiters, which can sit on a conversation for an hour.
    // That is too long to hold a request open when the client can poll instead.
    //
    // Every other tool stays Synchronous: they answer in a round trip or two, and turning a
    // sub-second call into a task handle the caller has to chase makes it worse. The waiters are
    // Optional rather than Required, so a client that never negotiated the extension still gets
    // an answer by blocking. That is why each waiter bounds its own wait instead of relying on
    // the client to give up.
    .WithTasks(
        new InMemoryMcpTaskStore(),
        options => options.ExecutionModeSelector = request =>
            ToolExecution.IsLongRunning(request.MatchedPrimitive)
                ? McpTaskExecutionMode.Optional
                : McpTaskExecutionMode.Synchronous)
    // Both filters only trim what the SDK produced: tools/list is a cacheable result under
    // 2026-07-28 and needs the caching hints stamped on it, and a tool result arrives carrying
    // its payload twice once UseStructuredContent is on.
    .WithRequestFilters(filters => filters
        .AddListToolsFilter(next => async (request, ct) =>
            ToolListing.Stamp(await next(request, ct), request.Params?.Cursor))
        .AddCallToolFilter(next => async (request, ct) =>
            ToolResults.Trim(await next(request, ct))));

var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("server");

Diagnostics.LogEnvironment(log, "server");

// A crash in the transport or a background task otherwise leaves no trace at all.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.Line(LogLevel.Critical, Ev.Crash, "unhandled exception", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    log.Line(LogLevel.Error, Ev.Crash, "unobserved task exception", e.Exception);
    e.SetObserved();
};

try
{
    await host.RunAsync();
    log.Line(LogLevel.Information, Ev.Shutdown, "stopped");
    return 0;
}
catch (Exception e)
{
    log.Line(LogLevel.Critical, Ev.Crash, "host terminated", e);
    return 1;
}
