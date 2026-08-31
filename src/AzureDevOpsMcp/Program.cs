using System.CommandLine;
using System.CommandLine.Completions;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDevOpsMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Verbs parse through System.CommandLine, so a mistyped verb is a loud parse error rather than a
// silently started stdio server that looks hung. Zero arguments is the server itself, run as the
// root command's own action. Nothing on that path writes to stdout; the transport owns it.
var root = new RootCommand("MCP stdio server for one Azure DevOps organization.");

// `ado-mcp install [directory]` : find the repository around the working directory and register
// this server in the MCP client config it uses, keeping whatever else that file holds. Run it
// first in a new checkout, then `auth`.
//
// Install owns its parsing (`Install.Options`, which the tests drive). The declarations here
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
});
root.Subcommands.Add(auth);

// `dotnet run -- selftest` : console-mode silent-auth + REST round-trip with raw errors. The
// fastest way to tell an auth problem from a tool problem; it writes to the log file the server
// uses.
var selftest = new Command("selftest",
    "Silent-auth and a REST round-trip, with raw errors on the console.");
selftest.SetAction(async (_, _) =>
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
});
root.Subcommands.Add(selftest);

// `dotnet run -- config` : load every data file the server would use and show what each says, so
// a data edit can be checked without driving the tools through an MCP client. A missing file is a
// note, since the feature is opt-in; an invalid one is the failure this catches.
var config = new Command("config",
    "Validate and print the server's data files (deployment map).");
config.SetAction(parseResult =>
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
});
root.Subcommands.Add(config);

// `dotnet run -- call [tool] [arguments...]` : one shot of one tool with no MCP client on the
// other end. The server is the real one, same host and silent auth and Run wrapper and filters as
// server mode, but transported over in-memory pipes, so stdout stays a console: the result JSON is
// all that goes there, logging stays on stderr and the file, and a tool error exits non-zero. Bare
// `call` lists the tools.
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
            // Print the correction on stderr, which the result does not own.
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
        Console.Error.WriteLine($"Full detail: {AdoMcpLog.FilePath}");
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

// The one MCP host, parameterized only by transport: no verb means stdio, with an MCP client on
// the other end of this process; `call` means in-memory pipes to this process's own client.
// Everything else (sinks, serializer, instructions, tasks extension, filters) is identical on both
// paths, so a `call` result says something about server mode.
IHost BuildMcpHost(Stream? input, Stream? output)
{
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

    // Sized like a system prompt. Only what a tool description cannot carry: how this server fails
    // and what its silences mean.
    const string instructions = """
        Reads and writes one Azure DevOps organization, fixed by this server's environment.

        Ids and names are interchangeable wherever a project, repository or pipeline is named: an
        ambiguous or unknown name fails with the candidates listed, so re-call with one of them rather
        than guessing. Project defaults to the server's configured project when omitted.

        Absent fields are absent on purpose — results omit anything null or uninteresting, so a missing
        field means "nothing to say", not "unknown". `skipped` counts records that were filtered out;
        no `skipped` and no results means nothing matched. `hasMore` means the limit was reached, not
        that the query was wrong.

        Azure DevOps has two unrelated kinds of pipeline and this server keeps them apart by name. The
        *_pipeline* tools mean build/YAML pipelines and their runs. The *_release* tools mean classic
        release pipelines: a release definition has environments (stages), a release is one instance of
        it, and each environment deploys separately. A release definition never appears in
        list_pipelines, so "not found" there is not evidence it does not exist.

        A release is history and a release definition is configuration, and only the second one says
        what a deploy is set up to do — which variables it overrides and which files its tasks
        rewrite. get_release_definition reads one, search_release_definitions finds either across the
        project. No tool returns a value Azure DevOps marks secret; the name and `isSecret` are the
        whole answer, and asking again another way will not produce it.

        Where a stage lands is configuration too, and a stage's name is a label rather than evidence
        of it. get_release_definition_targets resolves each stage to the machines its deployment
        group and tags select now, and is the answer to "which servers does this touch";
        deployment_status says what version is out, not where. Deployment groups are not the
        Environments of YAML pipelines — the two share a word and nothing else.

        When no tool covers what is needed, ado_api_request calls the REST API with this server's own
        credential rather than sending you looking for a personal access token, which is how a
        session ends up debugging a second, staler credential instead of the question it started
        with. ado_auth_status says whether this server's credential still works, and reports a dead
        one as an answer rather than a failure.

        The write tools (create_work_item, update_work_item, add_pull_request_comment, run_pipeline,
        deploy_release, approve_release) refuse unless ADO_MCP_ALLOW_WRITE=true in this server's
        environment, and approve_release needs ADO_MCP_ALLOW_APPROVE=true as well. Those refusals are
        configuration and will not change on retry — report it and stop.

        Every error a tool call returns carries a req=N and the path of this server's log file. Quote
        both when reporting a failure; they are what makes it diagnosable. A protocol-level refusal —
        an unknown tool or method name — carries neither, because it never reached a tool.

        An error naming an argument the tool does not take has been rejected before the tool ran, so
        nothing happened: read the parameter list it gives you and call again. Parameter names are
        snake_case.
        """;

    var mcp = builder.Services
        // The SDK advertises the MCP `logging` capability unconditionally and McpServerOptions
        // cannot switch it off, so the advertisement overstates what a client gets. This server
        // never emits notifications/message; it logs to stderr and its own file, which is the
        // 2026-07-28 migration path off the deprecated logging feature.
        .AddMcpServer(options => options.ServerInstructions = instructions);

    mcp = input is not null && output is not null
        ? mcp.WithStreamServerTransport(input, output)
        : mcp.WithStdioServerTransport();

    mcp
        .WithToolsFromAssembly(serializerOptions: serializerOptions)
        // Tasks (SEP-2663) is here for the waiters: wait_for_pipeline_run, wait_for_pull_request
        // and wait_for_release can run for half an hour, too long to hold a request open when the
        // client can poll instead. Every other tool stays Synchronous, since turning a sub-second call into a task
        // handle the caller has to chase makes it worse. Optional rather than Required means a
        // client that never negotiated the extension still gets an answer by blocking, so each
        // waiter bounds its own wait instead of relying on the client to give up.
        .WithTasks(
            new InMemoryMcpTaskStore(),
            options => options.ExecutionModeSelector = request =>
                ToolExecution.IsLongRunning(request.MatchedPrimitive)
                    ? McpTaskExecutionMode.Optional
                    : McpTaskExecutionMode.Synchronous)
        // The list filter stamps the caching hints tools/list needs under 2026-07-28 onto the
        // listing the SDK produced. The call filter drops the duplicate payload
        // UseStructuredContent produces, and is also the only place that sees a call which failed
        // before the tool body was entered: Run() is inside the body, and the SDK drops the detail
        // one frame above here. ToolErrors.Guard gives such a failure the tool name and a req=N.
        .WithRequestFilters(filters => filters
            .AddListToolsFilter(next => async (request, ct) =>
                ToolListing.Prepare(await next(request, ct), request.Params?.Cursor))
            .AddCallToolFilter(next => async (request, ct) =>
                ToolResults.Trim(await ToolErrors.Guard(
                    () => next(request, ct),
                    request.Params?.Name,
                    request.Params?.Arguments?.Keys,
                    (request.MatchedPrimitive as McpServerTool)?.ProtocolTool.InputSchema,
                    request.Services?.GetService<ILoggerFactory>()?.CreateLogger<AdoTools>()))));

    return builder.Build();
}
