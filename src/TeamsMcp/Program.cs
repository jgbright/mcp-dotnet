using System.CommandLine;
using System.CommandLine.Completions;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using TeamsMcp;

// The verbs parse through System.CommandLine: a mistyped verb is a loud parse error instead of a
// silently started stdio server that looks hung. Zero arguments is the server itself, run as the
// root command's own action — nothing on that path writes to stdout, which the transport owns.
var root = new RootCommand("MCP stdio server for Microsoft Teams, as the signed-in user.");

// `teams-mcp install [directory]` : find the repository around the working directory and register
// this server in the MCP client config it uses, preserving whatever else that file holds. Run
// this first in a new checkout, then `auth`.
//
// Install owns its parsing (`Install.Options`, which the tests drive); the declarations here
// mirror it so `--help` and unknown-option errors read like every other verb's, and the action
// hands the tokens straight back. A new install option means touching both.
var installDirectory = new Argument<string?>("directory")
{
    Arity = ArgumentArity.ZeroOrOne,
    Description = "Where to start looking for the repository (default: current directory).",
};
var installClient = new Option<string?>("--client")
{
    Description = "claude | vscode | cursor (default: whichever the repository shows signs of).",
};
var installFile = new Option<string?>("--file")
{
    Description = "Write this file instead of the client's own config path.",
};
var installName = new Option<string?>("--name")
{
    Description = "Key for this server in the config.",
};
var installSet = new Option<string[]>("--set")
{
    Description = "Add or override an env entry as KEY=VALUE; KEY= removes one; repeatable.",
};
var installForce = new Option<bool>("--force")
{
    Description = "Replace an existing entry that differs.",
};
var installDryRun = new Option<bool>("--dry-run")
{
    Description = "Print the resulting file, write nothing.",
};
var install = new Command("install",
    "Register this server in the MCP client config of the repository around the given directory.")
{
    installDirectory, installClient, installFile, installName, installSet, installForce, installDryRun,
};
install.SetAction(parseResult =>
{
    var raw = new List<string>();
    if (parseResult.GetValue(installDirectory) is { } directory) raw.Add(directory);
    if (parseResult.GetValue(installClient) is { } client) raw.AddRange(["--client", client]);
    if (parseResult.GetValue(installFile) is { } file) raw.AddRange(["--file", file]);
    if (parseResult.GetValue(installName) is { } name) raw.AddRange(["--name", name]);
    foreach (var pair in parseResult.GetValue(installSet) ?? []) raw.AddRange(["--set", pair]);
    if (parseResult.GetValue(installForce)) raw.Add("--force");
    if (parseResult.GetValue(installDryRun)) raw.Add("--dry-run");
    return Install.Run([.. raw]);
});
root.Subcommands.Add(install);

// `dotnet run -- auth` : interactive sign-in that primes the persisted token cache,
// so the MCP server never has to prompt over stdio.
var auth = new Command("auth",
    "Interactive sign-in that primes the persisted token cache; run once before the server.");
auth.SetAction(async (_, _) =>
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
});
root.Subcommands.Add(auth);

// `dotnet run -- selftest` : console-mode silent-auth + Graph round-trip with raw errors.
// This is the fastest way to tell an auth problem apart from a tool problem, and it writes to
// the same log file the server does.
var selftest = new Command("selftest",
    "Silent-auth and a Graph round-trip, with raw errors on the console.");
selftest.SetAction(async (_, _) =>
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
});
root.Subcommands.Add(selftest);

