using System.Globalization;
using System.Text.Json;

namespace AzureDevOpsMcp;

// ----------------------------------------------------------------------- wire models
//
// Shapes as Azure DevOps returns them. Everything is nullable because fields documented as
// present are often absent anyway (older API versions, partial `fields` projections, records the
// caller cannot see), and assuming presence throws NullReferenceException at a user.

internal sealed record ListResponse<T>(int Count, List<T>? Value);

internal sealed record WireIdentity(
    string? DisplayName, string? UniqueName, string? Id,
    // connectionData names the signed-in user this way instead. Defaulted so it binds by name
    // without disturbing positional construction elsewhere.
    string? ProviderDisplayName = null);

internal sealed record WireConnectionData(WireIdentity? AuthenticatedUser);

internal sealed record WireProject(
    string? Id, string? Name, string? Description, string? State, string? Visibility,
    DateTimeOffset? LastUpdateTime);

internal sealed record WireProjectRef(string? Id, string? Name);

internal sealed record WireRepo(
    string? Id, string? Name, string? DefaultBranch, string? WebUrl, bool? IsDisabled,
    WireProjectRef? Project);

internal sealed record WireReviewer(string? DisplayName, string? UniqueName, int? Vote, bool? IsRequired);

internal sealed record WirePullRequest(
    int PullRequestId, string? Title, string? Description, string? Status, WireIdentity? CreatedBy,
    DateTimeOffset? CreationDate, DateTimeOffset? ClosedDate, string? SourceRefName, string? TargetRefName,
    bool? IsDraft, string? MergeStatus, WireRepo? Repository, List<WireReviewer>? Reviewers);

internal sealed record WireFilePosition(int? Line, int? Offset);

internal sealed record WireThreadContext(
    string? FilePath, WireFilePosition? RightFileStart, WireFilePosition? LeftFileStart);

internal sealed record WireComment(
    int Id, int? ParentCommentId, WireIdentity? Author, string? Content, string? CommentType,
    DateTimeOffset? PublishedDate, bool? IsDeleted);

internal sealed record WireThread(
    int Id, string? Status, List<WireComment>? Comments, WireThreadContext? ThreadContext, bool? IsDeleted);

internal sealed record WireRelation(string? Rel, string? Url, Dictionary<string, JsonElement>? Attributes);

internal sealed record WireWorkItem(
    int Id, Dictionary<string, JsonElement>? Fields, List<WireRelation>? Relations, string? Url);

internal sealed record WiqlRef(int Id);

internal sealed record WiqlResult(List<WiqlRef>? WorkItems);

internal sealed record WireWorkItemComment(
    int Id, string? Text, WireIdentity? CreatedBy, DateTimeOffset? CreatedDate,
    DateTimeOffset? ModifiedDate, bool? IsDeleted);

internal sealed record WireWorkItemComments(List<WireWorkItemComment>? Comments, int? TotalCount);

internal sealed record WirePipeline(int Id, string? Name, string? Folder, int? Revision);

internal sealed record WireBuildDefinition(int Id, string? Name);

/// <summary>
/// A pipeline run as the build API returns it. A run id and a build id are the same number. Runs
/// are read through the build endpoints: they take a run id without its pipeline id, filter and
/// page properly, and carry the timeline.
/// </summary>
internal sealed record WireBuild(
    int Id, string? BuildNumber, string? Status, string? Result, DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, string? SourceBranch,
    WireBuildDefinition? Definition, WireIdentity? RequestedFor, WireProjectRef? Project,
    // Defaulted so they bind by name without disturbing positional construction elsewhere.
    // sourceVersion is the changeset number as a bare string for TFVC, a commit SHA for git.
    string? SourceVersion = null,
    WireBuildRepositoryRef? Repository = null);

internal sealed record WireBuildRepositoryRef(string? Id, string? Name, string? Type);

internal sealed record WireIssue(string? Type, string? Category, string? Message);

internal sealed record WireLogRef(int Id, string? Url);

internal sealed record WireTimelineRecord(
    string? Id, string? ParentId, string? Type, string? Name, string? State, string? Result,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, int? ErrorCount, int? WarningCount,
    List<WireIssue>? Issues, WireLogRef? Log, int? Order);

internal sealed record WireTimeline(List<WireTimelineRecord>? Records);

internal sealed record WireTfvcItem(string? Path);

internal sealed record WireTfvcChange(WireTfvcItem? Item, string? ChangeType);

internal sealed record WireTfvcChangesetRef(
    int ChangesetId, WireIdentity? Author, DateTimeOffset? CreatedDate, string? Comment);

// Release Management (classic release pipelines). These live on the vsrm host (see
// Deployments.VsrmBaseUrl). The artifact reference is stringly typed: definitionReference carries
// name/id pairs whose ids are numbers serialized as strings.

internal sealed record WireReleaseDefEnvironment(int Id, string? Name, int? Rank);

internal sealed record WireReleaseDefinition(
    int Id, string? Name, List<WireReleaseDefEnvironment>? Environments, string? Path = null);

internal sealed record WireReleaseEnvRef(int? DefinitionEnvironmentId, string? Name);

internal sealed record WireReleaseRef(int Id, string? Name);

internal sealed record WireDeployment(
    int Id, WireReleaseRef? Release, WireReleaseEnvRef? ReleaseEnvironment,
    DateTimeOffset? CompletedOn, string? DeploymentStatus);

internal sealed record WireArtifactPart(string? Id, string? Name);

internal sealed record WireArtifactDefinitionReference(WireArtifactPart? Definition, WireArtifactPart? Version);

internal sealed record WireReleaseArtifact(
    string? Alias, string? Type, bool? IsPrimary, WireArtifactDefinitionReference? DefinitionReference);

// A release read whole. `environments` needs $expand=environments on the listing and always
// arrives on a single release. The per-task detail inside deploySteps needs $expand=tasks; without
// it releaseDeployPhases comes back empty and a failed deployment looks unexplained. The fields
// after Artifacts are defaulted so deployment_status, which asks for none, still constructs.
internal sealed record WireRelease(
    int Id, string? Name, List<WireReleaseArtifact>? Artifacts,
    string? Status = null, string? Reason = null, string? Description = null,
    DateTimeOffset? CreatedOn = null, WireIdentity? CreatedBy = null,
    List<WireReleaseEnvironment>? Environments = null, WireReleaseRef? ReleaseDefinition = null);

internal sealed record WireReleaseEnvironment(
    int Id, string? Name, string? Status, int? Rank, int? DefinitionEnvironmentId,
    string? TriggerReason, List<WireDeploymentAttempt>? DeploySteps,
    List<WireReleaseApproval>? PreDeployApprovals, List<WireReleaseApproval>? PostDeployApprovals);

/// <summary>
/// One try at deploying a stage. A redeploy adds an attempt instead of replacing the last one, so
/// what happened this time is the highest <c>attempt</c>, not the first in the list.
/// </summary>
internal sealed record WireDeploymentAttempt(
    int Attempt, int? DeploymentId, string? Status, string? OperationStatus,
    DateTimeOffset? QueuedOn, DateTimeOffset? LastModifiedOn, string? Reason,
    WireIdentity? RequestedFor, List<WireReleaseDeployPhase>? ReleaseDeployPhases);

internal sealed record WireReleaseDeployPhase(
    string? Name, string? PhaseType, int? Rank, string? Status, DateTimeOffset? StartedOn,
    List<WireDeploymentJob>? DeploymentJobs);

internal sealed record WireDeploymentJob(WireReleaseTask? Job, List<WireReleaseTask>? Tasks);

internal sealed record WireReleaseTask(
    int Id, string? Name, string? Status, int? Rank, string? AgentName,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, string? LogUrl,
    List<WireReleaseIssue>? Issues);

/// <summary>
/// Not <see cref="WireIssue"/>: the build timeline spells this field <c>type</c> and Release
/// Management spells it <c>issueType</c>, so one record cannot bind both.
/// </summary>
internal sealed record WireReleaseIssue(string? IssueType, string? Message);

internal sealed record WireReleaseApproval(
    int Id, string? Status, string? ApprovalType, WireIdentity? Approver, WireIdentity? ApprovedBy,
    DateTimeOffset? CreatedOn, DateTimeOffset? ModifiedOn, string? Comments, bool? IsAutomated,
    int? Rank, int? Attempt, WireReleaseRef? Release, WireReleaseRef? ReleaseEnvironment);

// A release definition read whole (the by-id endpoint; the listing returns a summary): variables
// at two scopes, the variable groups it pulls in, and per environment the phases and their tasks
// with inputs. A variable is a map entry name -> {value,isSecret,allowOverride}; `variableGroups`
// is a list of bare ids at both scopes, and the names cost a second request.

internal sealed record WireReleaseVariable(string? Value, bool? IsSecret, bool? AllowOverride);

internal sealed record WireReleaseDefinitionDetail(
    int Id, string? Name, string? Path, string? Description, int? Revision,
    Dictionary<string, WireReleaseVariable>? Variables, List<int>? VariableGroups,
    List<WireReleaseDefEnvironmentDetail>? Environments, List<WireReleaseArtifact>? Artifacts);

internal sealed record WireReleaseDefEnvironmentDetail(
    int Id, string? Name, int? Rank,
    Dictionary<string, WireReleaseVariable>? Variables, List<int>? VariableGroups,
    List<WireDeployPhase>? DeployPhases);

internal sealed record WireDeployPhase(
    string? Name, int? Rank, string? PhaseType, List<WireWorkflowTask>? WorkflowTasks,
    WireDeploymentInput? DeploymentInput = null);

