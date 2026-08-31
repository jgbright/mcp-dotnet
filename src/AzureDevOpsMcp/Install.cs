using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureDevOpsMcp;

/// <summary>
/// `ado-mcp install` registers this server in a repository's MCP client configuration, so setting
/// up a checkout is a command instead of a paste from the README.
///
/// It walks up from the working directory to the nearest <c>.git</c> and writes relative to that
/// root, where an MCP client's config lives. Which client is decided from marker files already in
/// the repository (see <see cref="Clients"/>). Clients differ only in the property holding the
/// servers and how an environment variable is referenced, so they are data, not code paths.
///
/// Identity is referenced, never copied: tenant and client ids go in as
/// <c>${ADO_MCP_TENANT_ID}</c> and resolve from the environment at launch, because an app
/// registration belongs to whoever runs the server and this file usually ends up committed. The
/// organization URL and default project are addresses rather than credentials, so they are
/// written literally. Pinning them to the repository is the point of a per-repository install.
///
/// Existing content is preserved: other servers, other top-level properties, and an entry that
/// already differs. Replacing that one needs <c>--force</c>, so an install cannot quietly undo a
/// hand edit.
/// </summary>
internal static class Install
{
    /// <summary>The name this server is launched by once installed as a .NET tool.</summary>
    internal const string ToolCommand = "ado-mcp";

    /// <summary>Default key for the server inside the config's servers object.</summary>
    internal const string DefaultName = "azuredevops";

    internal static readonly string Usage = $"""
        Usage: {ToolCommand} install [directory] [options]

          directory           where to start looking for the repository (default: current directory)
          --client <name>     claude | vscode | cursor (default: whichever the repository shows signs of)
          --file <path>       write this file instead of the client's own config path
          --name <key>        key for this server in the config (default: {DefaultName})
          --set KEY=VALUE     add or override an env entry; KEY= removes one; repeatable
          --force             replace an existing entry that differs
          --dry-run           print the resulting file, write nothing
        """;

    // ------------------------------------------------------------------------ clients

    /// <summary>
    /// One MCP client as far as installing is concerned: where its per-repository config lives,
    /// which property holds the servers, how it spells an environment reference, and what marks a
    /// repository as using it.
    /// </summary>
    internal sealed record Client(
        string Name, string ConfigFile, string ServersProperty, string EnvRefPrefix, string[] Markers)
    {
        /// <summary>A reference the client resolves at launch, e.g. <c>${ADO_MCP_TENANT_ID}</c>.</summary>
        internal string Ref(string variable) => $"${{{EnvRefPrefix}{variable}}}";

        internal string PathIn(string root) =>
            Path.Combine(root, ConfigFile.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static readonly Client ClaudeCode = new(
        "claude", ".mcp.json", "mcpServers", "",
        [".mcp.json", ".claude", "CLAUDE.md"]);

    internal static readonly Client VsCode = new(
        "vscode", ".vscode/mcp.json", "servers", "env:",
        [".vscode/mcp.json", ".github/copilot-instructions.md"]);

    internal static readonly Client Cursor = new(
        "cursor", ".cursor/mcp.json", "mcpServers", "",
        [".cursor/mcp.json", ".cursorrules"]);

    /// <summary>Also the preference order when a repository shows signs of more than one.</summary>
    internal static readonly Client[] Clients = [ClaudeCode, VsCode, Cursor];

    /// <summary>Which clients this repository shows signs of, in preference order.</summary>
    internal static List<Client> Detect(string root) =>
        [.. Clients.Where(c => c.Markers.Any(m => Exists(root, m)))];

    private static bool Exists(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// Nearest ancestor holding a <c>.git</c> (a directory normally, a file in a worktree or
    /// submodule). Walking beats shelling out to git: no git needed on PATH, no process.
    /// </summary>
    internal static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------- the registration

    /// <summary>
    /// How the client should start this server, derived from how this process was started: an
    /// installed tool registers itself by command name, a checkout registers the `dotnet run` that
    /// reaches the same code.
    /// </summary>
    internal static (string Command, List<string> Args) LaunchCommand(string? processPath, string? projectDir)
    {
        if (processPath is { Length: > 0 } &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), ToolCommand, StringComparison.OrdinalIgnoreCase))
        {
            return (ToolCommand, []);
        }
        if (projectDir is { Length: > 0 })
        {
            return ("dotnet", ["run", "--project", projectDir]);
        }
        // Neither a tool nor a checkout, a published folder say. The absolute path still works.
        return (processPath is { Length: > 0 } ? processPath : ToolCommand, []);
    }

    /// <summary>The project this build came from: up from bin/{config}/{tfm} to the .csproj.</summary>
    internal static string? FindProjectDir(string baseDirectory)
    {
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.csproj").Any())
            {
                return dir.FullName;
            }
        }
        return null;
    }

