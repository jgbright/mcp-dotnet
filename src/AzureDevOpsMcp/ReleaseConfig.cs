using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// Reading a classic release definition as configuration rather than as history, to answer "does
/// anything in this pipeline write over the value my repository checks in". No single field
/// answers it. A definition- or environment-scope variable can override a setting by name alone,
/// and a substitution task (File Transform, Replace Tokens, JSON variable substitution) names the
/// files it rewrites in its own <c>inputs</c>, so a variable list without task inputs cannot tell
/// "not overridden" from "overridden by whatever matches the file".
///
/// Everything here is pure: flattening a definition to the settings it holds, and deciding
/// whether a pattern matches one. A secret's value is never a candidate, since matching on a
/// value the tool then refuses to return would leak it a bit at a time.
/// </summary>
internal static class ReleaseConfig
{
    /// <summary>How many definitions one scan will read in full. Each costs its own request.</summary>
    internal const int ScanCap = 200;

    /// <summary>Guards a caller-supplied regex against a pathological pattern.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    internal const string VariableKind = "variable";
    internal const string TaskInputKind = "taskInput";

    /// <summary>
    /// One configured setting, wherever it lives. <c>Environment</c> is null at definition scope
    /// and <c>Task</c> is null for a variable, which is how the result DTO reports them.
    /// </summary>
    internal readonly record struct Setting(
        string? Environment, string Kind, string? Task, string Key, string? Value, bool IsSecret);

    internal static (bool Variables, bool TaskInputs) ParseScope(string? scope) =>
        (scope ?? "both").ToLowerInvariant() switch
        {
            "both" => (true, true),
            "variables" => (true, false),
            "task_inputs" => (false, true),
            _ => throw new McpException(
                $"Unknown scope '{scope}'. Use variables, task_inputs, or both."),
        };

    /// <summary>
    /// The test a pattern describes: a case-insensitive substring by default, or the caller's own
    /// regular expression when <paramref name="regex"/> is set. A regex that does not compile
    /// fails loudly instead of silently matching nothing.
    /// </summary>
    internal static Func<string, bool> Matcher(string pattern, bool regex)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            throw new McpException("`pattern` is empty. Give the name or text to look for.");
        }
        if (!regex)
        {
            return text => text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
        Regex compiled;
        try
        {
            compiled = new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"`pattern` is not a valid regular expression: {e.Message}");
        }
        return text =>
        {
            try
            {
                return compiled.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // Backtracking blew the budget on this one value; the rest of the scan is fine.
                return false;
            }
        };
    }

    /// <summary>
    /// Everything one definition configures, in the order a reader would look: definition
    /// variables, then each environment's variables, then the inputs of each task it runs.
    /// </summary>
    internal static IEnumerable<Setting> Settings(
        WireReleaseDefinitionDetail definition, bool variables, bool taskInputs)
    {
        if (variables)
        {
            foreach (var v in definition.Variables ?? [])
            {
                yield return Variable(environment: null, v.Key, v.Value);
            }
        }
        foreach (var env in (definition.Environments ?? []).OrderBy(e => e.Rank ?? 0))
        {
            if (variables)
            {
                foreach (var v in env.Variables ?? [])
                {
                    yield return Variable(env.Name, v.Key, v.Value);
                }
            }
            if (!taskInputs)
            {
                continue;
            }
            foreach (var phase in (env.DeployPhases ?? []).OrderBy(p => p.Rank ?? 0))
            {
                foreach (var task in phase.WorkflowTasks ?? [])
                {
                    foreach (var input in task.Inputs ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(input.Value))
                        {
                            continue;
                        }
                        yield return new Setting(
                            env.Name, TaskInputKind, task.Name, input.Key, input.Value, IsSecret: false);
                    }
                }
            }
        }
    }

    private static Setting Variable(string? environment, string name, WireReleaseVariable? variable)
    {
        var secret = variable?.IsSecret is true;
        return new Setting(
            environment, VariableKind, Task: null, name, secret ? null : variable?.Value, secret);
    }

    /// <summary>
    /// The settings of one definition that match. A secret matches on its name only: its value is
    /// neither tested nor returned.
    /// </summary>
    internal static IEnumerable<ReleaseDefinitionMatchDto> Matches(
        WireReleaseDefinitionDetail definition, bool variables, bool taskInputs,
        Func<string, bool> matches, string? webUrl)
    {
        foreach (var setting in Settings(definition, variables, taskInputs))
        {
            var matchedIn = matches(setting.Key) ? "name"
                : setting.Value is { } value && matches(value) ? "value"
                : null;
            if (matchedIn is null)
            {
                continue;
            }
            yield return new ReleaseDefinitionMatchDto(
                definition.Id,
                definition.Name,
                setting.Environment,
                setting.Kind,
                setting.Task,
                setting.Key,
                setting.Value,
                setting.IsSecret ? true : null,
                matchedIn,
                webUrl);
        }
    }
}