/// <summary>
/// Where a phase runs. <c>queueId</c> is a deployment group on a <c>machineGroupBasedDeployment</c>
/// phase and an agent queue on an <c>agentBasedDeployment</c> phase; only <c>phaseType</c> tells
/// them apart. <c>tags</c> pick machines in the group: a machine needs all of them, case does not
/// matter, and no tags means every machine (see <see cref="Targeting"/>).
/// </summary>
internal sealed record WireDeploymentInput(
    int? QueueId, List<string>? Tags, string? DeploymentHealthOption, int? HealthPercent,
    int? TimeoutInMinutes, string? Condition);

// Deployment groups: the machines classic release stages deploy to, on the task agent service at
// distributedtask/deploymentgroups, project-scoped on the core host. They are not the ADO
// Environments that YAML pipelines deploy to (WireEnvironmentInstance), which sit next to them
// under distributedtask/environments. The listing rejects $expand=machines (400, "no longer
// supported"); only the by-id read returns machines.
//
// Agent capabilities are left out: they are the agent's own environment variables, the service
// does not mark them secret, and on a real agent they held a license key. The by-id read does
// not return them, and no tool calls the endpoint that does.

internal sealed record WireDeploymentGroup(
    int Id, string? Name, string? Description, int? MachineCount, List<WireDeploymentMachine>? Machines);

internal sealed record WireDeploymentMachine(int Id, List<string>? Tags, WireDeploymentAgent? Agent);

internal sealed record WireDeploymentAgent(
    int Id, string? Name, string? Version, string? OsDescription, bool? Enabled, string? Status);

/// <summary>
/// One configured task. Not <see cref="WireReleaseTask"/>, which is a task as it ran: this one is
/// flat (<c>taskId</c>/<c>version</c>, no nested task reference) and carries the <c>inputs</c>
/// that say which files a transform touches.
/// </summary>
internal sealed record WireWorkflowTask(
    string? TaskId, string? Version, string? Name, bool? Enabled, string? Condition,
    Dictionary<string, string?>? Inputs);

/// <summary>A variable group as the task agent service lists it. Its variables are never read.</summary>
internal sealed record WireVariableGroup(int Id, string? Name);

internal sealed record WireBuildRepository(string? Type, string? Name, Dictionary<string, string>? Properties);

/// <summary>A build definition read individually. The list shape (WirePipeline) has no repository.</summary>
internal sealed record WireBuildDefinitionDetail(int Id, string? Name, WireBuildRepository? Repository);

// Pipeline (modern/YAML) deployables. Deployments into an ADO Environment arrive as
// distributedtask environmentdeploymentrecords, newest first. A record's `owner` is the run
// (= build) that performed it; `definition` is the pipeline it belongs to.

internal sealed record WireEnvironmentInstance(int Id, string? Name);

internal sealed record WireEnvRecordRef(int Id, string? Name);

internal sealed record WireEnvDeploymentRecord(
    long Id, string? Result, WireEnvRecordRef? Definition, WireEnvRecordRef? Owner,
    DateTimeOffset? FinishTime);

internal sealed record WireGitAuthor(string? Name, string? Email, DateTimeOffset? Date);

internal sealed record WireGitCommitRef(string? CommitId, WireGitAuthor? Author, string? Comment);

// Search (code, work item, wiki). These arrive from the almsearch host (see Search.BaseUrl) and
// mark matched terms with <highlighthit>, stripped before output.

internal sealed record WireSearchRepository(string? Name, string? Id, string? Type);

internal sealed record WireSearchVersion(string? BranchName, string? ChangeId);

internal sealed record WireCodeHit(int? CharOffset, int? Length, int? Line, int? Column, string? CodeSnippet);

internal sealed record WireCodeMatches(List<WireCodeHit>? Content, List<WireCodeHit>? FileName);

internal sealed record WireCodeResult(
    string? FileName, string? Path, WireCodeMatches? Matches, WireProjectRef? Project,
    WireSearchRepository? Repository, List<WireSearchVersion>? Versions);

internal sealed record WireCodeSearchResponse(int Count, List<WireCodeResult>? Results);

internal sealed record WireSearchHit(string? FieldReferenceName, List<string>? Highlights);

internal sealed record WireWorkItemSearchResult(
    WireProjectRef? Project, Dictionary<string, string?>? Fields, List<WireSearchHit>? Hits);

internal sealed record WireWorkItemSearchResponse(int Count, List<WireWorkItemSearchResult>? Results);

internal sealed record WireWikiRef(string? Id, string? Name, string? MappedPath, string? Version);

internal sealed record WireWikiResult(
    string? FileName, string? Path, WireProjectRef? Project, WireWikiRef? Wiki, List<WireSearchHit>? Hits);

internal sealed record WireWikiSearchResponse(int Count, List<WireWikiResult>? Results);

internal sealed record WireTeam(string? Id, string? Name, string? Description);

internal sealed record WireWorkItemType(string? Name, string? ReferenceName);

/// <summary>
/// An identity as the vssps identity service returns it (see <see cref="Writes.VsspsBaseUrl"/>).
/// The account (UPN) sits in <c>properties</c> inside a <c>{$type, $value}</c> envelope, unwrapped
/// by <see cref="Writes.IdentityValue"/>.
/// </summary>
internal sealed record WireIdentitySearchResult(
    string? Id, string? ProviderDisplayName, string? CustomDisplayName,
    Dictionary<string, JsonElement>? Properties, bool? IsActive);

internal sealed record WireTeamFieldValue(string? Value, bool? IncludeChildren);

internal sealed record WireTeamFieldValues(string? DefaultValue, List<WireTeamFieldValue>? Values);

// ------------------------------------------------------------------------ output DTOs
//
// Shaped for a model's context window: uninteresting fields are null and the serializer
// (configured in Program.cs) omits nulls.

public sealed record ProjectDto(string? Id, string? Name, string? Description, string? State, string? Visibility);

public sealed record RepoDto(string? Id, string? Name, string? DefaultBranch, string? WebUrl, bool? Disabled);

public sealed record PullRequestDto(
    int Id,
    string? Title,
    string? Status,
    string? Repo,
    string? CreatedBy,
    DateTimeOffset? Created,
    string? SourceBranch,
    string? TargetBranch,
    bool? Draft,
    string? MergeStatus,
    List<ReviewerDto>? Reviewers,
    string? WebUrl);

public sealed record ReviewerDto(string? Name, string? Vote, bool? Required);

/// <summary>Envelope for pull request listings: hasMore is omitted when the whole list was returned.</summary>
public sealed record PullRequestsResult(List<PullRequestDto> PullRequests, bool? HasMore);

public sealed record PullRequestDetailDto(
    int Id,
    string? Title,
    string? Status,
    string? Repo,
    string? CreatedBy,
    DateTimeOffset? Created,
    DateTimeOffset? Closed,
    string? SourceBranch,
    string? TargetBranch,
    bool? Draft,
    string? MergeStatus,
    string? Description,
    bool? Truncated,
    List<ReviewerDto>? Reviewers,
    List<ThreadDto>? Threads,
    // True when max_threads cut the list short.
    bool? MoreThreads,
    SkippedDto? Skipped,
    string? WebUrl);

public sealed record ThreadDto(int Id, string? Status, string? FilePath, int? Line, List<CommentDto> Comments);

public sealed record CommentDto(
    int Id, string? Author, DateTimeOffset? Created, string? Type, string? Body, bool? Truncated);

public sealed record WorkItemDto(
    int Id,
    string? Type,
    string? Title,
    string? State,
    string? AssignedTo,
    DateTimeOffset? Changed,
    string? AreaPath,
    string? IterationPath,
    List<string>? Tags,
    int? Priority,
    string? WebUrl);

/// <summary>
/// Envelope for work item queries. <c>wiql</c> is the query the server built from the filter
/// arguments, echoed back so it can be refined and passed straight to the `wiql` parameter. Null
/// when the caller supplied the query.
/// </summary>
public sealed record WorkItemsResult(List<WorkItemDto> WorkItems, bool? HasMore, string? Wiql);

public sealed record WorkItemDetailDto(
    int Id,
    string? Type,
    string? Title,
    string? State,
    string? Reason,
    string? AssignedTo,
    string? CreatedBy,
    DateTimeOffset? Created,
    DateTimeOffset? Changed,
    string? AreaPath,
    string? IterationPath,
    List<string>? Tags,
    int? Priority,
    double? OriginalEstimate,
    double? RemainingWork,
    double? CompletedWork,
    double? StoryPoints,
    double? Effort,
    string? Description,
    string? ReproSteps,
    string? AcceptanceCriteria,
    bool? Truncated,
    List<RelationDto>? Relations,
    List<CommentDto>? Comments,
    SkippedDto? Skipped,
    string? WebUrl);

public sealed record RelationDto(string? Type, string? Name, int? WorkItemId, string? Url);

public sealed record PipelineDto(int Id, string? Name, string? Folder);

public sealed record PipelineRunDto(
    int Id, string? Name, string? State, string? Result, DateTimeOffset? Created,
    DateTimeOffset? Finished, string? Branch, string? RequestedFor, string? WebUrl);

/// <summary>Envelope for run listings: hasMore is omitted when the whole list was returned.</summary>
public sealed record PipelineRunsResult(List<PipelineRunDto> Runs, bool? HasMore);

/// <summary>
/// The run exactly as <c>get_pipeline_run</c> reports it, with the wait alongside. <c>TimedOut</c>
/// is present only when the wait gave up, so a caller can tell "it failed" from "not finished".
/// </summary>
public sealed record PipelineRunWaitResult(
    PipelineRunDetailDto Run,
    int WaitedSeconds,
    bool? TimedOut);