// `dotnet run -- call [tool] [arguments...]` : one shot of one tool without an MCP client on the
// other end. The server is the real one — the same host, silent auth, Run wrapper and filters as
// server mode — but transported over in-memory pipes, so stdout stays a console: the result JSON
// is the only thing written there, logging stays on stderr and the file, and a tool error exits
// non-zero. Bare `call` lists the tools.
var callTool = new Argument<string?>("tool")
{
    Arity = ArgumentArity.ZeroOrOne,
    Description = "The tool to invoke; omit to list the tools.",
};
callTool.CompletionSources.Add(_ => Call.ToolNames().Select(n => new CompletionItem(n)));
var callArguments = new Argument<string[]>("arguments")
{
    Arity = ArgumentArity.ZeroOrMore,
    Description = "KEY=VALUE pairs, one JSON object, or '-' to read a JSON object from stdin.",
};
var call = new Command("call",
    "Invoke one tool through the real server path and print its result as JSON.")
{
    callTool, callArguments,
};
call.SetAction(async (parseResult, cancellationToken) =>
{
    var toolName = parseResult.GetValue(callTool);
    var tokens = parseResult.GetValue(callArguments) ?? [];

    var toServer = new Pipe();
    var toClient = new Pipe();
    var host = BuildMcpHost(toServer.Reader.AsStream(), toClient.Writer.AsStream());
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    var log = loggerFactory.CreateLogger("call");
    Diagnostics.LogEnvironment(log, "call");
    await host.StartAsync(cancellationToken);
    try
    {
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), loggerFactory),
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);

        // The listing arrives sorted and is the authority a mistyped name is corrected against.
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        if (toolName is null)
        {
            foreach (var tool in tools)
            {
                Console.WriteLine(tool.Name);
            }
            return 0;
        }

        var resolved = Call.ResolveTool([.. tools.Select(t => t.Name)], toolName);
        if (resolved != toolName)
        {
            // The correction is worth a line, on the stream the result does not own.
            Console.Error.WriteLine($"tool: {resolved}");
        }
        var match = tools.First(t => t.Name == resolved);

        var arguments = Call.ParseArguments(match.ProtocolTool.InputSchema, tokens, Console.In.ReadToEnd);
        var result = await client.CallToolAsync(resolved, arguments, cancellationToken: cancellationToken);
        return Call.Render(result, Console.Out, Console.Error);
    }
    catch (FormatException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    catch (Exception e)
    {
        log.Line(LogLevel.Error, Ev.Crash, "call failed", e);
        Console.Error.WriteLine($"call failed: {e.GetType().Name}: {e.Message}");
        Console.Error.WriteLine($"Full detail: {TeamsMcpLog.FilePath}");
        return 1;
    }
    finally
    {
        await host.StopAsync(CancellationToken.None);
    }
});
root.Subcommands.Add(call);

// No verb: the MCP server on stdio, which is how an MCP client launches this process.
root.SetAction(async (_, cancellationToken) =>
{
    var host = BuildMcpHost(input: null, output: null);
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
        await host.RunAsync(cancellationToken);
        log.Line(LogLevel.Information, Ev.Shutdown, "stopped");
        return 0;
    }
    catch (Exception e)
    {
        log.Line(LogLevel.Critical, Ev.Crash, "host terminated", e);
        return 1;
    }
});

return await root.Parse(args).InvokeAsync();

// The one MCP host, parameterized only by transport: no verb means stdio (an MCP client is on the
// other end of this process), `call` means in-memory pipes (this process's own client is).
// Everything else — the sinks, the serializer, the instructions, the tasks extension, the filters —
// is identical on both paths, which is what makes a `call` result mean something about server mode.
IHost BuildMcpHost(Stream? input, Stream? output)
{
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

        The sending and reaction tools (send_channel_message, send_chat_message,
        react_to_chat_message, react_to_channel_message) refuse unless TEAMS_MCP_ALLOW_SEND=true in
        this server's environment. That refusal is configuration and will not change on retry —
        report it and stop.

        Every error carries a req=N and the path of this server's log file. Quote both when reporting a
        failure; they are what makes it diagnosable.
        """;

    var mcp = builder.Services
        // The SDK advertises the MCP `logging` capability unconditionally and McpServerOptions cannot
        // switch it off. This server never emits notifications/message. It logs to stderr and its own
        // file, which is the 2026-07-28 migration path off the deprecated logging feature, so the
        // advertisement overstates what a client gets.
        .AddMcpServer(options => options.ServerInstructions = instructions);

    mcp = input is not null && output is not null
        ? mcp.WithStreamServerTransport(input, output)
        : mcp.WithStdioServerTransport();

    mcp
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

    return builder.Build();
}
