using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDevOpsMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;

// `ado-mcp install [directory]` : find the repository around the working directory and register
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
    using var authLoggerFactory = AdoMcpLog.CreateFactory();
    var authLog = authLoggerFactory.CreateLogger("auth");
    Console.WriteLine($"Logging to {AdoMcpLog.FilePath}");
    try
    {
        await AdoContext.AuthenticateInteractiveAsync(authLog);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Sign-in failed: {e.Message}");
        Console.Error.WriteLine($"Full detail: {AdoMcpLog.FilePath}");
        return 1;
    }
    return 0;
}

// `dotnet run -- selftest` : console-mode silent-auth + REST round-trip with raw errors.
// This is the fastest way to tell an auth problem apart from a tool problem, and it writes to
// the same log file the server does.
if (args.Length > 0 && string.Equals(args[0], "selftest", StringComparison.OrdinalIgnoreCase))
{
    using var selfTestLoggerFactory = AdoMcpLog.CreateFactory();
    Console.WriteLine($"Logging to {AdoMcpLog.FilePath}");
    Diagnostics.LogEnvironment(selfTestLoggerFactory.CreateLogger("selftest"), "selftest");
    try
    {
        var ctx = new AdoContext(selfTestLoggerFactory.CreateLogger<AdoContext>());
        var client = await ctx.GetClientAsync();
        Console.WriteLine($"org: {client.OrgUrl}");
        // connectionData is a preview-only resource: it rejects a bare 7.1 with
        // VssInvalidPreviewVersionException, unlike every endpoint the tools use.
        var me = await client.GetAsync<WireConnectionData>(
            "_apis/connectionData?api-version=7.1-preview", default);
        Console.WriteLine(
            $"me: {me.AuthenticatedUser?.DisplayName ?? me.AuthenticatedUser?.ProviderDisplayName}");
        var projects = await client.GetAsync<ListResponse<WireProject>>(
            "_apis/projects?api-version=7.1&$top=10", default);
        foreach (var p in projects.Value ?? [])
        {
            Console.WriteLine($"project: {p.Id} {p.Name}");
        }
        Console.WriteLine("selftest ok");
        return 0;
    }
    catch (Exception e)
    {
        selfTestLoggerFactory.CreateLogger("selftest").Line(LogLevel.Error, Ev.Crash, "selftest failed", e);
        Console.Error.WriteLine($"selftest failed: {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine($"Full detail: {AdoMcpLog.FilePath}");
        return 1;
    }
}

// `dotnet run -- config` : load every data file the server would use and show what each says, so
// an edit to the data can be checked without driving the tools through an MCP client. A missing
// file is a note (the feature is opt-in). An invalid one is the failure this exists to catch.
if (args.Length > 0 && string.Equals(args[0], "config", StringComparison.OrdinalIgnoreCase))
{
    using var configLoggerFactory = AdoMcpLog.CreateFactory();
    var configLog = configLoggerFactory.CreateLogger("config");
    var invalid = false;

    Console.WriteLine($"deployment map: {Deployments.ConfiguredPath}");
    if (!File.Exists(Deployments.ConfiguredPath))
    {
        Console.WriteLine("  not configured (file does not exist)");
    }
    else
    {
        try
        {
            var deployables = Deployments.Get(configLog);
            foreach (var d in deployables)
            {
                Console.WriteLine(d.Pipeline is not null
                    ? $"  {d.Name}: pipeline '{d.Pipeline}'" +
                      (d.Environment is null ? "" : $" -> environment '{d.Environment}'") +
                      (d.Branch is null ? "" : $" branch '{d.Branch}'") +
                      (d.Paths is null ? "" : $" paths={d.Paths.Count}")
                    : $"  {d.Name}: '{d.ReleaseDefinition}' -> '{d.Environment}'" +
                      (d.Paths is null ? " (paths from build definition)" : $" paths={d.Paths.Count}"));
            }
            Console.WriteLine($"  {deployables.Count} deployable(s) ok");
        }
        catch (Exception e)
        {
            invalid = true;
            Console.Error.WriteLine($"  {e.Message}");
        }
    }
    return invalid ? 1 : 0;
}

var builder = Host.CreateApplicationBuilder(args);

// The stdio transport owns stdout, so all logging goes to stderr and to the log file. The file
// is what survives: MCP clients routinely discard a server's stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(AdoMcpLog.Level);
builder.Logging.AddProvider(new CompactLoggerProvider(new FileLineSink(AdoMcpLog.FilePath), AdoMcpLog.Level));
builder.Logging.AddProvider(new CompactLoggerProvider(new StderrLineSink(), AdoMcpLog.Level));

builder.Services.AddSingleton<AdoContext>();

// Omit null fields from tool results, since every serialized byte lands in a model's context.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// The instructions are sized like a system prompt. They carry only what a tool description
// cannot: how this server fails and what its silences mean.
const string instructions = """
    Reads and writes one Azure DevOps organization, fixed by this server's environment.

    Ids and names are interchangeable wherever a project, repository or pipeline is named: an
    ambiguous or unknown name fails with the candidates listed, so re-call with one of them rather
    than guessing. Project defaults to the server's configured project when omitted.

    Absent fields are absent on purpose — results omit anything null or uninteresting, so a missing
    field means "nothing to say", not "unknown". `skipped` counts records that were filtered out;
    no `skipped` and no results means nothing matched. `hasMore` means the limit was reached, not
    that the query was wrong.

    The write tools (create_work_item, update_work_item, add_pull_request_comment) refuse unless
    ADO_MCP_ALLOW_WRITE=true in this server's environment. That refusal is configuration and will
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
    // Tasks (SEP-2663) exists here for one tool. wait_for_pipeline_run can run for half an hour,
    // which is too long to hold a request open when the client can poll instead.
    //
    // Every other tool stays Synchronous: they answer in a round trip or two, and turning a
    // sub-second call into a task handle the caller has to chase makes it worse. The waiter is
    // Optional rather than Required, so a client that never negotiated the extension still gets
    // an answer by blocking. That is why the tool bounds its own wait instead of relying on the
    // client to give up.
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