/// <summary>
/// Shaped like <see cref="PipelineRunWaitResult"/>: the pull request as <c>get_pull_request</c>
/// reports it, with <c>TimedOut</c> present only when the wait gave up.
/// </summary>
public sealed record PullRequestWaitResult(
    PullRequestDetailDto PullRequest,
    int WaitedSeconds,
    bool? TimedOut);

public sealed record PipelineRunDetailDto(
    int Id,
    string? Name,
    string? Pipeline,
    string? State,
    string? Result,
    DateTimeOffset? Created,
    DateTimeOffset? Finished,
    string? Branch,
    string? RequestedFor,
    List<FailedStepDto>? FailedSteps,
    SkippedDto? Skipped,
    string? WebUrl);

/// <summary>
/// One failed timeline record with the stage/job it sits under, the issues Azure DevOps recorded
/// against it, and, when asked for, the tail of its log, where the error text actually is.
/// </summary>
public sealed record FailedStepDto(
    string? Stage,
    string? Job,
    string? Task,
    string? Result,
    List<string>? Errors,
    string? LogTail,
    bool? Truncated);

/// <summary>
/// A failed step paired with its timeline record's log url, so fetching the log needs no
/// re-matching by task name (not unique across jobs). Internal: the url is an API address.
/// </summary>
internal sealed record FailedStep(FailedStepDto Step, string? LogUrl);

/// <summary>
/// A listed release task paired with its log url. Internal for the same reason as
/// <see cref="FailedStep"/>: the url is an API address.
/// </summary>
internal sealed record ReleaseTaskEntry(ReleaseTaskDto Task, string? LogUrl);

// ------------------------------------------------------- classic release pipelines
//
// Named as the API and the deployment map name them: a *release definition* is the classic
// pipeline, a *release* is one instance of it, and its stages are *environments*. The build/YAML
// tools own the word "pipeline" here and never mean a classic one. ServerInstructions spells
// that distinction out for the model.

public sealed record ReleaseDefinitionDto(
    int Id, string? Name, string? Folder, List<string>? Environments, string? WebUrl);

public sealed record ReleaseDto(
    int Id,
    string? Name,
    string? Status,
    DateTimeOffset? Created,
    string? CreatedBy,
    string? Reason,
    List<ReleaseEnvironmentDto>? Environments,
    string? WebUrl);

public sealed record ReleaseEnvironmentDto(int Id, string? Name, string? Status);

/// <summary>Envelope for release listings, shaped like <see cref="PipelineRunsResult"/>.</summary>
public sealed record ReleasesResult(List<ReleaseDto> Releases, bool? HasMore);

/// <summary>
/// One release read whole: what it shipped (the artifacts), and per stage where it stands, what
/// is waiting on a human, and what failed.
/// </summary>
public sealed record ReleaseDetailDto(
    int Id,
    string? Name,
    string? Definition,
    string? Status,
    DateTimeOffset? Created,
    string? CreatedBy,
    string? Reason,
    string? Description,
    List<ReleaseArtifactDto>? Artifacts,
    List<ReleaseEnvironmentDetailDto> Environments,
    SkippedDto? Skipped,
    string? WebUrl);

/// <summary>
/// What a release carries. For the usual Build artifact, <c>version</c> is the build number and
/// <c>buildId</c> is the run id, which is what get_pipeline_run takes.
/// </summary>
public sealed record ReleaseArtifactDto(
    string? Alias, string? Type, string? Definition, string? Version, int? BuildId, bool? Primary);

/// <summary>
/// One stage of a release. <c>pendingApprovals</c> is why a stage can sit at <c>queued</c>
/// indefinitely with nothing wrong; each entry carries the id <c>approve_release</c> takes.
/// </summary>
public sealed record ReleaseEnvironmentDetailDto(
    int Id,
    string? Name,
    string? Status,
    string? OperationStatus,
    int? Attempt,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string? RequestedFor,
    List<PendingApprovalDto>? PendingApprovals,
    List<FailedStepDto>? FailedSteps,
    // Every task of the latest attempt, only when include_tasks asked for them. Otherwise a
    // succeeded stage says nothing about what it ran beyond the `skipped.succeeded` count.
    List<ReleaseTaskDto>? Tasks);

public sealed record PendingApprovalDto(int Id, string? Type, string? Approver, DateTimeOffset? Created);

/// <summary>
/// One task as it ran. <c>id</c> is what <c>task_log</c> takes: unique within a deployment
/// attempt but repeated across stages, so that argument also accepts "stage / id" and lists the
/// candidates on ambiguity. A substitution task's log is often the most direct statement of what
/// value a deploy wrote, and the failure path never reaches it.
/// </summary>
public sealed record ReleaseTaskDto(
    int Id,
    string? Phase,
    string? Job,
    string? Name,
    string? Status,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string? LogTail,
    bool? Truncated);

/// <summary>
/// Shaped like the other waiters: the release as <c>get_release</c> reports it, <c>environment</c>
/// naming the stage waited on, and <c>timedOut</c> only when the wait gave up.
/// </summary>
public sealed record ReleaseWaitResult(
    ReleaseDetailDto Release,
    string Environment,
    int WaitedSeconds,
    bool? TimedOut);

/// <summary>
/// What <c>approve_release</c> did, with the release afterwards so the caller can see whether the
/// deployment it unblocked has started.
/// </summary>
public sealed record ReleaseApprovalResult(ApprovalDto Approval, ReleaseDetailDto Release);

public sealed record ApprovalDto(
    int Id,
    string? Status,
    string? Type,
    string? Environment,
    string? ApprovedBy,
    string? Comments,
    DateTimeOffset? Modified);

// ------------------------------------------------- what a release definition is configured to do
//
// The tools above say what a release did; these say what it was set up to do, which is what
// answers "would editing this file change what deploys". A substitution task carries its target
// files in its own inputs, so a variable list alone cannot settle it.
//
// A secret's value never appears here: `isSecret: true` with no `value` is the whole answer. Same
// rule in the passthrough tool (see Secrets.Mask).

public sealed record ReleaseVariableDto(string Name, string? Value, bool? IsSecret, bool? AllowOverride);

/// <summary>A referenced variable group: what it is, never what is in it.</summary>
public sealed record VariableGroupDto(int Id, string? Name);

/// <summary>
/// One configured task. <c>inputs</c> is the field that matters: a File Transform, Replace Tokens
/// or JSON substitution task names its target files there. Empty inputs are dropped, since a
/// task's schema contributes dozens of them.
/// </summary>
public sealed record ReleaseTaskConfigDto(
    string? Name,
    string? Version,
    // Only when the task is switched off; enabled is the normal case.
    bool? Disabled,
    string? Condition,
    Dictionary<string, string>? Inputs);

/// <summary>
/// Where a phase is set to run. A deployment-group phase carries either <c>tags</c> or
/// <c>allMachines</c>, which spells out the empty tag list: no tags means every machine in the
/// group, including ones added later. <c>healthOption</c>, <c>timeoutMinutes</c> and
/// <c>condition</c> appear only when they differ from the designer's defaults: one target at a
/// time, no timeout, <c>succeeded()</c>.
/// </summary>
public sealed record DeployTargetConfigDto(
    DeploymentGroupRefDto? DeploymentGroup,
    // The agent queue an agentBasedDeployment phase runs on, which is not a deployment group.
    int? AgentQueue,
    List<string>? Tags,
    bool? AllMachines,
    string? HealthOption,
    int? HealthPercent,
    int? TimeoutMinutes,
    string? Condition);

/// <summary>A deployment group by id and name; get_release_definition_targets resolves the machines.</summary>
public sealed record DeploymentGroupRefDto(int Id, string? Name);

public sealed record ReleaseDeployPhaseDto(
    string? Name, string? Type, DeployTargetConfigDto? Target, List<ReleaseTaskConfigDto>? Tasks);

// ------------------------------------------------------------- deployment groups and targets
//
// Where a classic release stage lands. A deployment group is a set of machines; a stage's deploy
// phase names one and picks machines in it by tag. Both are Azure DevOps' own data, not
// organization-specific knowledge, so none of this touches the deployment map. Agent capabilities
// are never returned (the wire models above say why); the escape hatch covers the rare case.

/// <summary>
/// One machine in a deployment group. <c>status</c> appears only when the agent is not online and
/// <c>disabled</c> only when it is switched off; online and enabled is the normal state.
/// </summary>
public sealed record DeploymentMachineDto(
    int Id,
    string? Name,
    string? Status,
    bool? Disabled,
    string? AgentVersion,
    string? Os,
    List<string>? Tags);

/// <summary>
/// A deployment group. <c>machines</c> is absent when the caller did not ask for them or the group
/// has none. <c>machineCount</c> is the service's own count and always present, so a target list
/// can be compared with the whole group.
/// </summary>
public sealed record DeploymentGroupDto(
    int Id,
    string? Name,
    string? Description,
    int? MachineCount,
    List<DeploymentMachineDto>? Machines);

/// <summary>
/// One deploy phase resolved to the machines it would run on now. <c>machines</c> is an empty
/// list when the phase's tags match nothing in its group, which is itself the answer. It is absent
/// when the phase does not run on a deployment group (<c>type</c> says what it runs on) or the
/// group could not be read (<c>error</c> says why).
/// </summary>
public sealed record PhaseTargetsDto(
    string? Phase,
    string? Type,
    DeploymentGroupDto? DeploymentGroup,
    int? AgentQueue,
    List<string>? Tags,
    bool? AllMachines,
    List<DeploymentMachineDto>? Machines,
    string? Error);

public sealed record StageTargetsDto(int Id, string? Name, List<PhaseTargetsDto> Phases);

