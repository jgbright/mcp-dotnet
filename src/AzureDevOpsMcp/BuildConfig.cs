using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// Reading a build definition as configuration, the build counterpart of
/// <see cref="ReleaseConfig"/>: which folders a CI build maps, which tasks it runs, and what their
/// inputs and variables say. Pure, like its counterpart. A secret matches on its name only.
/// </summary>
internal static class BuildConfig
{
    /// <summary>How many definitions one scan examines. The listing carries them whole, in one request.</summary>
    internal const int ScanCap = 500;

    internal const string MappingKind = "mapping";
    internal const string TaskKind = "task";

    internal readonly record struct Scope(bool Variables, bool TaskInputs, bool Mappings, bool Tasks);

    internal static Scope ParseScope(string? scope) =>
        (scope ?? "all").ToLowerInvariant() switch
        {
            "all" => new(true, true, true, true),
            "variables" => new(true, false, false, false),
            "task_inputs" => new(false, true, false, false),
            "mappings" => new(false, false, true, false),
            "tasks" => new(false, false, false, true),
            _ => throw new McpException(
                $"Unknown scope '{scope}'. Use variables, task_inputs, mappings, tasks, or all."),
        };

    /// <summary>
    /// One configured setting. <c>KeySearched</c> is false where the key is a label rather than
    /// something a definition author wrote (map/cloak, "task").
    /// </summary>
    private readonly record struct Setting(
        string Kind, string? Phase, string? Step, string Key, string? Value, bool IsSecret, bool KeySearched);

    private static IEnumerable<Setting> Settings(
        WireBuildDefinitionDetail d, Scope scope, IReadOnlyDictionary<string, string> taskNames)
    {
        if (scope.Variables)
        {
            foreach (var v in d.Variables ?? [])
            {
                var secret = v.Value?.IsSecret is true;
                yield return new Setting(ReleaseConfig.VariableKind, null, null, v.Key,
                    secret ? null : v.Value?.Value, secret, KeySearched: true);
            }
        }
        if (scope.Mappings)
        {
            foreach (var m in Deployments.TfvcWorkspace(Mapping.TfvcMappingJson(d)))
            {
                yield return new Setting(MappingKind, null, null, m.Cloaked is true ? "cloak" : "map",
                    m.ServerPath, false, KeySearched: false);
            }
        }
        if (!scope.Tasks && !scope.TaskInputs)
        {
            yield break;
        }
        foreach (var phase in d.Process?.Phases ?? [])
        {
            foreach (var step in phase.Steps ?? [])
            {
                if (scope.Tasks && Mapping.TaskLabel(step.Task, taskNames) is { } label)
                {
                    var value = taskNames.ContainsKey(step.Task!.Id!) ? $"{label} ({step.Task.Id})" : label;
                    yield return new Setting(TaskKind, phase.Name, step.DisplayName, "task", value, false,
                        KeySearched: false);
                }
                if (!scope.TaskInputs)
                {
                    continue;
                }
                foreach (var input in step.Inputs ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(input.Value))
                    {
                        yield return new Setting(ReleaseConfig.TaskInputKind, phase.Name, step.DisplayName,
                            input.Key, input.Value, false, KeySearched: true);
                    }
                }
            }
        }
    }

    internal static IEnumerable<BuildDefinitionMatchDto> Matches(
        WireBuildDefinitionDetail d, Scope scope, IReadOnlyDictionary<string, string> taskNames,
        Func<string, bool> matches)
    {
        foreach (var s in Settings(d, scope, taskNames))
        {
            var matchedIn = s.KeySearched && matches(s.Key) ? "name"
                : s.Value is { } value && matches(value) ? "value"
                : null;
            if (matchedIn is not null)
            {
                yield return new BuildDefinitionMatchDto(
                    d.Id, d.Name, s.Kind, s.Phase, s.Step, s.Key, s.Value, s.IsSecret ? true : null, matchedIn);
            }
        }
    }
}
