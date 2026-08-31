using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp;

// The deployment map's wire shape. Which deployables exist, what ships each one and what counts
// as "production" are one organization's facts, so they live in a JSON file; the server knows
// only the protocol chains and how to compare versions against paths. A deployable takes one of
// two forms, by which field names its pipeline:
//
//   {
//     "deployables": [
//       { "name": "clients-website",
//         "releaseDefinition": "Clients -  Website",      // classic release pipeline
//         "environment": "Production",
//         "paths": ["$/Contoso/Websites/Trunk"],
//         "note": "customer portal" },
//       { "name": "billing-api",
//         "pipeline": "Billing API",                      // build/YAML pipeline
//         "environment": "production",                    // optional: an ADO Environment
//         "branch": "main" }                              // optional: defaults to the run's branch
//     ]
//   }
//
// Classic chain: release definition → environment → latest succeeded deployment → build artifact
// → TFVC changeset. Pipeline chain: pipeline → latest succeeded run (through the Environment's
// deployment records when `environment` is configured, straight off the build API otherwise) →
// the commit or changeset it was built from.
//
// `paths` (TFVC server-path prefixes, not globs) scope the "undeployed changesets" question when
// the deployed build came from TFVC. Omitted, they are derived at call time from the build
// definition's TFVC workspace mappings, so the file only has to say what the build definition
// cannot. For a git-built deployable the branch answers that question instead. `note` is opaque
// passthrough and unknown fields are ignored, so the same file can carry data for other consumers
// (e.g. a git-tagging script).

internal sealed record DeploymentFile(List<DeployableEntry>? Deployables);

internal sealed record DeployableEntry(
    string? Name, string? ReleaseDefinition, string? Environment, string? Pipeline, string? Branch,
    string? Project, List<string>? Paths, string? Note);

internal sealed record Deployable(
    string Name, string? ReleaseDefinition, string? Environment, string? Pipeline, string? Branch,
    string? Project, List<string>? Paths, string? Note);

/// <summary>
/// The deployment map (format above). File mechanics live in <see cref="DataFile{T}"/>; the
/// tool's pure helpers live here.
/// </summary>
internal static class Deployments
{
    private static readonly DataFile<List<Deployable>> Data = new(
        "ADO_MCP_DEPLOYMENTS", "deployments.json", "deployment map",
        "{\"deployables\":[{\"name\":\"...\",\"releaseDefinition\":\"<classic release definition " +
        "name or id>\",\"environment\":\"<environment name or id>\",\"paths\":[\"$/Project/Area\"]," +
        "\"note\":\"...\"} | {\"name\":\"...\",\"pipeline\":\"<build/YAML pipeline name or id>\"," +
        "\"environment\":\"<ADO Environment, optional>\",\"branch\":\"<optional>\"}]}",
        Parse);

    internal static string ConfiguredPath => Data.ConfiguredPath;

    internal static List<Deployable> Get(ILogger log) => Data.Get(log);

    internal static List<Deployable> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<DeploymentFile>(json, AdoClient.Json)
                   ?? throw new FormatException("the file is empty");
        var deployables = new List<Deployable>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, index) in (file.Deployables ?? []).Select((e, i) => (e, i)))
        {
            if (entry.Name is not { Length: > 0 } name)
            {
                throw new FormatException($"deployables[{index}] has no \"name\"");
            }
            if (!names.Add(name))
            {
                throw new FormatException($"deployables[{index}] duplicates the name \"{name}\"");
            }
            var classic = entry.ReleaseDefinition is { Length: > 0 };
            var pipeline = entry.Pipeline is { Length: > 0 };
            if (classic == pipeline)
            {
                throw new FormatException(
                    $"deployables[{index}] (\"{name}\") needs exactly one of \"releaseDefinition\" " +
                    "(classic release) or \"pipeline\" (build/YAML pipeline)");
            }
            if (classic && entry.Environment is not { Length: > 0 })
            {
                throw new FormatException($"deployables[{index}] (\"{name}\") has no \"environment\"");
            }
            if (classic && entry.Branch is { Length: > 0 })
            {
                throw new FormatException(
                    $"deployables[{index}] (\"{name}\"): \"branch\" only applies to a \"pipeline\" deployable");
            }
            if (entry.Paths is { } paths && paths.Any(p => string.IsNullOrWhiteSpace(p) || !p.StartsWith("$/", StringComparison.Ordinal)))
            {
                throw new FormatException(
                    $"deployables[{index}] (\"{name}\"): every path must be a TFVC server path starting with $/");
            }
            deployables.Add(new Deployable(
                name, entry.ReleaseDefinition, entry.Environment, entry.Pipeline, entry.Branch,
                entry.Project, entry.Paths, entry.Note));
        }
        return deployables;
    }

    /// <summary>
    /// Azure DevOps splits services across hosts: dev.azure.com/{org} answers builds and version
    /// control, vsrm.dev.azure.com/{org} answers release definitions and deployments.
    /// <see cref="Search.BaseUrl"/> and <see cref="Writes.VsspsBaseUrl"/> do the same for their
    /// services.
    /// </summary>
    internal static string VsrmBaseUrl(string orgUrl)
    {
        const string modern = "https://dev.azure.com/";
        if (orgUrl.StartsWith(modern, StringComparison.OrdinalIgnoreCase))
        {
            return "https://vsrm.dev.azure.com/" + orgUrl[modern.Length..];
        }
        // Legacy {org}.visualstudio.com hosts release management on {org}.vsrm.visualstudio.com.
        var legacy = new Uri(orgUrl);
        return legacy.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            ? $"{legacy.Scheme}://{legacy.Host[..legacy.Host.IndexOf('.')]}.vsrm.visualstudio.com"
            : orgUrl;
    }

    /// <summary>
    /// The mapped (not cloaked) server paths of a classic build definition's TFVC workspace: the
    /// definition's own answer to "which paths feed this build". The value arrives as a JSON
    /// string inside repository.properties.
    /// </summary>
    internal static List<string> ParseTfvcMappings(string? tfvcMappingJson)
    {
        if (string.IsNullOrWhiteSpace(tfvcMappingJson))
        {
            return [];
        }
        var parsed = JsonSerializer.Deserialize<WireTfvcMappingFile>(tfvcMappingJson, AdoClient.Json);
        return (parsed?.Mappings ?? [])
            .Where(m => string.Equals(m.MappingType, "map", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.ServerPath)
            .OfType<string>()
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>refs/heads/main → main. Anything else passes through untouched.</summary>
    internal static string ShortBranch(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;

    /// <summary>TFVC prefix containment: the path is one of the roots or lies underneath one.</summary>
    internal static bool UnderAny(string path, IReadOnlyList<string> roots) =>
        roots.Any(root =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
}

internal sealed record WireTfvcMapping(string? ServerPath, string? MappingType);

internal sealed record WireTfvcMappingFile(List<WireTfvcMapping>? Mappings);