public sealed record ReleaseTargetsDto(
    int Id, string? Name, List<StageTargetsDto> Environments, string? WebUrl);

/// <summary>
/// One stage of a definition. <c>phases</c> is absent both when the caller asked for no tasks and
/// when the stage runs none; the tool's own description says which.
/// </summary>
public sealed record ReleaseDefinitionEnvironmentConfigDto(
    int Id,
    string? Name,
    List<ReleaseVariableDto>? Variables,
    List<VariableGroupDto>? VariableGroups,
    List<ReleaseDeployPhaseDto>? Phases);

public sealed record ReleaseDefinitionDetailDto(
    int Id,
    string? Name,
    string? Folder,
    string? Description,
    List<ReleaseVariableDto>? Variables,
    List<VariableGroupDto>? VariableGroups,
    List<ReleaseArtifactDto>? Artifacts,
    List<ReleaseDefinitionEnvironmentConfigDto> Environments,
    string? WebUrl);

/// <summary>
/// One place a pattern matched across the project's release definitions. <c>environment</c> is
/// absent when the match is at definition scope, <c>task</c> when it is a variable rather than a
/// task input, and <c>value</c> when the variable is a secret.
/// </summary>
public sealed record ReleaseDefinitionMatchDto(
    int DefinitionId,
    string? Definition,
    string? Environment,
    string Kind,
    string? Task,
    string Key,
    string? Value,
    bool? IsSecret,
    string MatchedIn,
    string? WebUrl);

/// <summary>
/// <c>scanned</c> is how many definitions were read, so an empty result means something. A capped
/// scan sets <c>hasMore</c> rather than passing itself off as complete.
/// </summary>
public sealed record ReleaseDefinitionSearchResult(
    List<ReleaseDefinitionMatchDto> Results, int Scanned, bool? HasMore);

/// <summary>
/// A raw REST response. <c>json</c> carries the parsed body when it is JSON and fits the cap;
/// otherwise <c>text</c> carries it and <c>truncated</c> says so. The way out is a narrower
/// `filter` or the endpoint's own paging, not a bigger cap.
/// </summary>
public sealed record ApiResponseDto(
    int Status,
    string Url,
    string? ContentType,
    JsonElement? Json,
    string? Text,
    bool? Truncated);

/// <summary>
/// Which credential this server is using and whether it still works. A dead sign-in is reported
/// as data rather than thrown: it is the answer this tool exists to give.
/// </summary>
public sealed record AuthStatusDto(
    bool SignedIn,
    string Credential,
    string? Account,
    string? Identity,
    string? TenantId,
    string? ClientId,
    string? Authority,
    DateTimeOffset? SignedInOn,
    DateTimeOffset? TokenExpires,
    int? TokenExpiresInMinutes,
    string? Organization,
    string? Project,
    string? Error,
    PatStatusDto? Pat);

/// <summary>
/// AZURE_DEVOPS_PAT, probed separately because sessions reach for it as a fallback when a tool
/// fails and need one line telling them it is dead. Absent when the variable is unset.
/// </summary>
public sealed record PatStatusDto(bool Valid, string? Identity, string? Error);

// Search envelopes always carry `total`, the service's overall match count, so an empty result
// list still says whether nothing matched (0) or the caller's limit cut it short (with hasMore).
// Snippets are the matched text with the highlight markers stripped.

public sealed record CodeSearchResult(List<CodeSearchHitDto> Results, int Total, bool? HasMore);