    /// <summary>
    /// The env block: identity by reference, addresses by value, then whatever <c>--set</c> says.
    /// The entries are an allow-list, not a copy of the environment: neither ADO_MCP_ALLOW_WRITE
    /// nor ADO_MCP_ALLOW_APPROVE is ever written. Both gates are opt-in per environment, and this
    /// file usually ends up committed, so an install must not switch either on for whoever clones
    /// the repository.
    /// </summary>
    internal static List<KeyValuePair<string, string>> EnvEntries(
        Client client, IEnumerable<KeyValuePair<string, string>> overrides, Func<string, string?> env)
    {
        List<KeyValuePair<string, string>> entries = [];

        void Set(string name, string? value)
        {
            var at = entries.FindIndex(e => e.Key == name);
            if (value is null)
            {
                if (at >= 0)
                {
                    entries.RemoveAt(at);
                }
                return;
            }
            if (at >= 0)
            {
                entries[at] = new(name, value);
            }
            else
            {
                entries.Add(new(name, value));
            }
        }

        Set("ADO_MCP_TENANT_ID", client.Ref("ADO_MCP_TENANT_ID"));
        Set("ADO_MCP_CLIENT_ID", client.Ref("ADO_MCP_CLIENT_ID"));
        Set("ADO_MCP_ORG_URL", env("ADO_MCP_ORG_URL") is { Length: > 0 } url
            ? AdoContext.NormalizeOrgUrl(url)
            : client.Ref("ADO_MCP_ORG_URL"));
        if (env("ADO_MCP_PROJECT") is { Length: > 0 } project)
        {
            Set("ADO_MCP_PROJECT", project);
        }

        foreach (var (key, value) in overrides)
        {
            Set(key, value.Length == 0 ? null : value);
        }
        return entries;
    }

    /// <summary>The variable an entry defers to, or null when the value is literal.</summary>
    internal static string? ReferencedVariable(string value)
    {
        if (!value.StartsWith("${", StringComparison.Ordinal) || !value.EndsWith('}'))
        {
            return null;
        }
        var inner = value[2..^1];
        if (inner.StartsWith("env:", StringComparison.Ordinal))
        {
            inner = inner[4..];
        }
        return inner.Length > 0 && !inner.Contains('$') ? inner : null;
    }

