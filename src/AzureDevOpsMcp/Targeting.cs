namespace AzureDevOpsMcp;

/// <summary>
/// Which machines a classic release stage would deploy to: the pure half of
/// <c>get_release_definition_targets</c>. The tool reads the definition and the deployment groups
/// it names; what they add up to is decided here, so it can be tested without the service.
///
/// Each of these rules gives a wrong answer, not an error, if assumed away:
/// <list type="bullet">
/// <item><c>phaseType</c> decides what <c>queueId</c> means. On a <c>machineGroupBasedDeployment</c>
/// phase it is a deployment group. On an <c>agentBasedDeployment</c> phase it is an agent queue,
/// which has no machines to resolve.</item>
/// <item>A machine is selected when it carries all of the phase's tags, ignoring case. Both parts
/// are Azure DevOps' own rule: the targets endpoint documents its filter as "contain all these
/// tags", and the deployment-group docs say tags are case insensitive. Both were checked live: a
/// phase tagged <c>clients</c> selected the machines tagged <c>Clients</c>, and two tags selected
/// only the machine carrying both.</item>
/// <item>No tags means every machine in the group, including any added later. That is what
/// <c>Enumerable.All</c> returns for an empty list, which is correct and easy to misread, so the
/// result says <c>allMachines: true</c> outright.</item>
/// </list>
///
/// A phase whose tags select nothing reports <c>machines: []</c> beside the group's
/// <c>machineCount</c>, because a deploy to zero targets succeeds and looks like a good
/// deployment. A group that could not be read puts the error on that phase instead of failing the
/// whole definition, since the other stages still have answers.
/// </summary>
internal static class Targeting
{
    internal const string MachineGroupPhase = "machineGroupBasedDeployment";

    internal static bool IsMachineGroup(string? phaseType) =>
        string.Equals(phaseType, MachineGroupPhase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tags as configured, trimmed, with blanks dropped, in their original order.</summary>
    internal static List<string> Tags(IEnumerable<string?>? tags) =>
        (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).ToList();

    /// <summary>
    /// The machines a phase's tags select from a group. A machine needs every wanted tag; an empty
    /// list selects everything.
    /// </summary>
    internal static List<WireDeploymentMachine> Select(
        IEnumerable<WireDeploymentMachine> machines, IEnumerable<string?>? tags)
    {
        var wanted = Tags(tags);
        return machines
            .Where(m =>
            {
                var carried = Tags(m.Tags);
                return wanted.All(w => carried.Any(t => string.Equals(t, w, StringComparison.OrdinalIgnoreCase)));
            })
            .ToList();
    }

    /// <summary>
    /// A definition resolved to targets, with environments and phases sorted by rank. The raw
    /// arrays are not sorted: measured, one definition arrives Production-then-QA while the ranks
    /// say the reverse. <paramref name="groups"/> holds the groups that were read and
    /// <paramref name="errors"/> says why the others were not, both keyed by group id.
    /// </summary>
    internal static ReleaseTargetsDto Resolve(
        WireReleaseDefinitionDetail definition,
        IReadOnlyDictionary<int, WireDeploymentGroup> groups,
        IReadOnlyDictionary<int, string> errors,
        string orgUrl,
        string? project) => new(
        definition.Id,
        definition.Name,
        (definition.Environments ?? [])
            .OrderBy(e => e.Rank ?? 0)
            .Select(e => new StageTargetsDto(
                e.Id,
                e.Name,
                (e.DeployPhases ?? []).OrderBy(p => p.Rank ?? 0).Select(p => Phase(p, groups, errors)).ToList()))
            .ToList(),
        Mapping.ReleaseDefinitionUrl(orgUrl, project, definition.Id));

    internal static PhaseTargetsDto Phase(
        WireDeployPhase phase,
        IReadOnlyDictionary<int, WireDeploymentGroup> groups,
        IReadOnlyDictionary<int, string> errors)
    {
        var input = phase.DeploymentInput;
        if (!IsMachineGroup(phase.PhaseType))
        {
            // An agent pool or the server runs this, so there are no machines to resolve. Report
            // the type; the common case leaves it out.
            return new PhaseTargetsDto(
                phase.Name, phase.PhaseType, null, input?.QueueId is > 0 ? input.QueueId : null,
                null, null, null, null);
        }

        var tags = Tags(input?.Tags);
        List<string>? tagList = tags.Count > 0 ? tags : null;
        bool? allMachines = tags.Count == 0 ? true : null;

        if (input?.QueueId is not { } groupId)
        {
            return new PhaseTargetsDto(
                phase.Name, null, null, null, tagList, allMachines, null,
                "the phase names no deployment group");
        }
        if (groups.TryGetValue(groupId, out var group))
        {
            return new PhaseTargetsDto(
                phase.Name, null, Mapping.DeploymentGroup(group, includeMachines: false), null,
                tagList, allMachines,
                // An empty list is the finding: the tags selected nothing.
                Mapping.DeploymentMachines(Select(group.Machines ?? [], tags)) ?? [],
                null);
        }
        return new PhaseTargetsDto(
            phase.Name, null, new DeploymentGroupDto(groupId, null, null, null, null), null,
            tagList, allMachines, null,
            errors.TryGetValue(groupId, out var error) ? error : $"deployment group {groupId} was not read");
    }
}