/// <summary>One matching file: how many places matched in it, and up to three matched snippets.</summary>
public sealed record CodeSearchHitDto(
    string? Path,
    string? Repo,
    string? Branch,
    int? Matches,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

public sealed record WorkItemSearchResult(List<WorkItemSearchHitDto> Results, int Total, bool? HasMore);

public sealed record WorkItemSearchHitDto(
    int? Id,
    string? Type,
    string? Title,
    string? State,
    string? AssignedTo,
    DateTimeOffset? Changed,
    List<string>? Tags,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

public sealed record WikiSearchResult(List<WikiSearchHitDto> Results, int Total, bool? HasMore);

public sealed record WikiSearchHitDto(
    string? Path,
    string? Wiki,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

/// <summary>
/// What `add_pull_request_comment` created, echoed back so a follow-up reply can be addressed at
/// the thread without a second call.
/// </summary>
public sealed record PullRequestCommentResult(int PullRequestId, int ThreadId, CommentDto Comment, string? WebUrl);

/// <summary>Envelope for the deployment map: one status per configured deployable.</summary>
public sealed record DeploymentStatusResult(List<DeployableStatusDto> Deployables);

/// <summary>
/// One deployable's production state: what is out (the release for a classic deployable, the run
/// for a pipeline one), the build it shipped, the version that build was made from (a TFVC
/// <c>changeset</c> or a git <c>commit</c>, whichever the build's repository implies), and what
/// has landed since: changesets under the deployable's paths, or commits on its branch.
/// <c>containsChangeset</c>/<c>affects</c> appear only when the caller asked about a specific
/// changeset, and only for a TFVC-built deployable. A deployable that could not be evaluated
/// reports <c>error</c> instead of failing the whole call.
/// </summary>
public sealed record DeployableStatusDto(
    string Name,
    string? Note,
    string? ReleaseDefinition,
    string? Environment,
    string? Pipeline,
    string? Release,
    DateTimeOffset? Deployed,
    string? Build,
    int? Changeset,
    string? Commit,
    string? Branch,
    string? Repository,
    List<string>? Paths,
    int? UndeployedCount,
    List<ChangesetDto>? Undeployed,
    List<CommitDto>? UndeployedCommits,
    bool? HasMore,
    bool? ContainsChangeset,
    bool? Affects,
    string? Error,
    string? WebUrl);

public sealed record ChangesetDto(int Id, string? Author, DateTimeOffset? Created, string? Comment);

public sealed record CommitDto(string Id, string? Author, DateTimeOffset? Date, string? Comment);

/// <summary>
/// What was filtered out, so "nothing there" is distinguishable from "everything was filtered".
/// Each count is null when it did not fire.
/// </summary>
public sealed record SkippedDto(int? Deleted, int? System, int? Succeeded);

internal sealed class SkipCounter
{
    public int Deleted;
    public int System;
    public int Succeeded;

    public SkippedDto? ToDto() => Deleted == 0 && System == 0 && Succeeded == 0
        ? null
        : new SkippedDto(
            Deleted == 0 ? null : Deleted,
            System == 0 ? null : System,
            Succeeded == 0 ? null : Succeeded);
}

// -------------------------------------------------------------------------- mapping

/// <summary>
/// Wire shape to output shape. Pure and static, so tests reach all of it without an Azure DevOps
/// organization behind it.
/// </summary>
internal static class Mapping
{
    /// <summary>
    /// Whether a build's <c>status</c> means it has stopped moving. Verdict is in <c>result</c>
    /// and progress in <c>status</c>, and only <c>completed</c> is an end state. <c>cancelling</c>
    /// is not: it becomes <c>completed</c> once the cancellation lands, so a waiter treating it as
    /// terminal would report a run still winding down. An absent status counts as terminal.
    /// </summary>
    internal static bool IsTerminalRunStatus(string? status) =>
        status is null || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pull request's <c>status</c> means it has stopped moving. <c>active</c> is the
    /// only state a pull request leaves on its own; <c>completed</c> and <c>abandoned</c> are both
    /// ends and the DTO's status says which. Anything unrecognized or absent counts as terminal,
    /// so a waiter returns what it sees instead of polling an unknown state until the timeout.
    /// </summary>
    internal static bool IsTerminalPullRequestStatus(string? status) =>
        !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fields worth asking for in list results. Anything else is padding.</summary>
    internal static readonly string[] ListFields =
    [
        "System.TeamProject", "System.WorkItemType", "System.Title", "System.State",
        "System.AssignedTo", "System.ChangedDate", "System.AreaPath", "System.IterationPath",
        "System.Tags", "Microsoft.VSTS.Common.Priority",
    ];

    internal static ProjectDto Project(WireProject p) => new(
        p.Id,
        p.Name,
        TrimDescription(p.Description, p.Name),
        // "wellFormed" is the state of every project anyone can use.
        string.Equals(p.State, "wellFormed", StringComparison.OrdinalIgnoreCase) ? null : p.State,
        string.Equals(p.Visibility, "private", StringComparison.OrdinalIgnoreCase) ? null : p.Visibility);

    internal static RepoDto Repo(WireRepo r) => new(
        r.Id, r.Name, ShortBranch(r.DefaultBranch), r.WebUrl, r.IsDisabled is true ? true : null);

    internal static PullRequestDto PullRequest(WirePullRequest pr, string orgUrl, bool includeRepo) => new(
        pr.PullRequestId,
        pr.Title,
        pr.Status,
        includeRepo ? pr.Repository?.Name : null,
        pr.CreatedBy?.DisplayName,
        pr.CreationDate,
        ShortBranch(pr.SourceRefName),
        ShortBranch(pr.TargetRefName),
        pr.IsDraft is true ? true : null,
        // "succeeded" says nothing; every other merge status is a reason a PR is stuck.
        string.Equals(pr.MergeStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? null : pr.MergeStatus,
        Reviewers(pr.Reviewers),
        PullRequestUrl(orgUrl, pr));

    internal static List<ReviewerDto>? Reviewers(List<WireReviewer>? reviewers)
    {
        var mapped = (reviewers ?? [])
            .Where(r => r.DisplayName is not null)
            .Select(r => new ReviewerDto(r.DisplayName, Vote(r.Vote), r.IsRequired is true ? true : null))
            .ToList();
        return mapped.Count > 0 ? mapped : null;
    }

    /// <summary>The vote scale is documented as -10..10. No vote is the default and says nothing.</summary>
    internal static string? Vote(int? vote) => vote switch
    {
        10 => "approved",
        5 => "approved with suggestions",
        -5 => "waiting for author",
        -10 => "rejected",
        _ => null,
    };

    /// <summary>
    /// A pull request thread, or null when nothing survives filtering. Deleted comments and the
    /// system threads posted for every push, vote and policy evaluation are counted, not dropped.
    /// </summary>
    internal static ThreadDto? Thread(WireThread t, bool includeSystem, int bodyLimit, SkipCounter counts)
    {
        if (t.IsDeleted is true)
        {
            counts.Deleted++;
            return null;
        }

        var comments = new List<CommentDto>();
        foreach (var c in t.Comments ?? [])
        {
            if (c.IsDeleted is true)
            {
                counts.Deleted++;
                continue;
            }
            var isSystem = c.CommentType is not null &&
                           !string.Equals(c.CommentType, "text", StringComparison.OrdinalIgnoreCase);
            if (isSystem && !includeSystem)
            {
                counts.System++;
                continue;
            }
            var (body, truncated) = Text.Truncate(Text.FromMarkdown(c.Content), bodyLimit);
            comments.Add(new CommentDto(
                c.Id,
                c.Author?.DisplayName,
                c.PublishedDate,
                isSystem ? c.CommentType : null, // omit the redundant default "text"
                body,
                truncated));
        }

        if (comments.Count == 0)
        {
            return null; // an all-system thread: already counted, nothing left to show
        }

        return new ThreadDto(
            t.Id,
            t.Status,
            t.ThreadContext?.FilePath,
            t.ThreadContext?.RightFileStart?.Line ?? t.ThreadContext?.LeftFileStart?.Line,
            comments);
    }

    internal static CommentDto? WorkItemComment(WireWorkItemComment c, int bodyLimit, SkipCounter counts)
    {
        if (c.IsDeleted is true)
        {
            counts.Deleted++;
            return null;
        }
        var (body, truncated) = Text.Truncate(Text.FromHtml(c.Text), bodyLimit);
        return new CommentDto(c.Id, c.CreatedBy?.DisplayName, c.CreatedDate, null, body, truncated);
    }

    internal static WorkItemDto WorkItem(WireWorkItem w, string orgUrl)
    {
        var project = Str(w.Fields, "System.TeamProject");
        return new WorkItemDto(
            w.Id,
            Str(w.Fields, "System.WorkItemType"),
            Str(w.Fields, "System.Title"),
            Str(w.Fields, "System.State"),
            Person(w.Fields, "System.AssignedTo"),
            Date(w.Fields, "System.ChangedDate"),
            TrimPath(Str(w.Fields, "System.AreaPath"), project),
            TrimPath(Str(w.Fields, "System.IterationPath"), project),
            Tags(Str(w.Fields, "System.Tags")),
            Int(w.Fields, "Microsoft.VSTS.Common.Priority"),
            WorkItemUrl(orgUrl, project, w.Id));
    }

    internal static WorkItemDetailDto WorkItemDetail(
        WireWorkItem w, int bodyLimit, string orgUrl, List<CommentDto>? comments, SkippedDto? skipped)
    {
        var project = Str(w.Fields, "System.TeamProject");
        var (description, dTrunc) = Text.Truncate(Text.FromHtml(Str(w.Fields, "System.Description")), bodyLimit);
        var (repro, rTrunc) = Text.Truncate(
            Text.FromHtml(Str(w.Fields, "Microsoft.VSTS.TCM.ReproSteps")), bodyLimit);
        var (criteria, cTrunc) = Text.Truncate(
            Text.FromHtml(Str(w.Fields, "Microsoft.VSTS.Common.AcceptanceCriteria")), bodyLimit);

        return new WorkItemDetailDto(
            w.Id,
            Str(w.Fields, "System.WorkItemType"),
            Str(w.Fields, "System.Title"),
            Str(w.Fields, "System.State"),
            // Reason repeats the state often enough ("New"/"New", "Done"/"Completed") to be noise.
            TrimDescription(Str(w.Fields, "System.Reason"), Str(w.Fields, "System.State")),
            Person(w.Fields, "System.AssignedTo"),
            Person(w.Fields, "System.CreatedBy"),
            Date(w.Fields, "System.CreatedDate"),
            Date(w.Fields, "System.ChangedDate"),
            TrimPath(Str(w.Fields, "System.AreaPath"), project),
            TrimPath(Str(w.Fields, "System.IterationPath"), project),
            Tags(Str(w.Fields, "System.Tags")),
            Int(w.Fields, "Microsoft.VSTS.Common.Priority"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.RemainingWork"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.CompletedWork"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.StoryPoints"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.Effort"),
            description,
            repro,
            criteria,
            dTrunc is true || rTrunc is true || cTrunc is true ? true : null,
            Relations(w.Relations),
            comments is { Count: > 0 } ? comments : null,
            skipped,
            WorkItemUrl(orgUrl, project, w.Id));
    }

    internal static List<RelationDto>? Relations(List<WireRelation>? relations)
    {
        var mapped = (relations ?? [])
            .Where(r => r.Rel is not null)
            .Select(r => new RelationDto(
                ShortRelation(r.Rel),
                AttributeString(r.Attributes, "name"),
                LinkedWorkItemId(r.Url),
                // The id says everything a work item link has to say. Keep the url only when it
                // points at something else (a commit, a pull request, an attachment).
                LinkedWorkItemId(r.Url) is null ? r.Url : null))
            .ToList();
        return mapped.Count > 0 ? mapped : null;
    }

    internal static ChangesetDto Changeset(WireTfvcChangesetRef c) =>
        new(c.ChangesetId, c.Author?.DisplayName, c.CreatedDate, c.Comment);

    internal static CommitDto Commit(WireGitCommitRef c) =>
        new(c.CommitId ?? "", c.Author?.Name, c.Author?.Date, c.Comment);

    internal static PipelineDto Pipeline(WirePipeline p) => new(
        p.Id, p.Name, string.Equals(p.Folder, "\\", StringComparison.Ordinal) ? null : p.Folder);

    internal static PipelineRunDto Run(WireBuild b, string orgUrl, string? project) => new(
        b.Id,
        b.BuildNumber,
        // A finished run says everything through `result`; only unfinished runs need a status.
        string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase) ? null : b.Status,
        b.Result,
        b.QueueTime,
        b.FinishTime,
        ShortBranch(b.SourceBranch),
        b.RequestedFor?.DisplayName,
        RunUrl(orgUrl, b.Project?.Name ?? project, b.Id));

    /// <summary>
    /// Walks the build timeline and reports only what failed, with the stage and job it belongs to.
    /// Records that succeeded are counted into <paramref name="counts"/>; ones that never ran are
    /// neither listed nor counted. Each step carries the log url of its own record, since a task
    /// name is not unique across jobs and pairing by name could attach the wrong log.
    /// </summary>
    internal static List<FailedStep> FailedSteps(WireTimeline timeline, int maxErrors, SkipCounter counts)
    {
        var records = timeline.Records ?? [];
        var byId = records.Where(r => r.Id is not null).ToDictionary(r => r.Id!, r => r);

        var failed = new List<FailedStep>();
        foreach (var record in records.OrderBy(r => r.Order ?? int.MaxValue))
        {
            if (!IsRunTaskFailure(record.Result))
            {
                if (IsRunTaskSuccess(record.Result))
                {
                    counts.Succeeded++;
                }
                continue;
            }
            // Stage and job failures roll up the task that actually failed. Listing them too
            // would report the same failure three times.
            if (!string.Equals(record.Type, "Task", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (stage, job) = Ancestors(record, byId);
            var errors = (record.Issues ?? [])
                .Where(i => string.Equals(i.Type, "error", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Message)
                .OfType<string>()
                .Take(maxErrors)
                .ToList();

            failed.Add(new FailedStep(
                new FailedStepDto(
                    stage, job, record.Name, record.Result, errors.Count > 0 ? errors : null, null, null),
                record.Log?.Url));
        }
        return failed;
    }

    internal static bool IsRunTaskFailure(string? result) =>
        string.Equals(result, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "abandoned", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Not the negation of <see cref="IsRunTaskFailure"/>: a skipped stage and a task that has not
    /// started are neither, and counting them as passing overstates what the run did.
    /// </summary>
    internal static bool IsRunTaskSuccess(string? result) =>
        string.Equals(result, "succeeded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "succeededWithIssues", StringComparison.OrdinalIgnoreCase);

    /// <summary>Climbs parentId to name the stage and job a task record sits under.</summary>
    private static (string? Stage, string? Job) Ancestors(
        WireTimelineRecord record, Dictionary<string, WireTimelineRecord> byId)
    {
        string? stage = null;
        string? job = null;
        var current = record;
        var depth = 0;
        while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && depth++ < 10)
        {
            if (string.Equals(parent.Type, "Stage", StringComparison.OrdinalIgnoreCase))
            {
                stage ??= parent.Name;
            }
            else if (string.Equals(parent.Type, "Job", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parent.Type, "Phase", StringComparison.OrdinalIgnoreCase))
            {
                job ??= parent.Name;
            }
            current = parent;
        }
        return (stage, job);
    }

    /// <summary>Keeps the end of a log: a build failure is explained by its last lines.</summary>
    internal static (string? Tail, bool? Truncated) LogTail(string log, int lines)
    {
        if (lines <= 0 || string.IsNullOrWhiteSpace(log))
        {
            return (null, null);
        }
        var all = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var kept = all.Length <= lines ? all : all[^lines..];
        return (string.Join("\n", kept), all.Length > lines ? true : null);
    }

    // ------------------------------------------------------- classic release pipelines

    internal static ReleaseDefinitionDto ReleaseDefinition(
        WireReleaseDefinition d, string orgUrl, string? project) => new(
        d.Id,
        d.Name,
        // Release definitions live in folders spelled "\", the same as a pipeline's root folder.
        string.Equals(d.Path, "\\", StringComparison.Ordinal) ? null : d.Path,
        // Rank order: the order they deploy in. Names, not ids; the release tools take either.
        (d.Environments ?? []).OrderBy(e => e.Rank ?? 0).Select(e => e.Name).OfType<string>().ToList()
            is { Count: > 0 } environments ? environments : null,
        ReleaseDefinitionUrl(orgUrl, project, d.Id));

    internal static ReleaseDto Release(WireRelease r, string orgUrl, string? project) => new(
        r.Id,
        r.Name,
        // "active" is the state of every release that was not abandoned or left as a draft.
        string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase) ? null : r.Status,
        r.CreatedOn,
        r.CreatedBy?.DisplayName,
        r.Reason,
        (r.Environments ?? []).OrderBy(e => e.Rank ?? 0)
            .Select(e => new ReleaseEnvironmentDto(e.Id, e.Name, e.Status))
            .ToList() is { Count: > 0 } environments ? environments : null,
        ReleaseUrl(orgUrl, project, r.Id));

    internal static ReleaseArtifactDto ReleaseArtifact(WireReleaseArtifact a)
    {
        var version = a.DefinitionReference?.Version;
        return new ReleaseArtifactDto(
            a.Alias,
            a.Type,
            a.DefinitionReference?.Definition?.Name,
            version?.Name,
            // The version id of a Build artifact is the build/run id, as a string on the wire.
            string.Equals(a.Type, "Build", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(version?.Id, CultureInfo.InvariantCulture, out var buildId) ? buildId : null,
            a.IsPrimary is true ? true : null);
    }

    /// <summary>
    /// The attempt to report: the highest-numbered one. A redeploy adds an attempt instead of
    /// replacing the previous one, so the first entry can be a failure that was later retried
    /// successfully.
    /// </summary>
    internal static WireDeploymentAttempt? LatestAttempt(WireReleaseEnvironment env) =>
        (env.DeploySteps ?? []).OrderByDescending(d => d.Attempt).FirstOrDefault();

    /// <summary>
    /// Why a stage failed: the failed tasks of its latest attempt, with the phase and job they sit
    /// under. Tasks that passed are counted rather than listed, as <see cref="FailedSteps"/> does;
    /// tasks that never ran are neither. Tasks need <c>$expand=tasks</c> on the read.
    /// </summary>
    internal static List<FailedStep> ReleaseFailedSteps(
        WireReleaseEnvironment env, int maxErrors, SkipCounter counts, bool countSucceeded = true)
    {
        var failed = new List<FailedStep>();
        if (LatestAttempt(env) is not { } attempt)
        {
            return failed;
        }
        foreach (var phase in (attempt.ReleaseDeployPhases ?? []).OrderBy(p => p.Rank ?? 0))
        {
            foreach (var job in phase.DeploymentJobs ?? [])
            {
                foreach (var task in (job.Tasks ?? []).OrderBy(t => t.Rank ?? 0))
                {
                    if (!IsReleaseTaskFailure(task.Status))
                    {
                        // Counted only while nothing else reports them: include_tasks lists the
                        // passing tasks, and then `skipped.succeeded` would claim they were
                        // filtered out of a result they are sitting in.
                        if (countSucceeded && IsReleaseTaskSuccess(task.Status))
                        {
                            counts.Succeeded++;
                        }
                        continue;
                    }
                    var errors = (task.Issues ?? [])
                        .Where(i => string.Equals(i.IssueType, "error", StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.Message)
                        .OfType<string>()
                        .Take(maxErrors)
                        .ToList();
                    failed.Add(new FailedStep(
                        new FailedStepDto(
                            phase.Name, job.Job?.Name, task.Name, task.Status,
                            errors.Count > 0 ? errors : null, null, null),
                        task.LogUrl));
                }
            }
        }
        return failed;
    }

    /// <summary>
    /// Release Management reports a task's verdict under two spellings apiece, <c>failed</c> and
    /// <c>failure</c>, <c>succeeded</c> and <c>success</c>, both in its own enum. Matching only
    /// the familiar one silently drops half the failures.
    /// </summary>
    internal static bool IsReleaseTaskFailure(string? status) =>
        status is not null &&
        (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Not the negation of <see cref="IsReleaseTaskFailure"/>: skipped and pending tasks are
    /// neither, and counting them as passing overstates what the deployment did.
    /// <c>partiallySucceeded</c> is the release side's <c>succeededWithIssues</c>.
    /// </summary>
    internal static bool IsReleaseTaskSuccess(string? status) =>
        status is not null &&
        (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("partiallySucceeded", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The approvals a stage is actually waiting on: pending, minus the automated placeholders
    /// Azure DevOps records for stages that need no approval.
    /// </summary>
    internal static List<WireReleaseApproval> PendingApprovals(WireReleaseEnvironment env) =>
        ((env.PreDeployApprovals ?? []).Concat(env.PostDeployApprovals ?? []))
        .Where(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))
        .Where(a => a.IsAutomated is not true)
        .OrderBy(a => a.Rank ?? 0)
        .ToList();

    internal static PendingApprovalDto PendingApproval(WireReleaseApproval a) =>
        new(a.Id, a.ApprovalType, a.Approver?.DisplayName ?? a.Approver?.UniqueName, a.CreatedOn);

    internal static ApprovalDto Approval(WireReleaseApproval a) => new(
        a.Id,
        a.Status,
        a.ApprovalType,
        a.ReleaseEnvironment?.Name,
        a.ApprovedBy?.DisplayName ?? a.ApprovedBy?.UniqueName,
        a.Comments,
        a.ModifiedOn);

    /// <summary>
    /// One stage, given the failed steps already resolved for it (the tool fetches their logs, so
    /// that part cannot be pure).
    /// </summary>
    internal static ReleaseEnvironmentDetailDto ReleaseEnvironment(
        WireReleaseEnvironment env, List<FailedStepDto> failedSteps, List<ReleaseTaskDto>? tasks = null)
    {
        var attempt = LatestAttempt(env);
        var pending = PendingApprovals(env).Select(PendingApproval).ToList();
        return new ReleaseEnvironmentDetailDto(
            env.Id,
            env.Name,
            env.Status,
            // On a green stage this only ever says "Approved"; everywhere else it is the signal.
            // An environment has no `failed` status: a failed deployment reports as `rejected`
            // with operationStatus PhaseFailed, so dropping this would make a broken deployment
            // indistinguishable from one a person turned down. It also separates "an agent is
            // running it" from "held at a manual intervention", both inProgress.
            string.Equals(env.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
                ? null
                : attempt?.OperationStatus,
            // A first attempt says nothing; a later one says somebody retried.
            attempt is { Attempt: > 1 } ? attempt.Attempt : null,
            attempt?.QueuedOn,
            // lastModifiedOn is a finish time only once the stage has finished; while it runs it
            // is just when something last happened.
            attempt is not null && IsTerminalEnvironmentStatus(env.Status) ? attempt.LastModifiedOn : null,
            attempt?.RequestedFor?.DisplayName,
            pending.Count > 0 ? pending : null,
            failedSteps.Count > 0 ? failedSteps : null,
            tasks is { Count: > 0 } ? tasks : null);
    }

    /// <summary>
    /// Every task of the stage's latest attempt, in the order it ran, with the phase and job it
    /// sits under. Unlike <see cref="ReleaseFailedSteps"/> this lists what passed and what was
    /// skipped, since the caller asked what the stage runs. Tasks need <c>$expand=tasks</c>.
    /// </summary>
    internal static List<ReleaseTaskEntry> ReleaseTasks(WireReleaseEnvironment env)
    {
        var tasks = new List<ReleaseTaskEntry>();
        if (LatestAttempt(env) is not { } attempt)
        {
            return tasks;
        }
        foreach (var phase in (attempt.ReleaseDeployPhases ?? []).OrderBy(p => p.Rank ?? 0))
        {
            foreach (var job in phase.DeploymentJobs ?? [])
            {
                foreach (var task in (job.Tasks ?? []).OrderBy(t => t.Rank ?? 0))
                {
                    tasks.Add(new ReleaseTaskEntry(
                        new ReleaseTaskDto(
                            task.Id, phase.Name, job.Job?.Name, task.Name, task.Status,
                            task.StartTime, task.FinishTime, null, null),
                        task.LogUrl));
                }
            }
        }
        return tasks;
    }

    /// <summary>
    /// The release around its stages. <paramref name="descriptionLimit"/> caps the one free-text
    /// field. An absent description arrives as <c>""</c>, not as a missing field, and the
    /// serializer only drops nulls.
    /// </summary>
    internal static ReleaseDetailDto ReleaseDetail(
        WireRelease r, List<ReleaseEnvironmentDetailDto> environments, SkippedDto? skipped,
        int descriptionLimit, string orgUrl, string? project) => new(
        r.Id,
        r.Name,
        r.ReleaseDefinition?.Name,
        string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase) ? null : r.Status,
        r.CreatedOn,
        r.CreatedBy?.DisplayName,
        r.Reason,
        r.Description is { Length: > 0 } description
            ? Text.Truncate(description, descriptionLimit).Body
            : null,
        (r.Artifacts ?? []).Select(ReleaseArtifact).ToList() is { Count: > 0 } artifacts ? artifacts : null,
        environments,
        skipped,
        ReleaseUrl(orgUrl, project, r.Id));

    /// <summary>
    /// Whether a stage's status means it has stopped moving. <c>notStarted</c> is not terminal:
    /// it is what an untriggered stage reports, and a caller waiting for an automatic promotion is
    /// waiting for exactly that transition. An unrecognized status counts as terminal, so a waiter
    /// returns what it sees instead of polling an unknown state until the timeout.
    /// </summary>
    internal static bool IsTerminalEnvironmentStatus(string? status) => status switch
    {
        null => true,
        _ when status.Equals("notStarted", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("inProgress", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("queued", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("scheduled", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("undefined", StringComparison.OrdinalIgnoreCase) => false,
        _ => true,
    };

    // ------------------------------------------- what a release definition is configured to do

    /// <summary>
    /// Definition- or environment-scope variables, sorted by name so two reads of the same
    /// definition compare. A secret is reported by name with <c>isSecret</c> and no value. Azure
    /// DevOps already returns null for one; this enforces it here as well.
    /// </summary>
    internal static List<ReleaseVariableDto>? ReleaseVariables(
        Dictionary<string, WireReleaseVariable>? variables) =>
        (variables ?? [])
        .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
        .Select(v => new ReleaseVariableDto(
            v.Key,
            v.Value?.IsSecret is true ? null : v.Value?.Value,
            v.Value?.IsSecret is true ? true : null,
            // Overridable at queue time is the interesting case; the default is not.
            v.Value?.AllowOverride is true ? true : null))
        .ToList() is { Count: > 0 } list ? list : null;

    /// <summary>
    /// The groups a scope pulls in, id and name. The names come from a separate lookup that may
    /// not have answered; the id still identifies the group.
    /// </summary>
    internal static List<VariableGroupDto>? VariableGroups(
        List<int>? ids, IReadOnlyDictionary<int, string> names) =>
        (ids ?? [])
        .Select(id => new VariableGroupDto(id, names.TryGetValue(id, out var name) ? name : null))
        .ToList() is { Count: > 0 } groups ? groups : null;

    /// <summary>
    /// One configured task. Empty inputs are dropped: a task's schema contributes every input it
    /// declares whether or not the definition set one.
    /// </summary>
    internal static ReleaseTaskConfigDto TaskConfig(WireWorkflowTask task) => new(
        task.Name,
        task.Version,
        task.Enabled is false ? true : null,
        Condition(task.Condition),
        (task.Inputs ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Value))
            .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(i => i.Key, i => i.Value!) is { Count: > 0 } inputs ? inputs : null);

    /// <summary>
    /// A task's or phase's run condition, unless it is a default the designer writes on its own.
    /// "succeededContinueOnError" is what the default checkbox produces.
    /// </summary>
    internal static string? Condition(string? condition) =>
        condition is { Length: > 0 } &&
        !condition.Equals("succeeded()", StringComparison.OrdinalIgnoreCase) &&
        !condition.Equals("succeededContinueOnError", StringComparison.OrdinalIgnoreCase)
            ? condition
            : null;

    /// <summary>
    /// Where a phase is set to run. Null when the phase has no <c>deploymentInput</c> worth
    /// reporting, such as a server-side phase at its defaults. <c>queueId</c> is a deployment
    /// group only on a machine-group phase; on an agent-based phase it is an agent queue.
    /// </summary>
    internal static DeployTargetConfigDto? DeployTarget(
        WireDeployPhase phase, IReadOnlyDictionary<int, string> deploymentGroupNames)
    {
        if (phase.DeploymentInput is not { } input)
        {
            return null;
        }
        var machineGroup = Targeting.IsMachineGroup(phase.PhaseType);
        var tags = Targeting.Tags(input.Tags);
        // One target at a time is the default; only the rolling options are worth reporting.
        var health = input.DeploymentHealthOption is { Length: > 0 } option &&
                     !option.Equals("OneTargetAtATime", StringComparison.OrdinalIgnoreCase)
            ? option
            : null;
        var target = new DeployTargetConfigDto(
            machineGroup && input.QueueId is { } groupId
                ? new DeploymentGroupRefDto(
                    groupId, deploymentGroupNames.TryGetValue(groupId, out var name) ? name : null)
                : null,
            !machineGroup && input.QueueId is > 0 ? input.QueueId : null,
            machineGroup && tags.Count > 0 ? tags : null,
            machineGroup && tags.Count == 0 ? true : null,
            health,
            health is not null && input.HealthPercent is > 0 ? input.HealthPercent : null,
            input.TimeoutInMinutes is > 0 ? input.TimeoutInMinutes : null,
            Condition(input.Condition));
        return target is
        {
            DeploymentGroup: null, AgentQueue: null, Tags: null, AllMachines: null,
            HealthOption: null, TimeoutMinutes: null, Condition: null,
        }
            ? null
            : target;
    }

    internal static ReleaseDeployPhaseDto DeployPhase(
        WireDeployPhase phase, IReadOnlyDictionary<int, string> deploymentGroupNames) => new(
        phase.Name,
        phase.PhaseType,
        DeployTarget(phase, deploymentGroupNames),
        (phase.WorkflowTasks ?? []).Select(TaskConfig).ToList() is { Count: > 0 } tasks ? tasks : null);

    internal static ReleaseDefinitionEnvironmentConfigDto ReleaseDefinitionEnvironment(
        WireReleaseDefEnvironmentDetail env, IReadOnlyDictionary<int, string> groupNames, bool includeTasks,
        IReadOnlyDictionary<int, string> deploymentGroupNames) => new(
        env.Id,
        env.Name,
        ReleaseVariables(env.Variables),
        VariableGroups(env.VariableGroups, groupNames),
        includeTasks && (env.DeployPhases ?? []).OrderBy(p => p.Rank ?? 0)
            .Select(p => DeployPhase(p, deploymentGroupNames)).ToList()
            is { Count: > 0 } phases
            ? phases
            : null);

    /// <summary>
    /// The deployment groups a definition's machine-group phases name, once each, in id order.
    /// Agent-based phases are skipped: their queue is not a group.
    /// </summary>
    internal static IReadOnlyList<int> ReferencedDeploymentGroups(WireReleaseDefinitionDetail d) =>
        [.. (d.Environments ?? [])
            .SelectMany(e => e.DeployPhases ?? [])
            .Where(p => Targeting.IsMachineGroup(p.PhaseType))
            .Select(p => p.DeploymentInput?.QueueId)
            .OfType<int>()
            .Distinct()
            .OrderBy(id => id)];

    /// <summary>
    /// One machine. Online and enabled is the state that deploys, so only other states are
    /// reported. Tags are sorted so machines with the same set read the same.
    /// </summary>
    internal static DeploymentMachineDto DeploymentMachine(WireDeploymentMachine machine) => new(
        machine.Id,
        machine.Agent?.Name,
        machine.Agent?.Status is { Length: > 0 } status &&
        !status.Equals("online", StringComparison.OrdinalIgnoreCase)
            ? status
            : null,
        machine.Agent?.Enabled is false ? true : null,
        machine.Agent?.Version,
        machine.Agent?.OsDescription?.Trim() is { Length: > 0 } os ? os : null,
        Targeting.Tags(machine.Tags).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()
            is { Count: > 0 } tags ? tags : null);

    internal static List<DeploymentMachineDto>? DeploymentMachines(IEnumerable<WireDeploymentMachine>? machines) =>
        (machines ?? [])
        .OrderBy(m => m.Agent?.Name, StringComparer.OrdinalIgnoreCase)
        .Select(DeploymentMachine)
        .ToList() is { Count: > 0 } list ? list : null;

    /// <summary>
    /// A group, with its machines when asked. <c>machineCount</c> falls back to the machines in
    /// hand, so a target list can always be compared with the whole group.
    /// </summary>
    internal static DeploymentGroupDto DeploymentGroup(WireDeploymentGroup group, bool includeMachines) => new(
        group.Id,
        group.Name,
        group.Description is { Length: > 0 } description ? description : null,
        group.MachineCount ?? group.Machines?.Count,
        includeMachines ? DeploymentMachines(group.Machines) : null);

    /// <summary>
    /// Every variable group id a definition references, at either scope, once each. The name
    /// lookup gets these; the groups' contents are never read.
    /// </summary>
    internal static IReadOnlyList<int> ReferencedVariableGroups(WireReleaseDefinitionDetail d) =>
        [.. (d.VariableGroups ?? [])
            .Concat((d.Environments ?? []).SelectMany(e => e.VariableGroups ?? []))
            .Distinct()
            .OrderBy(id => id)];

    /// <summary>
    /// <paramref name="deploymentGroupNames"/> adds each phase's group name. Like the variable
    /// group names it comes from a lookup that may have failed; the id identifies the group anyway.
    /// </summary>
    internal static ReleaseDefinitionDetailDto ReleaseDefinitionDetail(
        WireReleaseDefinitionDetail d, IReadOnlyDictionary<int, string> groupNames, bool includeTasks,
        string orgUrl, string? project, IReadOnlyDictionary<int, string>? deploymentGroupNames = null) => new(
        d.Id,
        d.Name,
        string.Equals(d.Path, "\\", StringComparison.Ordinal) ? null : d.Path,
        TrimDescription(d.Description, d.Name),
        ReleaseVariables(d.Variables),
        VariableGroups(d.VariableGroups, groupNames),
        (d.Artifacts ?? []).Select(ReleaseArtifact).ToList() is { Count: > 0 } artifacts ? artifacts : null,
        (d.Environments ?? []).OrderBy(e => e.Rank ?? 0)
            .Select(e => ReleaseDefinitionEnvironment(
                e, groupNames, includeTasks, deploymentGroupNames ?? new Dictionary<int, string>()))
            .ToList(),
        ReleaseDefinitionUrl(orgUrl, project, d.Id));

    // ----------------------------------------------------------------- search results

    /// <summary>Snippets a code result keeps. `matches` still reports how many places matched.</summary>
    private const int MaxCodeSnippets = 3;

    internal static CodeSearchHitDto CodeSearchHit(WireCodeResult r, int bodyLimit, string orgUrl, string project)
    {
        var hits = r.Matches?.Content ?? [];
        var snippets = hits
            .Select(h => Text.FromHighlight(h.CodeSnippet))
            .OfType<string>()
            .Distinct()
            .Take(MaxCodeSnippets)
            .ToList();
        var (snippet, truncated) = Text.Truncate(
            snippets.Count > 0 ? string.Join("\n…\n", snippets) : null, bodyLimit);
        return new CodeSearchHitDto(
            r.Path,
            r.Repository?.Name,
            r.Versions is [{ BranchName: { Length: > 0 } branch }, ..] ? branch : null,
            hits.Count > 0 ? hits.Count : null, // a filename-only match has no content matches
            snippet,
            truncated,
            CodeUrl(orgUrl, project, r));
    }

    internal static WorkItemSearchHitDto WorkItemSearchHit(
        WireWorkItemSearchResult r, int bodyLimit, string orgUrl)
    {
        int? id = int.TryParse(
            SearchField(r.Fields, "system.id"), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var (snippet, truncated) = Snippet(
            r.Hits, bodyLimit,
            // Fields already carried on the DTO: a highlight there would only repeat them.
            "system.id", "system.workitemtype", "system.title", "system.state", "system.assignedto",
            "system.tags");
        return new WorkItemSearchHitDto(
            id,
            SearchField(r.Fields, "system.workitemtype"),
            SearchField(r.Fields, "system.title"),
            SearchField(r.Fields, "system.state"),
            PersonName(SearchField(r.Fields, "system.assignedto")),
            SearchField(r.Fields, "system.changeddate") is { } changed &&
            DateTimeOffset.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                ? ts
                : null,
            Tags(SearchField(r.Fields, "system.tags")),
            snippet,
            truncated,
            id is { } wid && r.Project?.Name is { } project ? WorkItemUrl(orgUrl, project, wid) : null);
    }

    internal static WikiSearchHitDto WikiSearchHit(WireWikiResult r, int bodyLimit, string orgUrl, string project)
    {
        // The path names the page. A highlight on the file name would only repeat it.
        var (snippet, truncated) = Snippet(r.Hits, bodyLimit, "fileNames");
        return new WikiSearchHitDto(r.Path, r.Wiki?.Name, snippet, truncated, WikiUrl(orgUrl, project, r));
    }

    /// <summary>
    /// The matched text of one result: every highlight not on an excluded field, deduplicated,
    /// joined and truncated at the body limit.
    /// </summary>
    internal static (string? Snippet, bool? Truncated) Snippet(
        List<WireSearchHit>? hits, int bodyLimit, params string[] excludedFields)
    {
        // Exclusion is by prefix: the service reports variants of a field ("fileNames",
        // "fileNames.pattern") that all carry the same text.
        var parts = (hits ?? [])
            .Where(h => h.FieldReferenceName is not { } field || !excludedFields.Any(f =>
                field.StartsWith(f, StringComparison.OrdinalIgnoreCase) &&
                (field.Length == f.Length || field[f.Length] == '.')))
            .SelectMany(h => h.Highlights ?? [])
            .Select(Text.FromHighlight)
            .OfType<string>()
            .Distinct()
            .ToList();
        return Text.Truncate(parts.Count > 0 ? string.Join(" … ", parts) : null, bodyLimit);
    }

    /// <summary>
    /// Search returns fields as a flat string dictionary with lower-cased reference names
    /// ("system.id"), matched case-insensitively in case that casing is not universal.
    /// </summary>
    internal static string? SearchField(Dictionary<string, string?>? fields, string name)
    {
        if (fields is null)
        {
            return null;
        }
        if (fields.TryGetValue(name, out var direct))
        {
            return direct;
        }
        return fields.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Search renders identities as "Display Name &lt;email&gt;". Keep the name.</summary>
    internal static string? PersonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var bracket = value.IndexOf('<');
        return bracket > 0 ? value[..bracket].TrimEnd() : value;
    }

    // ------------------------------------------------------------------ field helpers

    internal static string? Str(Dictionary<string, JsonElement>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? Int(Dictionary<string, JsonElement>? fields, string name) =>
        // The ValueKind check is not redundant: TryGetInt32 throws rather than returning false
        // when the element is not a number, which a custom field with a text value routinely is.
        fields is not null && fields.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    /// <summary>
    /// The scheduling fields are doubles: half an hour of remaining work and half a story point
    /// are ordinary values, and <see cref="Int"/> would round them away silently. Same ValueKind
    /// guard, for the same reason.
    /// </summary>
    internal static double? Number(Dictionary<string, JsonElement>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            ? d
            : null;

    internal static DateTimeOffset? Date(Dictionary<string, JsonElement>? fields, string name) =>
        Str(fields, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    /// <summary>Identity fields are objects. Only the display name is worth carrying.</summary>
    internal static string? Person(Dictionary<string, JsonElement>? fields, string name)
    {
        if (fields is null || !fields.TryGetValue(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("displayName", out var dn) => dn.GetString(),
            _ => null,
        };
    }

    private static string? AttributeString(Dictionary<string, JsonElement>? attributes, string name) =>
        attributes is not null && attributes.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static List<string>? Tags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }
        var split = tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return split.Count > 0 ? split : null;
    }

    /// <summary>refs/heads/main -> main. The prefix is always the same, so it is noise.</summary>
    internal static string? ShortBranch(string? refName) => refName switch
    {
        null => null,
        var r when r.StartsWith("refs/heads/", StringComparison.Ordinal) => r["refs/heads/".Length..],
        var r => r,
    };

    /// <summary>System.LinkTypes.Hierarchy-Reverse -> Hierarchy-Reverse.</summary>
    internal static string? ShortRelation(string? rel) => rel switch
    {
        null => null,
        var r when r.StartsWith("System.LinkTypes.", StringComparison.Ordinal) => r["System.LinkTypes.".Length..],
        var r when r.StartsWith("Microsoft.VSTS.Common.", StringComparison.Ordinal) =>
            r["Microsoft.VSTS.Common.".Length..],
        var r => r,
    };

    /// <summary>The trailing id of a .../_apis/wit/workItems/123 url, or null for other link targets.</summary>
    internal static int? LinkedWorkItemId(string? url)
    {
        if (url is null || url.IndexOf("/_apis/wit/workItems/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }
        var tail = url[(url.LastIndexOf('/') + 1)..];
        return int.TryParse(tail, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>An area/iteration path equal to the project itself carries no information.</summary>
    internal static string? TrimPath(string? path, string? project) =>
        string.IsNullOrWhiteSpace(path) || string.Equals(path, project, StringComparison.OrdinalIgnoreCase)
            ? null
            : path;

    internal static string? TrimDescription(string? description, string? name)
    {
        if (string.IsNullOrWhiteSpace(description) ||
            string.Equals(description, name, StringComparison.OrdinalIgnoreCase))
        {
            return null; // boilerplate: repeats the name or is empty
        }
        return description.Length <= 100 ? description : Text.Cut(description, 100) + "…";
    }

    // ------------------------------------------------------------------- browser links
    //
    // The REST `url` fields point at the API, which a person cannot open. These build the address
    // an agent can hand to a human.

    internal static string? PullRequestUrl(string orgUrl, WirePullRequest pr) =>
        pr.Repository?.Project?.Name is { } project && pr.Repository?.Name is { } repo
            ? $"{orgUrl}/{Escape(project)}/_git/{Escape(repo)}/pullrequest/{pr.PullRequestId}"
            : null;

    internal static string? WorkItemUrl(string orgUrl, string? project, int id) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_workitems/edit/{id}";

    internal static string? RunUrl(string orgUrl, string? project, int id) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_build/results?buildId={id}";

    /// <summary>
    /// The release progress view, the page showing stages and approvals. Not _releaseDefinition,
    /// which is the editor. Release links stay on the dev.azure.com host; only the API moved to
    /// vsrm.
    /// </summary>
    internal static string? ReleaseUrl(string orgUrl, string? project, int releaseId) =>
        project is null
            ? null
            : $"{orgUrl}/{Escape(project)}/_releaseProgress?_a=release-pipeline-progress&releaseId={releaseId}";

    internal static string? ReleaseDefinitionUrl(string orgUrl, string? project, int definitionId) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_release?definitionId={definitionId}";

    /// <summary>A $/-rooted path is TFVC and browses under _versionControl. Anything else is git.</summary>
    internal static string? CodeUrl(string orgUrl, string project, WireCodeResult r) =>
        r.Path is not { Length: > 0 } path
            ? null
            : path.StartsWith("$/", StringComparison.Ordinal)
                ? $"{orgUrl}/{Escape(project)}/_versionControl?path={Escape(path)}"
                : r.Repository?.Name is { } repo
                    ? $"{orgUrl}/{Escape(project)}/_git/{Escape(repo)}?path={Escape(path)}"
                    : null;

    internal static string? WikiUrl(string orgUrl, string project, WireWikiResult r) =>
        r.Wiki?.Name is { } wiki && r.Path is { Length: > 0 } path
            ? $"{orgUrl}/{Escape(project)}/_wiki/wikis/{Escape(wiki)}?pagePath={Escape(path)}"
            : null;

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