    internal static JsonObject Entry(
        string command, IReadOnlyList<string> args, IEnumerable<KeyValuePair<string, string>> env)
    {
        var entry = new JsonObject { ["type"] = "stdio", ["command"] = command };
        if (args.Count > 0)
        {
            entry["args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a))]);
        }
        var block = new JsonObject();
        foreach (var (key, value) in env)
        {
            block[key] = value;
        }
        if (block.Count > 0)
        {
            entry["env"] = block;
        }
        return entry;
    }

    // -------------------------------------------------------------------------- merging

    internal enum Outcome
    {
        Added,
        Replaced,
        Unchanged,
        /// <summary>An entry under this name exists and differs. Only --force gets past it.</summary>
        Conflict,
    }

    /// <summary>
    /// Merges the entry into the existing document, preserving every other server and every other
    /// top-level property. Comments and formatting do not survive the reserialize.
    /// </summary>
    internal static (JsonObject Root, Outcome Outcome, JsonNode? Existing) Merge(
        string? existingText, string serversProperty, string name, JsonObject entry, bool force)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingText))
        {
            root = [];
        }
        else
        {
            var parsed = JsonNode.Parse(existingText, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            root = parsed as JsonObject
                ?? throw new FormatException("its top level is not a JSON object");
        }

        if (root[serversProperty] is not JsonObject servers)
        {
            if (root[serversProperty] is not null)
            {
                throw new FormatException($"'{serversProperty}' is present but is not a JSON object");
            }
            servers = [];
            root[serversProperty] = servers;
        }

        if (servers[name] is { } existing)
        {
            if (JsonNode.DeepEquals(existing, entry))
            {
                return (root, Outcome.Unchanged, existing);
            }
            if (!force)
            {
                return (root, Outcome.Conflict, existing);
            }
            servers[name] = entry;
            return (root, Outcome.Replaced, existing);
        }

        servers[name] = entry;
        return (root, Outcome.Added, null);
    }

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // A config file, not a web page: an org URL or a project name should read as itself rather
        // than as escape sequences.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Serialize(JsonObject root) => root.ToJsonString(Format) + Environment.NewLine;

    // --------------------------------------------------------------------------- options

    internal sealed class Options
    {
        internal string? Directory { get; private set; }
        internal Client? Client { get; private set; }
        internal string? File { get; private set; }
        internal string Name { get; private set; } = DefaultName;
        internal List<KeyValuePair<string, string>> Set { get; } = [];
        internal bool Force { get; private set; }
        internal bool DryRun { get; private set; }

        /// <summary>Throws <see cref="FormatException"/> with a message meant for the terminal.</summary>
        internal static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                string Value() => i + 1 < args.Length
                    ? args[++i]
                    : throw new FormatException($"{arg} needs a value.");

                switch (arg)
                {
                    case "--client":
                        var name = Value();
                        options.Client = Clients.FirstOrDefault(c =>
                                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new FormatException(
                                $"Unknown client '{name}'. Known: {string.Join(", ", Clients.Select(c => c.Name))}.");
                        break;
                    case "--file":
                        options.File = Value();
                        break;
                    case "--name":
                        options.Name = Value();
                        break;
                    case "--set":
                        var pair = Value();
                        var eq = pair.IndexOf('=');
                        if (eq <= 0)
                        {
                            throw new FormatException($"--set expects KEY=VALUE, got '{pair}'.");
                        }
                        options.Set.Add(new(pair[..eq], pair[(eq + 1)..]));
                        break;
                    case "--force":
                        options.Force = true;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    default:
                        if (arg.StartsWith('-'))
                        {
                            throw new FormatException($"Unknown option '{arg}'.");
                        }
                        if (options.Directory is not null)
                        {
                            throw new FormatException(
                                $"Only one directory can be given; got '{options.Directory}' and '{arg}'.");
                        }
                        options.Directory = arg;
                        break;
                }
            }
            return options;
        }
    }

    // ------------------------------------------------------------------------------ run

    /// <summary>Console-mode verb: returns the process exit code and reports on stdout/stderr.</summary>
    internal static int Run(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var start = options.Directory ?? Environment.CurrentDirectory;
        if (!Directory.Exists(start))
        {
            Console.Error.WriteLine($"'{start}' is not a directory.");
            return 1;
        }

        var root = FindRepoRoot(start);
        if (root is null)
        {
            root = Path.GetFullPath(start);
            Console.WriteLine($"No git repository at or above '{start}'; using that directory as the root.");
        }
        Console.WriteLine($"Repository: {root}");

        var detected = Detect(root);
        var client = options.Client ?? detected.FirstOrDefault() ?? ClaudeCode;
        Console.WriteLine(detected.Count > 0
            ? $"Detected:   {string.Join(", ", detected.Select(c => c.Name))} -> installing for {client.Name}"
            : $"Detected:   nothing in particular -> installing for {client.Name}");
        foreach (var other in detected.Where(c => c != client))
        {
            Console.WriteLine($"            (also `{ToolCommand} install --client {other.Name}` for {other.ConfigFile})");
        }

        var path = options.File is { Length: > 0 } file ? Path.GetFullPath(file) : client.PathIn(root);

        var (command, commandArgs) = LaunchCommand(
            Environment.ProcessPath, FindProjectDir(AppContext.BaseDirectory));
        var env = EnvEntries(client, options.Set, Environment.GetEnvironmentVariable);
        var entry = Entry(command, commandArgs, env);

        string? existingText = null;
        if (File.Exists(path))
        {
            try
            {
                existingText = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot read '{path}': {e.Message}");
                return 1;
            }
        }

        JsonObject document;
        Outcome outcome;
        JsonNode? existingEntry;
        try
        {
            (document, outcome, existingEntry) =
                Merge(existingText, client.ServersProperty, options.Name, entry, options.Force);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            Console.Error.WriteLine($"'{path}' cannot be merged into: {e.Message}");
            Console.Error.WriteLine("Fix the file, or pass --file to write somewhere else.");
            return 1;
        }

        if (outcome == Outcome.Conflict)
        {
            Console.Error.WriteLine($"'{options.Name}' already exists in {path} and differs:");
            Console.Error.WriteLine(Indent(existingEntry!.ToJsonString(Format)));
            Console.Error.WriteLine("would become:");
            Console.Error.WriteLine(Indent(entry.ToJsonString(Format)));
            Console.Error.WriteLine("Re-run with --force to replace it, or with --name to install alongside it.");
            return 1;
        }

        var text = Serialize(document);

        if (options.DryRun)
        {
            Console.WriteLine($"Would write {path} ({Describe(outcome)}):");
            Console.WriteLine();
            Console.WriteLine(text.TrimEnd());
            return 0;
        }

        if (outcome == Outcome.Unchanged)
        {
            Console.WriteLine($"Already registered: {path} ({options.Name})");
        }
        else
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot write '{path}': {e.Message}");
                return 1;
            }
            Console.WriteLine($"{Describe(outcome)}: {path} ({options.Name} -> {command})");
        }

        ReportReadiness(env);
        return 0;
    }

    private static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Added => "Added",
        Outcome.Replaced => "Replaced",
        Outcome.Unchanged => "unchanged",
        _ => outcome.ToString(),
    };

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => "  " + l.TrimEnd('\r')));

    /// <summary>
    /// What is still needed before the registration works. The config only names the environment
    /// variables it defers to, so an unset one stays invisible until an MCP client fails
    /// obscurely, and nothing works until `auth` has run once.
    /// </summary>
    private static void ReportReadiness(List<KeyValuePair<string, string>> env)
    {
        Console.WriteLine();
        foreach (var (key, value) in env)
        {
            if (ReferencedVariable(value) is not { } variable)
            {
                Console.WriteLine($"  {key} = {value}");
                continue;
            }
            var set = Environment.GetEnvironmentVariable(variable) is { Length: > 0 };
            Console.WriteLine($"  {key} = {value}  {(set ? "(set)" : "** NOT SET in this environment **")}");
        }

        var record = AdoContext.RecordPath;
        Console.WriteLine(File.Exists(record)
            ? $"  signed in (record written {File.GetLastWriteTimeUtc(record):u})"
            : $"  not signed in. Run `{ToolCommand} auth` once");
    }
}
