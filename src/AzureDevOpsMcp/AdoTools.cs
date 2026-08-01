using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AzureDevOpsMcp;

// Output conventions, chosen to keep results small in a model's context window:
// - Null fields are omitted from serialized results (configured in Program.cs).
// - Fields that repeat the common case are set to null: a "wellFormed" project state, a
//   "succeeded" merge status, a "completed" run status, an area path equal to the project.
// - Deleted and system-generated pull request comments are skipped by default and surfaced as
//   counts in the `skipped` envelope field, so "no discussion" is distinguishable from "filtered".
// - Bodies are plain text (links kept as "text (url)"), truncated at body_limit with
//   `truncated: true`.
[McpServerToolType]
public sealed class AdoTools(AdoContext ado, ILogger<AdoTools> log)
{
    private const string Api = "api-version=7.1";

    /// <summary>Work item comments are still preview-only in 7.1. There is no GA version of them.</summary>
    private const string CommentsApi = "api-version=7.1-preview.3";

    /// <summary>The Search API is likewise preview-only. A bare 7.1 is rejected.</summary>
    private const string SearchApi = "api-version=7.1-preview.1";

    /// <summary>The identity service (vssps host) is likewise preview-only.</summary>
    private const string IdentityApi = "api-version=7.1-preview.1";

    /// <summary>
    /// Bodies echoed back from a write (the updated work item's description, the created comment)
    /// use the same default cap as get_work_item. The caller mostly wants ids and fields back,
    /// not a re-reading of prose it may itself have just written.
    /// </summary>
    private const int WriteEchoBodyLimit = 4000;

    /// <summary>Shorthand for the logging helper. Every tool logs its arguments through it.</summary>
    private static string A(string name, object? value) => AdoMcpLog.Arg(name, value);

    // ---------------------------------------------------------------- read tools

    [McpServerTool(Name = "list_projects", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the projects in the Azure DevOps organization. Returns id, name, description.")]
    public Task<List<ProjectDto>> ListProjects(
        [Description("Maximum projects to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_projects", A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var projects = await ListProjectsInternal(client, limit, ct);
        return projects.Select(Mapping.Project).ToList();
    });

    [McpServerTool(Name = "list_repos", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the Git repositories of a project. `project` may be a project id (GUID) or a " +
                 "name; it defaults to ADO_MCP_PROJECT when that is set for this server.")]
    public Task<List<RepoDto>> ListRepos(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum repositories to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_repos",
        A("project", project) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var resolved = await ResolveProjectAsync(client, project, ct);
        var repos = await ListReposInternal(client, resolved.Id, ct);
        return repos.Take(limit).Select(Mapping.Repo).ToList();
    });

    [McpServerTool(Name = "list_pull_requests", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List pull requests, newest first. Scoped to one repository when `repo` is given, " +
                 "otherwise to the whole project. `created_by` matches a display name case-insensitively. " +
                 "Returns {pullRequests, hasMore?}.")]
    public Task<PullRequestsResult> ListPullRequests(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Repository id (GUID) or name; omit to search every repository in the project")] string? repo = null,
        [Description("active (default), completed, abandoned, or all")] string status = "active",
        [Description("Only pull requests targeting this branch, e.g. main")] string? target_branch = null,
        [Description("Only pull requests whose author's display name contains this (case-insensitive)")] string? created_by = null,
        [Description("Maximum pull requests to return (default 25, max 200)")] int limit = 25,
        CancellationToken ct = default) => Run("list_pull_requests",
        A("project", project) + A("repo", repo) + A("status", status) + A("target_branch", target_branch) +
        A("created_by", created_by) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        const int scanCap = 500; // upper bound on pull requests examined when filtering by author
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var repoId = repo is null ? null : (await ResolveRepoAsync(client, resolvedProject.Id, repo, ct)).Id;

        var basePath = repoId is null
            ? $"{Escape(resolvedProject.Id)}/_apis/git/pullrequests"
            : $"{Escape(resolvedProject.Id)}/_apis/git/repositories/{Escape(repoId)}/pullrequests";

        var results = new List<PullRequestDto>();
        var hasMore = false;
        var scanned = 0;
        // Without an author filter every pull request the service returns is a result, so asking for
        // one more than the limit answers `hasMore` in a single request. With one, the filtering
        // happens here and full pages have to be scanned until the limit or the cap is reached.
        var pageSize = created_by is null ? Math.Min(limit + 1, 101) : 100;
        for (var skip = 0; skip < scanCap; skip += pageSize)
        {
            var path = basePath + "?" + Api +
                       $"&searchCriteria.status={Uri.EscapeDataString(status)}" +
                       (target_branch is null
                           ? ""
                           : $"&searchCriteria.targetRefName={Uri.EscapeDataString(FullBranch(target_branch))}") +
                       $"&$top={pageSize}&$skip={skip}";
            var page = await client.GetAsync<ListResponse<WirePullRequest>>(path, ct);
            var batch = page.Value ?? [];
            foreach (var pr in batch)
            {
                scanned++;
                if (created_by is not null &&
                    pr.CreatedBy?.DisplayName?.Contains(created_by, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }
                if (results.Count >= limit)
                {
                    hasMore = true;
                    break;
                }
                results.Add(Mapping.PullRequest(pr, client.OrgUrl, includeRepo: repoId is null));
            }
            if (hasMore || batch.Count < pageSize)
            {
                break;
            }
            log.Line(LogLevel.Debug, Ev.Page,
                "list_pull_requests next page" + A("scanned", scanned) + A("matched", results.Count));
            if (skip + pageSize >= scanCap)
            {
                // The page that got here was full, so the cap cut the scan short.
                hasMore = true;
                log.Line(LogLevel.Warning, Ev.Page,
                    "list_pull_requests hit the scan cap; results may be incomplete" +
                    A("scanned", scanned) + A("cap", scanCap) + A("matched", results.Count));
            }
        }
        return new PullRequestsResult(results, hasMore ? true : null);
    });

    [McpServerTool(Name = "get_pull_request", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one pull request with its description and review threads. Deleted and " +
                 "system-generated comments (pushes, votes, policy results) are filtered out and counted in " +
                 "`skipped`. Returns the pull request plus {threads, moreThreads?, skipped?}.")]
    public Task<PullRequestDetailDto> GetPullRequest(
        [Description("Pull request id")] int id,
        [Description("Include the review threads (default true)")] bool include_threads = true,
        [Description("Include system-generated comments such as pushes and votes (default false)")] bool include_system = false,
        [Description("Maximum threads to return (default 50)")] int max_threads = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("get_pull_request",
        A("id", id) + A("include_threads", include_threads) + A("include_system", include_system) +
        A("max_threads", max_threads) + A("body_limit", body_limit), async () =>
    {
        var client = await ado.GetClientAsync(ct);
        return await ReadPullRequestAsync(
            client, id, include_threads, include_system, max_threads, body_limit, ct);
    });

    /// <summary>
    /// Reads one pull request with its threads. Shared by <c>get_pull_request</c> and
    /// <c>wait_for_pull_request</c> so waiting for a pull request and asking about one report it
    /// identically.
    /// </summary>
    private async Task<PullRequestDetailDto> ReadPullRequestAsync(
        AdoClient client, int id, bool include_threads, bool include_system, int max_threads,
        int body_limit, CancellationToken ct)
    {
        max_threads = Math.Clamp(max_threads, 1, 500);
        // The organization-level endpoint finds a pull request from its id alone, so the caller does
        // not have to know which project or repository it lives in.
        var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);

        var counts = new SkipCounter();
        List<ThreadDto>? threads = null;
        var moreThreads = false;
        if (include_threads && pr.Repository?.Id is { } repoId && pr.Repository?.Project?.Id is { } projectId)
        {
            var response = await client.GetAsync<ListResponse<WireThread>>(
                $"{Escape(projectId)}/_apis/git/repositories/{Escape(repoId)}/pullRequests/{id}/threads?{Api}", ct);
            // Mapped eagerly so the skipped counts cover every thread, not just those under the cap.
            var mapped = (response.Value ?? [])
                .Select(t => Mapping.Thread(t, include_system, body_limit, counts))
                .OfType<ThreadDto>()
                .ToList();
            moreThreads = mapped.Count > max_threads;
            threads = moreThreads ? mapped[..max_threads] : mapped;
            if (threads.Count == 0)
            {
                threads = null;
            }
        }

        var (description, truncated) = Text.Truncate(Text.FromMarkdown(pr.Description), body_limit);
        return new PullRequestDetailDto(
            pr.PullRequestId,
            pr.Title,
            pr.Status,
            pr.Repository?.Name,
            pr.CreatedBy?.DisplayName,
            pr.CreationDate,
            pr.ClosedDate,
            Mapping.ShortBranch(pr.SourceRefName),
            Mapping.ShortBranch(pr.TargetRefName),
            pr.IsDraft is true ? true : null,
            string.Equals(pr.MergeStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? null : pr.MergeStatus,
            description,
            truncated,
            Mapping.Reviewers(pr.Reviewers),
            threads,
            moreThreads ? true : null,
            counts.ToDto(),
            Mapping.PullRequestUrl(client.OrgUrl, pr));
    }

    [McpServerTool(Name = "wait_for_pull_request", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for a pull request to reach a terminal state — completed " +
                 "(merged) or abandoned — then report it exactly as get_pull_request does. Polls " +
                 "until the pull request leaves `active` and returns as soon as it does. Running " +
                 "out of `timeout_seconds` is not an error: the pull request is returned as it " +
                 "stands with `timedOut: true`, so a still-open pull request is distinguishable " +
                 "from an abandoned one. Returns {pullRequest, waitedSeconds, timedOut?}.")]
    public Task<PullRequestWaitResult> WaitForPullRequest(
        [Description("Pull request id")] int id,
        [Description("Give up after this many seconds (default 1800, max 21600)")] int timeout_seconds = 1800,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Include the review threads once the wait ends (default true)")] bool include_threads = true,
        [Description("Include system-generated comments such as pushes and votes (default false)")] bool include_system = false,
        [Description("Maximum threads to return (default 50)")] int max_threads = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("wait_for_pull_request",
        A("id", id) + A("timeout_seconds", timeout_seconds) + A("poll_seconds", poll_seconds) +
        A("include_threads", include_threads) + A("include_system", include_system) +
        A("max_threads", max_threads) + A("body_limit", body_limit), async () =>
    {
        // Bounded like wait_for_pipeline_run: a caller cannot ask it to wait forever, and it
        // cannot poll hard enough to matter to the service.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 21600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(poll_seconds, 5, 600));
        var client = await ado.GetClientAsync(ct);

        var sw = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            // Only the pull request is fetched while waiting. The threads cost an extra request
            // and are of no interest until there is an ended pull request to report.
            var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);
            polls++;
            if (Mapping.IsTerminalPullRequestStatus(pr.Status))
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "pull request ended" + A("id", id) + A("status", pr.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PullRequestWaitResult(
                    await ReadPullRequestAsync(
                        client, id, include_threads, include_system, max_threads, body_limit, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: null);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("id", id) + A("status", pr.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PullRequestWaitResult(
                    await ReadPullRequestAsync(
                        client, id, include_threads, include_system, max_threads, body_limit, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: true);
            }

            log.Line(LogLevel.Debug, Ev.Poll,
                "still active" + A("id", id) + A("status", pr.Status) +
                A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
            await Task.Delay(interval < left ? interval : left, ct);
        }
    });

    [McpServerTool(Name = "list_work_items", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Query work items. Either pass a full `wiql` query, or leave it out and use the " +
                 "filter arguments — the WIQL the server builds from them is echoed back in the result so it " +
                 "can be refined and passed to `wiql` on the next call. Returns {workItems, hasMore?, wiql?}.")]
    public Task<WorkItemsResult> ListWorkItems(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Full WIQL query. When given, every other filter argument is ignored.")] string? wiql = null,
        [Description("Team id (GUID) or name; restricts results to the area paths that team owns")] string? team = null,
        [Description("Work item type(s), comma-separated, e.g. Bug or \"Bug,Task\"")] string? type = null,
        [Description("State(s), comma-separated, e.g. Active or \"Active,New\"")] string? state = null,
        [Description("Assignee display name or email; \"me\" for the signed-in user")] string? assigned_to = null,
        [Description("Only work items changed at/after this ISO-8601 timestamp")] string? changed_since = null,
        [Description("Only work items whose title contains this")] string? title_contains = null,
        [Description("Maximum work items to return (default 50, max 200)")] int limit = 50,
        CancellationToken ct = default) => Run("list_work_items",
        A("project", project) + A("wiql", wiql) + A("team", team) + A("type", type) + A("state", state) +
        A("assigned_to", assigned_to) + A("changed_since", changed_since) +
        A("title_contains", title_contains) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        string query;
        string? generated = null;
        if (wiql is { Length: > 0 })
        {
            query = wiql;
        }
        else
        {
            var areaPaths = team is null
                ? []
                : await TeamAreaPathsAsync(client, resolvedProject, team, ct);
            query = generated = BuildWiql(
                resolvedProject.Name, areaPaths, type, state, assigned_to,
                ParseTimestamp(changed_since, nameof(changed_since)), title_contains);
            log.Line(LogLevel.Debug, Ev.ToolStart, "generated wiql" + A("wiql", query));
        }

        // The WIQL endpoint returns ids only. The fields come from a separate batched read, and
        // asking for one id more than the limit answers `hasMore` without a second query.
        var refs = await client.PostAsync<WiqlResult>(
            $"{Escape(resolvedProject.Id)}/_apis/wit/wiql?{Api}&$top={limit + 1}",
            new { query },
            ct);
        var ids = (refs.WorkItems ?? []).Select(r => r.Id).ToList();
        var hasMore = ids.Count > limit;
        if (hasMore)
        {
            ids = ids[..limit];
        }
        if (ids.Count == 0)
        {
            return new WorkItemsResult([], null, generated);
        }

        var items = await GetWorkItemsAsync(client, ids, ct);
        // The batch read answers in id order. The query's own ordering is the one asked for.
        var position = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        var mapped = items
            .OrderBy(w => position.TryGetValue(w.Id, out var index) ? index : int.MaxValue)
            .Select(w => Mapping.WorkItem(w, client.OrgUrl))
            .ToList();
        return new WorkItemsResult(mapped, hasMore ? true : null, generated);
    });

    [McpServerTool(Name = "get_work_item", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one work item with its description, links to other work items and " +
                 "artifacts, and its discussion. Deleted comments are filtered out and counted in `skipped`.")]
    public Task<WorkItemDetailDto> GetWorkItem(
        [Description("Work item id")] int id,
        [Description("Include the discussion (default true)")] bool include_comments = true,
        [Description("Include links to other work items, commits and pull requests (default true)")] bool include_relations = true,
        [Description("Maximum comments to return (default 50)")] int max_comments = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 4000)")] int body_limit = 4000,
        CancellationToken ct = default) => Run("get_work_item",
        A("id", id) + A("include_comments", include_comments) + A("include_relations", include_relations) +
        A("max_comments", max_comments) + A("body_limit", body_limit), async () =>
    {
        max_comments = Math.Clamp(max_comments, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var expand = include_relations ? "all" : "none";
        var item = await client.GetAsync<WireWorkItem>(
            $"_apis/wit/workitems/{id}?$expand={expand}&{Api}", ct);

        var counts = new SkipCounter();
        List<CommentDto>? comments = null;
        // Comments hang off the project-scoped route, and the work item is the only thing that knows
        // which project it belongs to.
        if (include_comments && Mapping.Str(item.Fields, "System.TeamProject") is { } itemProject)
        {
            var response = await client.GetAsync<WireWorkItemComments>(
                $"{Escape(itemProject)}/_apis/wit/workItems/{id}/comments?{CommentsApi}&$top={max_comments}", ct);
            comments = (response.Comments ?? [])
                .Select(c => Mapping.WorkItemComment(c, body_limit, counts))
                .OfType<CommentDto>()
                .ToList();
        }

        return Mapping.WorkItemDetail(item, body_limit, client.OrgUrl, comments, counts.ToDto());
    });

    [McpServerTool(Name = "list_pipelines", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the pipelines of a project. The returned id is what list_pipeline_runs takes.")]
    public Task<List<PipelineDto>> ListPipelines(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum pipelines to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_pipelines",
        A("project", project) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var resolved = await ResolveProjectAsync(client, project, ct);
        var pipelines = await ListPipelinesInternal(client, resolved.Id, limit, ct);
        return pipelines.Select(Mapping.Pipeline).ToList();
    });

    [McpServerTool(Name = "list_pipeline_runs", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List runs of one pipeline, newest first. `pipeline` may be a numeric pipeline id " +
                 "or a name. Returns {runs, hasMore?}.")]
    public Task<PipelineRunsResult> ListPipelineRuns(
        [Description("Pipeline id (number) or name")] string pipeline,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Only runs with this result: succeeded, partiallySucceeded, failed, canceled")] string? result = null,
        [Description("Only runs queued at/after this ISO-8601 timestamp")] string? since = null,
        [Description("Only runs of this branch, e.g. main")] string? branch = null,
        [Description("Maximum runs to return (default 20, max 200)")] int limit = 20,
        CancellationToken ct = default) => Run("list_pipeline_runs",
        A("pipeline", pipeline) + A("project", project) + A("result", result) + A("since", since) +
        A("branch", branch) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedPipeline = await ResolvePipelineAsync(client, resolvedProject.Id, pipeline, ct);
        var sinceTs = ParseTimestamp(since, nameof(since));

        // One id over the limit, so `hasMore` is answered without a second round trip.
        var path = $"{Escape(resolvedProject.Id)}/_apis/build/builds?{Api}" +
                   $"&definitions={Uri.EscapeDataString(resolvedPipeline.Id)}" +
                   $"&queryOrder=queueTimeDescending&$top={limit + 1}" +
                   (result is null ? "" : $"&resultFilter={Uri.EscapeDataString(result)}") +
                   (branch is null ? "" : $"&branchName={Uri.EscapeDataString(FullBranch(branch))}") +
                   (sinceTs is null ? "" : $"&minTime={Uri.EscapeDataString(sinceTs.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        var page = await client.GetAsync<ListResponse<WireBuild>>(path, ct);

        var builds = page.Value ?? [];
        var hasMore = builds.Count > limit;
        var runs = builds
            .Take(limit)
            .Select(b => Mapping.Run(b, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new PipelineRunsResult(runs, hasMore ? true : null);
    });

    [McpServerTool(Name = "get_pipeline_run", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one pipeline run and summarize why it failed: every failed task with the " +
                 "stage and job it belongs to and the errors recorded against it. Set include_logs=true to " +
                 "also get the tail of each failed task's log, which is where the actual error text is. " +
                 "`skipped.succeeded` counts the timeline records that are not reported because they passed.")]
    public Task<PipelineRunDetailDto> GetPipelineRun(
        [Description("Run id (the same number as the build id)")] int run_id,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Fetch the tail of each failed task's log (default false; costs one request per task)")] bool include_logs = false,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        CancellationToken ct = default) => Run("get_pipeline_run",
        A("run_id", run_id) + A("project", project) + A("include_logs", include_logs) +
        A("log_tail_lines", log_tail_lines) + A("max_failed", max_failed) + A("max_errors", max_errors),
        async () =>
    {
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        return await ReadRunAsync(
            client, resolvedProject, run_id, include_logs, log_tail_lines, max_failed, max_errors, ct);
    });

    /// <summary>
    /// Reads one run and summarizes why it failed. Shared by <c>get_pipeline_run</c> and
    /// <c>wait_for_pipeline_run</c> so waiting for a run and asking about one report it
    /// identically.
    /// </summary>
    private async Task<PipelineRunDetailDto> ReadRunAsync(
        AdoClient client, Named resolvedProject, int run_id, bool include_logs,
        int log_tail_lines, int max_failed, int max_errors, CancellationToken ct)
    {
        max_failed = Math.Clamp(max_failed, 1, 50);
        max_errors = Math.Clamp(max_errors, 1, 50);
        log_tail_lines = Math.Clamp(log_tail_lines, 0, 500);

        var build = await client.GetAsync<WireBuild>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}?{Api}", ct);
        var timeline = await client.GetAsync<WireTimeline>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}/timeline?{Api}", ct);

        var counts = new SkipCounter();
        var failed = Mapping.FailedSteps(timeline, max_errors, counts);
        var reported = failed.Take(max_failed).Select(f => f.Step).ToList();

        if (include_logs && log_tail_lines > 0)
        {
            // Each step carries its own record's log url, so fetching is a straight walk with no
            // re-matching by name.
            for (var i = 0; i < reported.Count; i++)
            {
                if (failed[i].LogUrl is not { } url)
                {
                    continue;
                }
                var (tail, truncated) = Mapping.LogTail(await client.GetTextAsync(url, ct), log_tail_lines);
                reported[i] = reported[i] with { LogTail = tail, Truncated = truncated };
            }
        }

        return new PipelineRunDetailDto(
            build.Id,
            build.BuildNumber,
            build.Definition?.Name,
            string.Equals(build.Status, "completed", StringComparison.OrdinalIgnoreCase) ? null : build.Status,
            build.Result,
            build.QueueTime,
            build.FinishTime,
            Mapping.ShortBranch(build.SourceBranch),
            build.RequestedFor?.DisplayName,
            reported.Count > 0 ? reported : null,
            counts.ToDto(),
            Mapping.RunUrl(client.OrgUrl, build.Project?.Name ?? resolvedProject.Name, build.Id));
    }

    [McpServerTool(Name = "wait_for_pipeline_run", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for a pipeline run to finish, then report it exactly as " +
                 "get_pipeline_run does. Polls until the run reaches a terminal state and returns " +
                 "as soon as it does. Running out of `timeout_seconds` is not an error: the run is " +
                 "returned as it stands with `timedOut: true`, so an unfinished run is " +
                 "distinguishable from a failed one. Returns {run, waitedSeconds, timedOut?}.")]
    public Task<PipelineRunWaitResult> WaitForPipelineRun(
        [Description("Run id (the same number as the build id)")] int run_id,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Give up after this many seconds (default 1800, max 21600)")] int timeout_seconds = 1800,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Fetch the tail of each failed task's log once the run finishes (default true)")] bool include_logs = true,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        CancellationToken ct = default) => Run("wait_for_pipeline_run",
        A("run_id", run_id) + A("project", project) + A("timeout_seconds", timeout_seconds) +
        A("poll_seconds", poll_seconds) + A("include_logs", include_logs) +
        A("log_tail_lines", log_tail_lines) + A("max_failed", max_failed) + A("max_errors", max_errors),
        async () =>
    {
        // Bounded like every other loop in this server: a caller cannot ask it to wait forever, and
        // it cannot poll hard enough to matter to the service.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 21600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(poll_seconds, 5, 600));
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        var sw = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            // Only the build is fetched while waiting. The timeline and any logs cost an extra
            // request each and are of no interest until there is a finished run to explain.
            var build = await client.GetAsync<WireBuild>(
                $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}?{Api}", ct);
            polls++;
            if (Mapping.IsTerminalRunStatus(build.Status))
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "run finished" + A("run_id", run_id) + A("status", build.Status) +
                    A("result", build.Result) + A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PipelineRunWaitResult(
                    await ReadRunAsync(client, resolvedProject, run_id, include_logs,
                        log_tail_lines, max_failed, max_errors, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: null);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("run_id", run_id) + A("status", build.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PipelineRunWaitResult(
                    await ReadRunAsync(client, resolvedProject, run_id, include_logs,
                        log_tail_lines, max_failed, max_errors, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: true);
            }

            log.Line(LogLevel.Debug, Ev.Poll,
                "still running" + A("run_id", run_id) + A("status", build.Status) +
                A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
            await Task.Delay(interval < left ? interval : left, ct);
        }
    });

    // ---------------------------------------------------------------- search tools
    //
    // The Search service answers on its own host (Search.BaseUrl) with POST bodies. All three
    // tools scope to one project through the route, ask for `limit` results in a single request,
    // and answer hasMore from the service's total match count rather than fetching more.

    [McpServerTool(Name = "search_code", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text code search over the project's repositories (git and TFVC) " +
                 "via the Azure DevOps Search service; needs the Code Search extension installed in " +
                 "the organization. The query supports that service's syntax: AND/OR/NOT, wildcards, " +
                 "and inline filters such as ext:cs, class:Foo, file:web.config. Returns {results, " +
                 "total, hasMore?} — one result per file with its match count, and matched snippets " +
                 "when the service returns them; total is the service's overall match count, so 0 " +
                 "means nothing matched the query and filters.")]
    public Task<CodeSearchResult> SearchCode(
        [Description("What to search for (code search syntax allowed)")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Repository id (GUID) or name; omit to search every repository in the project")] string? repo = null,
        [Description("Only files under this path, e.g. $/Project/Websites, or /src with `repo`. " +
                     "The service scopes a path to one repository: a $/ path implies it, any other needs `repo`")] string? path = null,
        [Description("Only this branch (it must be indexed; the default branch always is)")] string? branch = null,
        [Description("Maximum files to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per file; longer get truncated:true (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("search_code",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("repo", repo) +
        A("path", path) + A("branch", branch) + A("limit", limit) + A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        // The Repository filter matches by name, so an id argument is resolved back to one, and a
        // wrong name fails here with the candidates listed instead of silently matching nothing.
        // TFVC content lives in a repository named "$/Project", which the git repository list
        // does not know, so a $/ value passes through as-is.
        string? repoName = null;
        if (repo is not null)
        {
            if (repo.StartsWith("$/", StringComparison.Ordinal))
            {
                repoName = repo;
            }
            else
            {
                var repos = await ListReposInternal(client, resolvedProject.Id, ct);
                var named = Resolve(
                    repo, IsGuid,
                    repos.Where(r => r.Id is not null && r.Name is not null)
                        .Select(r => new Named(r.Id!, r.Name!)).ToList(),
                    "repository", log);
                repoName = repos.FirstOrDefault(r =>
                        string.Equals(r.Id, named.Id, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? named.Name;
            }
        }
        if (path is not null && repoName is null)
        {
            // The service refuses a Path filter without a Repository filter. A TFVC server path
            // names its own repository. Anything else needs `repo` from the caller.
            repoName = Search.TfvcRepository(path) ?? throw new McpException(
                "The Search service only filters `path` within one repository — pass `repo` " +
                "together with `path`. (A TFVC path like $/Project/... implies its repository.)");
        }

        // Project is scoped by the route, but the service refuses a Repository filter unless a
        // Project filter accompanies it, so it is always sent.
        var request = Search.BuildRequest(query, limit,
            ("Project", resolvedProject.Name), ("Repository", repoName), ("Path", path),
            ("Branch", branch)) with { IncludeSnippet = true };
        var response = await client.PostAsync<WireCodeSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/codesearchresults?{SearchApi}",
            request, ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.CodeHit(r, body_limit, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new CodeSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "search_work_items", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text search over work items — titles, descriptions, comments and " +
                 "fields — via the Azure DevOps Search service; use list_work_items for structured " +
                 "(WIQL) queries. The query supports AND/OR/NOT, wildcards, and inline field filters. " +
                 "Returns {results, total, hasMore?} with the matched text as a snippet per work item.")]
    public Task<WorkItemSearchResult> SearchWorkItems(
        [Description("What to search for (work item search syntax allowed)")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum work items to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per work item; longer get truncated:true (0 = unlimited, default 500)")] int body_limit = 500,
        CancellationToken ct = default) => Run("search_work_items",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("limit", limit) +
        A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var response = await client.PostAsync<WireWorkItemSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/workitemsearchresults?{SearchApi}",
            Search.BuildRequest(query, limit), ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.WorkItemSearchHit(r, body_limit, client.OrgUrl))
            .ToList();
        return new WorkItemSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "search_wiki", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text search over the project's wikis via the Azure DevOps Search " +
                 "service. Returns {results, total, hasMore?} with the matched text as a snippet per " +
                 "page.")]
    public Task<WikiSearchResult> SearchWiki(
        [Description("What to search for")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum pages to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per page; longer get truncated:true (0 = unlimited, default 500)")] int body_limit = 500,
        CancellationToken ct = default) => Run("search_wiki",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("limit", limit) +
        A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var response = await client.PostAsync<WireWikiSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/wikisearchresults?{SearchApi}",
            Search.BuildRequest(query, limit), ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.WikiHit(r, body_limit, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new WikiSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "deployment_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Report production state per deployable defined in an external " +
                 "deployment map (ADO_MCP_DEPLOYMENTS). A deployable names either a classic " +
                 "release definition + environment, or a build/YAML pipeline (optionally through " +
                 "an ADO Environment's deployment records, optionally pinned to a branch). Each " +
                 "reports its latest succeeded deployment, the build it shipped, the version that " +
                 "build was made from — TFVC changeset or git commit, whichever the build's " +
                 "repository implies — and the work that has landed since: changesets under the " +
                 "deployable's paths, or commits on its branch. Pass `changeset` to ask whether " +
                 "that TFVC changeset is included in each TFVC-built deployable's deployed " +
                 "version (containsChangeset) and whether it touched that deployable's paths at " +
                 "all (affects). Paths default to the build definition's own TFVC workspace " +
                 "mappings. The server ships no deployment knowledge; without the map file this " +
                 "tool only explains how to create one.")]
    public Task<DeploymentStatusResult> DeploymentStatus(
        [Description("Deployable name from the map; omit to report all of them")] string? deployable = null,
        [Description("TFVC changeset id to check for being deployed (TFVC-built deployables only)")] int? changeset = null,
        [Description("List the undeployed changesets/commits rather than only counting them (default false)")] bool include_changesets = false,
        [Description("Maximum undeployed changesets/commits per deployable (default 25, max 100)")] int max_changesets = 25,
        CancellationToken ct = default) => Run("deployment_status",
        A("deployable", deployable) + A("changeset", changeset) +
        A("include_changesets", include_changesets) + A("max_changesets", max_changesets), async () =>
    {
        max_changesets = Math.Clamp(max_changesets, 1, 100);
        var map = Deployments.Get(log);
        if (deployable is not null)
        {
            var chosen = Resolve(
                deployable, _ => false, map.Select(d => new Named(d.Name, d.Name)).ToList(), "deployable", log);
            map = map.Where(d => string.Equals(d.Name, chosen.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);

        // When the caller asks about one changeset, its touched paths answer `affects` for every
        // deployable, so they are fetched once, outside the loop.
        List<string>? changesetPaths = null;
        if (changeset is { } askedId)
        {
            var changes = await client.GetAsync<ListResponse<WireTfvcChange>>(
                $"_apis/tfvc/changesets/{askedId}/changes?{Api}&$top=500", ct);
            changesetPaths = (changes.Value ?? [])
                .Select(c => c.Item?.Path)
                .OfType<string>()
                .ToList();
        }

        var definitionsByProject = new Dictionary<string, List<WireReleaseDefinition>>();
        async Task<List<WireReleaseDefinition>> DefinitionsAsync(Named project)
        {
            if (!definitionsByProject.TryGetValue(project.Id, out var defs))
            {
                const int definitionCap = 500;
                var response = await client.GetAsync<ListResponse<WireReleaseDefinition>>(
                    $"{vsrm}/{Escape(project.Id)}/_apis/release/definitions?{Api}&$expand=environments&$top={definitionCap}", ct);
                definitionsByProject[project.Id] = defs = response.Value ?? [];
                if (defs.Count >= definitionCap)
                {
                    log.Line(LogLevel.Warning, Ev.Page,
                        "deployment_status hit the release definition cap; resolution may be incomplete" +
                        A("project", project.Name) + A("cap", definitionCap));
                }
            }
            return defs;
        }

        var buildDefsByProject = new Dictionary<string, List<WireBuildDefinition>>();
        async Task<List<WireBuildDefinition>> BuildDefinitionsAsync(Named project)
        {
            if (!buildDefsByProject.TryGetValue(project.Id, out var defs))
            {
                const int definitionCap = 1000;
                var response = await client.GetAsync<ListResponse<WireBuildDefinition>>(
                    $"{Escape(project.Id)}/_apis/build/definitions?{Api}&$top={definitionCap}", ct);
                buildDefsByProject[project.Id] = defs = response.Value ?? [];
                if (defs.Count >= definitionCap)
                {
                    log.Line(LogLevel.Warning, Ev.Page,
                        "deployment_status hit the build definition cap; resolution may be incomplete" +
                        A("project", project.Name) + A("cap", definitionCap));
                }
            }
            return defs;
        }

        var results = new List<DeployableStatusDto>();
        foreach (var d in map)
        {
            try
            {
                results.Add(await StatusAsync(
                    client, vsrm, d, DefinitionsAsync, BuildDefinitionsAsync, changeset, changesetPaths,
                    include_changesets, max_changesets, ct));
            }
            catch (Exception e) when (e is McpException or AdoApiException)
            {
                // A fleet answer with one broken entry is still an answer. The entry says why.
                log.Line(LogLevel.Warning, Ev.ToolFail,
                    "deployment_status entry failed" + A("deployable", d.Name) + A("reason", e.Message));
                results.Add(new DeployableStatusDto(
                    d.Name, d.Note, d.ReleaseDefinition, d.Environment, d.Pipeline,
                    null, null, null, null, null, null, null, null, null, null, null, null, null,
                    null, e.Message, null));
            }
        }
        return new DeploymentStatusResult(results);
    });

    /// <summary>Dispatches on the deployable's form. Both forms converge on VersionStateAsync.</summary>
    private async Task<DeployableStatusDto> StatusAsync(
        AdoClient client,
        string vsrm,
        Deployable d,
        Func<Named, Task<List<WireReleaseDefinition>>> releaseDefinitions,
        Func<Named, Task<List<WireBuildDefinition>>> buildDefinitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var project = await ResolveProjectAsync(client, d.Project, ct);
        return d.Pipeline is null
            ? await ClassicStatusAsync(
                client, vsrm, project, d, releaseDefinitions,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct)
            : await PipelineStatusAsync(
                client, project, d, buildDefinitions,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
    }

    /// <summary>The classic chain: release definition → environment → deployment → release → build.</summary>
    private async Task<DeployableStatusDto> ClassicStatusAsync(
        AdoClient client,
        string vsrm,
        Named project,
        Deployable d,
        Func<Named, Task<List<WireReleaseDefinition>>> definitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var defs = await definitions(project);
        var def = Resolve(
            d.ReleaseDefinition!, IsNumber,
            defs.Where(x => x.Name is not null)
                .Select(x => new Named(x.Id.ToString(CultureInfo.InvariantCulture), x.Name!)).ToList(),
            "release definition", log);
        // Resolve passes a numeric id straight through, so it can name a definition that is not
        // in the list. Say so instead of failing with "sequence contains no matching element".
        var definition = defs.FirstOrDefault(x => x.Id.ToString(CultureInfo.InvariantCulture) == def.Id)
            ?? throw new McpException(
                $"No release definition with id {def.Id} in project '{project.Name}'. " +
                $"Available: {string.Join(", ", defs.Select(x => x.Name ?? x.Id.ToString(CultureInfo.InvariantCulture)))}");
        var environments = definition.Environments ?? [];
        var env = Resolve(
            d.Environment!, IsNumber,
            environments.OrderBy(e => e.Rank ?? 0)
                .Where(e => e.Name is not null)
                .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name!)).ToList(),
            "environment", log);

        var deployments = await client.GetAsync<ListResponse<WireDeployment>>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/deployments?{Api}" +
            $"&definitionId={def.Id}&definitionEnvironmentId={env.Id}&deploymentStatus=succeeded&$top=1", ct);
        if (deployments.Value is not [{ Release: { } releaseRef } deployment, ..])
        {
            return new DeployableStatusDto(
                d.Name, d.Note, def.Name, env.Name, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null,
                $"no succeeded deployment of '{def.Name}' in '{env.Name}'", null);
        }

        var release = await client.GetAsync<WireRelease>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/releases/{releaseRef.Id}?{Api}", ct);
        var artifact = (release.Artifacts ?? [])
            .Where(a => string.Equals(a.Type, "Build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.IsPrimary is true)
            .FirstOrDefault()
            ?? throw new McpException($"release '{release.Name}' of '{def.Name}' has no Build artifact");

        WireBuild? build = null;
        if (int.TryParse(artifact.DefinitionReference?.Version?.Id, CultureInfo.InvariantCulture, out var buildId))
        {
            build = await client.GetAsync<WireBuild>(
                $"{Escape(project.Id)}/_apis/build/builds/{buildId}?{Api}", ct);
        }

        var v = await VersionStateAsync(
            client, project, d, build, artifact.DefinitionReference?.Definition?.Id,
            askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
        return new DeployableStatusDto(
            d.Name, d.Note, def.Name, env.Name, null,
            release.Name, deployment.CompletedOn, build?.BuildNumber,
            v.Changeset, v.Commit, v.Branch, v.Repository, v.Paths,
            v.UndeployedCount, v.Undeployed, v.UndeployedCommits,
            v.HasMore, v.Contains, v.Affects, null,
            $"{client.OrgUrl}/{Escape(project.Name)}/_releaseProgress?_a=release-pipeline-progress&releaseId={releaseRef.Id}");
    }

    /// <summary>
    /// The pipeline chain: pipeline → latest succeeded run (through the ADO Environment's
    /// deployment records when one is configured, otherwise straight off the build API) → the
    /// version that run was built from.
    /// </summary>
    private async Task<DeployableStatusDto> PipelineStatusAsync(
        AdoClient client,
        Named project,
        Deployable d,
        Func<Named, Task<List<WireBuildDefinition>>> buildDefinitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var defs = await buildDefinitions(project);
        var pipe = Resolve(
            d.Pipeline!, IsNumber,
            defs.Where(x => x.Name is not null)
                .Select(x => new Named(x.Id.ToString(CultureInfo.InvariantCulture), x.Name!)).ToList(),
            "pipeline", log);
        var pipeId = int.Parse(pipe.Id, CultureInfo.InvariantCulture);

        string? environmentName = null;
        WireBuild build;
        DateTimeOffset? deployedOn;
        if (d.Environment is { } configuredEnv)
        {
            var environments = await client.GetAsync<ListResponse<WireEnvironmentInstance>>(
                $"{Escape(project.Id)}/_apis/distributedtask/environments?{Api}&$top=500", ct);
            var env = Resolve(
                configuredEnv, IsNumber,
                (environments.Value ?? []).Where(e => e.Name is not null)
                    .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name!)).ToList(),
                "environment", log);
            environmentName = env.Name;

            // Records arrive newest first, so the first succeeded record of this pipeline is the
            // deployment that is out.
            const int recordCap = 100;
            var records = await client.GetAsync<ListResponse<WireEnvDeploymentRecord>>(
                $"{Escape(project.Id)}/_apis/distributedtask/environments/{env.Id}" +
                $"/environmentdeploymentrecords?{Api}&top={recordCap}", ct);
            var record = (records.Value ?? []).FirstOrDefault(r =>
                string.Equals(r.Result, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                r.Definition?.Id == pipeId);
            if (record?.Owner is not { } run)
            {
                return new DeployableStatusDto(
                    d.Name, d.Note, null, env.Name, pipe.Name, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    $"no succeeded deployment of '{pipe.Name}' into environment '{env.Name}' " +
                    $"in the last {recordCap} records", null);
            }
            deployedOn = record.FinishTime;
            build = await client.GetAsync<WireBuild>(
                $"{Escape(project.Id)}/_apis/build/builds/{run.Id}?{Api}", ct);
        }
        else
        {
            var builds = await client.GetAsync<ListResponse<WireBuild>>(
                $"{Escape(project.Id)}/_apis/build/builds?{Api}&definitions={pipeId}" +
                "&statusFilter=completed&resultFilter=succeeded&$top=1" +
                (d.Branch is null ? "" : $"&branchName={Uri.EscapeDataString(FullBranch(d.Branch))}"), ct);
            if (builds.Value is not [{ } latest, ..])
            {
                return new DeployableStatusDto(
                    d.Name, d.Note, null, null, pipe.Name, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    $"no succeeded run of '{pipe.Name}'" + (d.Branch is null ? "" : $" on '{d.Branch}'"), null);
            }
            build = latest;
            deployedOn = build.FinishTime;
        }

        var v = await VersionStateAsync(
            client, project, d, build, build.Definition?.Id.ToString(CultureInfo.InvariantCulture),
            askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
        return new DeployableStatusDto(
            d.Name, d.Note, null, environmentName, pipe.Name,
            null, deployedOn, build.BuildNumber,
            v.Changeset, v.Commit, v.Branch, v.Repository, v.Paths,
            v.UndeployedCount, v.Undeployed, v.UndeployedCommits,
            v.HasMore, v.Contains, v.Affects, null,
            $"{client.OrgUrl}/{Escape(project.Name)}/_build/results?buildId={build.Id}");
    }

    /// <summary>
    /// What the deployed build pins down and what has landed since. A numeric sourceVersion is a
    /// TFVC changeset, and undeployed work is the changesets under the deployable's paths past
    /// it. Anything else is a git commit, and undeployed work is the commits on the branch ahead
    /// of it.
    /// </summary>
    private sealed record VersionState(
        int? Changeset, string? Commit, string? Branch, string? Repository,
        List<string>? Paths, int? UndeployedCount, List<ChangesetDto>? Undeployed,
        List<CommitDto>? UndeployedCommits, bool? HasMore, bool? Contains, bool? Affects);

    private async Task<VersionState> VersionStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        WireBuild? build,
        string? buildDefinitionId,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        if (build?.SourceVersion is not { Length: > 0 } source)
        {
            return new VersionState(null, null, null, null, null, null, null, null, null, null, null);
        }
        return int.TryParse(source, CultureInfo.InvariantCulture, out var changeset)
            ? await TfvcStateAsync(
                client, project, d, changeset, buildDefinitionId,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct)
            : await GitStateAsync(client, project, d, build, source, includeChangesets, maxChangesets, ct);
    }

    private async Task<VersionState> TfvcStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        int deployed,
        string? buildDefinitionId,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var paths = d.Paths;
        if (paths is null &&
            int.TryParse(buildDefinitionId, CultureInfo.InvariantCulture, out var buildDefId))
        {
            var detail = await client.GetAsync<WireBuildDefinitionDetail>(
                $"{Escape(project.Id)}/_apis/build/definitions/{buildDefId}?{Api}", ct);
            var mapping = detail.Repository?.Properties?.GetValueOrDefault("tfvcMapping");
            var derived = Deployments.ParseTfvcMappings(mapping);
            paths = derived.Count > 0 ? derived : null;
        }

        // Undeployed = changesets under the deployable's paths newer than the deployed one.
        int? undeployedCount = null;
        List<ChangesetDto>? undeployed = null;
        var hasMore = false;
        if (paths is { Count: > 0 })
        {
            // One changeset query per path. A deployable with an absurd mapping count stays
            // bounded, and hitting the cap is reported (hasMore plus a Warning) instead of
            // silently understating.
            const int pathSearchCap = 10;
            var searched = paths;
            if (paths.Count > pathSearchCap)
            {
                hasMore = true;
                log.Line(LogLevel.Warning, Ev.Page,
                    "deployment_status hit the path search cap; undeployed changesets may be missed" +
                    A("deployable", d.Name) + A("paths", paths.Count) + A("cap", pathSearchCap));
                searched = paths.Take(pathSearchCap).ToList();
            }
            var byId = new Dictionary<int, WireTfvcChangesetRef>();
            foreach (var path in searched)
            {
                var page = await client.GetAsync<ListResponse<WireTfvcChangesetRef>>(
                    $"{Escape(project.Id)}/_apis/tfvc/changesets?{Api}" +
                    $"&searchCriteria.itemPath={Uri.EscapeDataString(path)}" +
                    $"&searchCriteria.fromId={deployed + 1}&$top={maxChangesets + 1}", ct);
                var batch = page.Value ?? [];
                hasMore |= batch.Count > maxChangesets;
                foreach (var c in batch)
                {
                    byId[c.ChangesetId] = c;
                }
            }
            var ordered = byId.Values.OrderByDescending(c => c.ChangesetId).ToList();
            if (ordered.Count > maxChangesets)
            {
                hasMore = true;
                ordered = ordered[..maxChangesets];
            }
            undeployedCount = ordered.Count;
            if (includeChangesets && ordered.Count > 0)
            {
                undeployed = ordered.Select(Mapping.Changeset).ToList();
            }
        }

        bool? contains = null;
        bool? affects = null;
        if (askedChangeset is { } asked)
        {
            contains = asked <= deployed;
            affects = askedChangesetPaths is not null && paths is { Count: > 0 }
                ? askedChangesetPaths.Any(p => Deployments.UnderAny(p, paths))
                : null;
        }

        return new VersionState(
            deployed, null, null, null,
            paths is { Count: > 0 } ? paths : null,
            undeployedCount, undeployed, null,
            hasMore ? true : null, contains, affects);
    }

    private async Task<VersionState> GitStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        WireBuild build,
        string deployedSha,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var repo = build.Repository;
        var branch = Deployments.ShortBranch(d.Branch ?? build.SourceBranch ?? "");
        if (repo?.Id is not { Length: > 0 } repoId || branch.Length == 0)
        {
            // Enough to say what is deployed. Without a repository and branch the "since"
            // question has no frame to be answered in.
            return new VersionState(
                null, deployedSha, null, repo?.Name, null, null, null, null, null, null, null);
        }

        // Undeployed = commits on the branch ahead of the deployed one. The branch is read newest
        // first and walked until the deployed commit appears. A walk that runs out before finding
        // it (deep drift, or a version no longer on the branch) reports hasMore rather than a
        // number it cannot know.
        var page = await client.GetAsync<ListResponse<WireGitCommitRef>>(
            $"{Escape(project.Id)}/_apis/git/repositories/{repoId}/commits?{Api}" +
            $"&searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}" +
            "&searchCriteria.itemVersion.versionType=branch" +
            $"&searchCriteria.$top={maxChangesets + 1}", ct);
        var commits = page.Value ?? [];
        var ahead = new List<WireGitCommitRef>();
        var found = false;
        foreach (var c in commits)
        {
            if (string.Equals(c.CommitId, deployedSha, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
            ahead.Add(c);
        }
        // Not found means the count is a page of branch history, not "commits since deploy":
        // either the page was full and the walk ran out, or the deployed commit is no longer on
        // the branch at all. Either way an exact count is not known.
        var hasMore = !found;
        if (ahead.Count > maxChangesets)
        {
            ahead = ahead[..maxChangesets];
        }

        return new VersionState(
            null, deployedSha, branch, repo.Name, null,
            ahead.Count, null,
            includeChangesets && ahead.Count > 0 ? ahead.Select(Mapping.Commit).ToList() : null,
            hasMore ? true : null, null, null);
    }

    // ------------------------------------------------- write tools (ADO_MCP_ALLOW_WRITE)
    //
    // Every tool below mutates something other people can see, so every one calls
    // RequireWriteEnabled() before doing anything else, even validating its own arguments. The
    // refusal is the same regardless of what was passed. Each returns the post-write state in the
    // same null-omitting DTOs as the read tools, so the caller can confirm without a second call.

    // Destructive: this one overwrites fields that already had values, unlike the two below,
    // which only add. Not idempotent either, since a repeated call with the same `comment` posts
    // it again.
    [McpServerTool(Name = "update_work_item", UseStructuredContent = true, Destructive = true, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Update one " +
                 "work item: title, description/repro steps/acceptance criteria, state, assignee, " +
                 "area/iteration path, tags, priority, parent link, and/or add a comment to the " +
                 "discussion. Only the arguments given change; everything else is left alone. The " +
                 "body fields replace what is there — read the item first if you mean to extend it — " +
                 "while `comment` appends to the discussion and `add_tags`/`remove_tags` merge with " +
                 "the tags already on the item. A work item has at most one parent, so `parent` " +
                 "replaces whatever it is under and `remove_parent` leaves it unparented. Returns " +
                 "the updated work item.")]
    public Task<WorkItemDetailDto> UpdateWorkItem(
        [Description("Work item id")] int id,
        [Description("New state, e.g. Active, Resolved, Closed")] string? state = null,
        [Description("Assignee: display name, email, or identity GUID")] string? assigned_to = null,
        [Description("New area path")] string? area = null,
        [Description("New iteration path")] string? iteration = null,
        [Description("Tag(s) to add, comma-separated")] string? add_tags = null,
        [Description("Tag(s) to remove, comma-separated")] string? remove_tags = null,
        [Description("Priority, as this project's process defines it (commonly 1-4, 1 highest)")] int? priority = null,
        [Description("Work item id to parent this item under, replacing any existing parent")] int? parent = null,
        [Description("Remove the parent link, leaving the item unparented")] bool remove_parent = false,
        [Description("New title, replacing the current one")] string? title = null,
        [Description("New description (plain text or HTML), replacing the current one")] string? description = null,
        [Description("New repro steps (what a Bug's form shows instead of a description), " +
                     "replacing the current ones")] string? repro_steps = null,
        [Description("New acceptance criteria, replacing the current ones")] string? acceptance_criteria = null,
        [Description("Comment to add to the discussion")] string? comment = null,
        CancellationToken ct = default) => Run("update_work_item",
        A("id", id) + A("state", state) + A("assigned_to", assigned_to) + A("area", area) +
        A("iteration", iteration) + A("add_tags", add_tags) + A("remove_tags", remove_tags) +
        A("priority", priority) + A("parent", parent) + A("remove_parent", remove_parent ? true : null) +
        AdoMcpLog.ContentArg("title", title) + AdoMcpLog.ContentArg("description", description) +
        AdoMcpLog.ContentArg("repro_steps", repro_steps) +
        AdoMcpLog.ContentArg("acceptance_criteria", acceptance_criteria) +
        AdoMcpLog.ContentArg("comment", comment), async () =>
    {
        RequireWriteEnabled();
        if (state is null && assigned_to is null && area is null && iteration is null &&
            add_tags is null && remove_tags is null && priority is null && parent is null &&
            !remove_parent && title is null && description is null && repro_steps is null &&
            acceptance_criteria is null && comment is null)
        {
            throw new McpException(
                "Nothing to change: pass at least one of state, assigned_to, area, iteration, " +
                "add_tags, remove_tags, priority, parent, remove_parent, title, description, " +
                "repro_steps, acceptance_criteria, or comment.");
        }
        if (parent is not null && remove_parent)
        {
            throw new McpException(
                "`parent` and `remove_parent` ask for opposite things — pass one of them.");
        }
        if (parent == id)
        {
            throw new McpException($"Work item {id} cannot be its own parent.");
        }
        var client = await ado.GetClientAsync(ct);
        var assignee = assigned_to is null ? null : await ResolveIdentityAsync(client, assigned_to, ct);

        // Tags are one semicolon-joined field and the parent is a relation addressed by its index,
        // so both are read-merge-write. One read serves both, but `fields` and `$expand` cannot be
        // combined — asking for the relations means taking every field along with them.
        var reparenting = parent is not null || remove_parent;
        var merging = add_tags is not null || remove_tags is not null;
        string? tags = null;
        List<Writes.PatchOp> relationOps = [];
        WireWorkItem? current = null;
        if (reparenting || merging)
        {
            current = await client.GetAsync<WireWorkItem>(reparenting
                ? $"_apis/wit/workitems/{id}?{Api}&$expand=relations"
                : $"_apis/wit/workitems/{id}?{Api}&fields=System.Tags", ct);
            if (merging)
            {
                tags = Writes.MergeTags(Mapping.Str(current.Fields, "System.Tags"), add_tags, remove_tags);
            }
            relationOps = parent is { } parentId
                ? Writes.SetParent(current.Relations, parentId, client.OrgUrl)
                : remove_parent ? Writes.RemoveParent(current.Relations) : [];
        }

        var ops = Writes.UpdatePatch(
            state, assignee, area, iteration, tags, priority, title, description, repro_steps,
            acceptance_criteria, comment);
        ops.AddRange(relationOps);
        if (ops.Count == 0)
        {
            // The only thing asked for was a parent link that is already the way it was asked for.
            // Already-true is not a failure, and the read that established it is a whole work item.
            return Mapping.WorkItemDetail(current!, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
        }

        var updated = await client.PatchAsync<WireWorkItem>(
            HttpMethod.Patch,
            // Relations are only in the response when asked for, and a parent change is not
            // confirmable without them.
            $"_apis/wit/workitems/{id}?{Api}" + (reparenting ? "&$expand=relations" : ""),
            ops,
            ct);
        return Mapping.WorkItemDetail(updated, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
    });

    [McpServerTool(Name = "create_work_item", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Create a " +
                 "work item. `type` accepts the project's type names leniently (Bug, Task, \"User " +
                 "Story\", ...). On most process templates a Bug shows `repro_steps` where other " +
                 "types show `description`. `parent` files it under an existing work item. Returns " +
                 "the created work item with its id.")]
    public Task<WorkItemDetailDto> CreateWorkItem(
        [Description("Work item type, e.g. Bug, Task, \"User Story\"")] string type,
        [Description("Title")] string title,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Description (plain text or HTML)")] string? description = null,
        [Description("Repro steps (what a Bug's form shows instead of a description)")] string? repro_steps = null,
        [Description("Acceptance criteria")] string? acceptance_criteria = null,
        [Description("Assignee: display name, email, or identity GUID")] string? assigned_to = null,
        [Description("Area path")] string? area = null,
        [Description("Iteration path")] string? iteration = null,
        [Description("Tag(s), comma-separated")] string? tags = null,
        [Description("Priority, as this project's process defines it (commonly 1-4, 1 highest). " +
                     "Omitted means the process's own default, which is usually 2 — pass it to " +
                     "mean it.")] int? priority = null,
        [Description("Work item id to parent the new item under")] int? parent = null,
        CancellationToken ct = default) => Run("create_work_item",
        A("project", project) + A("type", type) + AdoMcpLog.ContentArg("title", title) +
        AdoMcpLog.ContentArg("description", description) + AdoMcpLog.ContentArg("repro_steps", repro_steps) +
        AdoMcpLog.ContentArg("acceptance_criteria", acceptance_criteria) +
        A("assigned_to", assigned_to) + A("area", area) + A("iteration", iteration) + A("tags", tags) +
        A("priority", priority) + A("parent", parent), async () =>
    {
        RequireWriteEnabled();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new McpException("`title` is required.");
        }
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedType = await ResolveTypeAsync(client, resolvedProject.Id, type, ct);
        var assignee = assigned_to is null ? null : await ResolveIdentityAsync(client, assigned_to, ct);

        var ops = Writes.CreatePatch(
            title, description, repro_steps, acceptance_criteria, assignee, area, iteration,
            tags is null ? null : Writes.MergeTags(null, tags, null), priority);
        if (parent is { } parentId)
        {
            // Nothing exists yet to be parented elsewhere, so this is always a bare add.
            ops.AddRange(Writes.SetParent(relations: null, parentId, client.OrgUrl));
        }

        var created = await client.PatchAsync<WireWorkItem>(
            HttpMethod.Post,
            // The route's $ prefix on the type name is literal. The name itself may contain spaces.
            $"{Escape(resolvedProject.Id)}/_apis/wit/workitems/${Escape(resolvedType.Name)}?{Api}" +
            (parent is null ? "" : "&$expand=relations"),
            ops,
            ct);
        return Mapping.WorkItemDetail(created, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
    });

    [McpServerTool(Name = "add_pull_request_comment", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Comment on " +
                 "a pull request: omit `thread_id` to start a new thread, pass one (from " +
                 "get_pull_request) to reply on it. Returns the created comment and its thread id.")]
    public Task<PullRequestCommentResult> AddPullRequestComment(
        [Description("Pull request id")] int id,
        [Description("Comment text (Markdown)")] string text,
        [Description("Existing thread id to reply on; omit to start a new thread")] int? thread_id = null,
        [Description("Comment id within that thread to reply to")] int? parent_comment_id = null,
        CancellationToken ct = default) => Run("add_pull_request_comment",
        A("id", id) + AdoMcpLog.ContentArg("text", text) + A("thread_id", thread_id) +
        A("parent_comment_id", parent_comment_id), async () =>
    {
        RequireWriteEnabled();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException("`text` is empty.");
        }
        if (parent_comment_id is not null && thread_id is null)
        {
            throw new McpException("`parent_comment_id` names a comment within a thread — pass `thread_id` with it.");
        }
        var client = await ado.GetClientAsync(ct);
        // Same organization-level lookup as get_pull_request: the id alone finds the pull request,
        // and the threads route needs the project and repository it reports.
        var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);
        if (pr.Repository?.Id is not { } repoId || pr.Repository?.Project?.Id is not { } projectId)
        {
            throw new McpException($"Pull request {id} did not report its repository, so its threads cannot be addressed.");
        }
        var basePath = $"{Escape(projectId)}/_apis/git/repositories/{Escape(repoId)}/pullRequests/{id}/threads";

        if (thread_id is { } threadId)
        {
            object body = parent_comment_id is { } parent
                ? new { parentCommentId = parent, content = text, commentType = "text" }
                : new { content = text, commentType = "text" };
            var comment = await client.PostAsync<WireComment>($"{basePath}/{threadId}/comments?{Api}", body, ct);
            var (mapped, truncated) = Text.Truncate(Text.FromMarkdown(comment.Content), WriteEchoBodyLimit);
            return new PullRequestCommentResult(
                id, threadId,
                new CommentDto(comment.Id, comment.Author?.DisplayName, comment.PublishedDate, null, mapped, truncated),
                Mapping.PullRequestUrl(client.OrgUrl, pr));
        }

        var thread = await client.PostAsync<WireThread>(
            $"{basePath}?{Api}",
            new { comments = new[] { new { content = text, commentType = "text" } }, status = "active" },
            ct);
        var dto = Mapping.Thread(thread, includeSystem: true, WriteEchoBodyLimit, new SkipCounter())
            ?? throw new McpException("Azure DevOps answered the thread creation with an empty thread.");
        return new PullRequestCommentResult(
            id, dto.Id, dto.Comments[0], Mapping.PullRequestUrl(client.OrgUrl, pr));
    });

    // Queuing a run consumes agents and can deploy things, so it sits behind the same write gate
    // as the tools that edit work items — a "read-only" registration must not be able to start
    // builds. Not destructive (it adds a run, overwrites nothing) and not idempotent (each call
    // queues another).
    [McpServerTool(Name = "run_pipeline", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Queue a " +
                 "run of a pipeline, optionally on a specific branch. Returns the queued run in " +
                 "the same shape list_pipeline_runs uses; pass its id to wait_for_pipeline_run to " +
                 "follow it to completion.")]
    public Task<PipelineRunDto> RunPipeline(
        [Description("Pipeline id (number) or name")] string pipeline,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Branch to run, e.g. main; omit for the pipeline's default branch")] string? branch = null,
        CancellationToken ct = default) => Run("run_pipeline",
        A("pipeline", pipeline) + A("project", project) + A("branch", branch), async () =>
    {
        RequireWriteEnabled();
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedPipeline = await ResolvePipelineAsync(client, resolvedProject.Id, pipeline, ct);
        var definitionId = int.Parse(resolvedPipeline.Id, CultureInfo.InvariantCulture);

        // Queued through the build API for the same reason runs are read through it: the response
        // is the same WireBuild shape every run tool speaks, so the queued run comes back exactly
        // as list_pipeline_runs would report it — including the id wait_for_pipeline_run takes.
        object body = branch is null
            ? new { definition = new { id = definitionId } }
            : new { definition = new { id = definitionId }, sourceBranch = FullBranch(branch) };
        var build = await client.PostAsync<WireBuild>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds?{Api}", body, ct);
        return Mapping.Run(build, client.OrgUrl, resolvedProject.Name);
    });

    // ------------------------------------------------------------------- helpers

    /// <summary>
    /// The gate every mutating tool calls first: writes are visible to other people, so they are
    /// opt-in per environment rather than something a config file can switch on.
    /// </summary>
    internal static void RequireWriteEnabled()
    {
        if (!AdoContext.WriteEnabled)
        {
            throw new McpException(
                "Writing is disabled. Set ADO_MCP_ALLOW_WRITE=true in this server's environment to " +
                "opt in to changes other people will see.");
        }
    }

    private static int _sequence;

    /// <summary>
    /// Wraps every tool call: assigns the <c>req=N</c> correlation id that the REST handler and
    /// MCP SDK events are stamped with, times the call, and records arguments and outcome.
    /// Failures log the full exception while the model sees a short message plus the req id,
    /// which is enough to find the log lines.
    /// </summary>
    internal async Task<T> Run<T>(string tool, string args, Func<Task<T>> action)
    {
        var req = Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
        var previous = AdoMcpLog.CurrentRequest;
        AdoMcpLog.CurrentRequest = req;
        var sw = Stopwatch.StartNew();
        log.Line(LogLevel.Information, Ev.ToolStart, tool + args);
        try
        {
            var result = await action();
            log.Line(LogLevel.Information, Ev.ToolOk,
                $"{tool} ok" + A("ms", sw.ElapsedMilliseconds) + Describe(result));
            return result;
        }
        catch (McpException e)
        {
            // Already a model-facing message (bad name, writing disabled, ...).
            log.Line(LogLevel.Warning, Ev.ToolFail,
                $"{tool} rejected" + A("ms", sw.ElapsedMilliseconds) + A("reason", e.Message));
            throw;
        }
        catch (AuthenticationRequiredException e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} auth-required" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException(
                "Sign-in expired or additional consent required. Run " +
                "`dotnet run --project <mcp-dotnet repo>/src/AzureDevOpsMcp -- auth` again. " + LogRef(req));
        }
        catch (AdoApiException e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} ado-error" + A("ms", sw.ElapsedMilliseconds) +
                A("status", e.Status) + A("typeKey", e.TypeKey) + A("path", e.Path), e);
            throw new McpException($"Azure DevOps error {e.Status}: {e.Message} {LogRef(req)}");
        }
        catch (OperationCanceledException e)
        {
            log.Line(LogLevel.Warning, Ev.ToolFail, $"{tool} cancelled" + A("ms", sw.ElapsedMilliseconds), e);
            throw;
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} unhandled" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException($"{e.GetType().Name}: {e.Message} {LogRef(req)}");
        }
        finally
        {
            AdoMcpLog.CurrentRequest = previous;
        }
    }

    /// <summary>Points the caller at the exact log lines for this call.</summary>
    private static string LogRef(string req) => $"(details: grep \"req={req}\" in {AdoMcpLog.FilePath})";

    /// <summary>
    /// Summarizes a tool result for the log without dumping its content. Descriptions and comment
    /// bodies go through <see cref="AdoMcpLog.ContentArg"/>, so by default the log records only
    /// their length.
    /// </summary>
    internal static string Describe(object? result) => result switch
    {
        PullRequestsResult p => A("pullRequests", p.PullRequests.Count) + (p.HasMore is true ? A("hasMore", true) : ""),
        PullRequestDetailDto pr =>
            A("pullRequest", pr.Id) +
            A("threads", pr.Threads?.Count ?? 0) +
            A("comments", pr.Threads?.Sum(t => t.Comments.Count) ?? 0) +
            Skipped(pr.Skipped) +
            AdoMcpLog.ContentArg("description", pr.Description),
        WorkItemsResult w => A("workItems", w.WorkItems.Count) + (w.HasMore is true ? A("hasMore", true) : ""),
        WorkItemDetailDto wi =>
            A("workItem", wi.Id) +
            A("relations", wi.Relations?.Count ?? 0) +
            A("comments", wi.Comments?.Count ?? 0) +
            Skipped(wi.Skipped) +
            AdoMcpLog.ContentArg("description", wi.Description),
        DeploymentStatusResult ds =>
            A("deployables", ds.Deployables.Count) +
            A("errors", ds.Deployables.Count(x => x.Error is not null)),
        PipelineRunsResult r => A("runs", r.Runs.Count) + (r.HasMore is true ? A("hasMore", true) : ""),
        CodeSearchResult c =>
            A("results", c.Results.Count) + A("total", c.Total) + (c.HasMore is true ? A("hasMore", true) : ""),
        WorkItemSearchResult w =>
            A("results", w.Results.Count) + A("total", w.Total) + (w.HasMore is true ? A("hasMore", true) : ""),
        WikiSearchResult w =>
            A("results", w.Results.Count) + A("total", w.Total) + (w.HasMore is true ? A("hasMore", true) : ""),
        PipelineRunDetailDto run =>
            A("run", run.Id) + A("result", run.Result) + A("failedSteps", run.FailedSteps?.Count ?? 0) +
            Skipped(run.Skipped),
        PullRequestCommentResult c =>
            A("pullRequest", c.PullRequestId) + A("thread", c.ThreadId) +
            AdoMcpLog.ContentArg("comment", c.Comment.Body),
        System.Collections.ICollection c => A("count", c.Count),
        _ => "",
    };

    private static string Skipped(SkippedDto? skipped) => skipped is null
        ? ""
        : A("skipped.deleted", skipped.Deleted) + A("skipped.system", skipped.System) +
          A("skipped.succeeded", skipped.Succeeded);

    // -------------------------------------------------------------- WIQL construction

    /// <summary>
    /// Builds the query the filter arguments describe. Pure: everything it needs is passed in, and
    /// the result is echoed back to the caller so a filter that matched nothing can be inspected.
    /// </summary>
    internal static string BuildWiql(
        string project,
        IReadOnlyList<string> areaPaths,
        string? type,
        string? state,
        string? assignedTo,
        DateTimeOffset? changedSince,
        string? titleContains)
    {
        var where = new List<string> { $"[System.TeamProject] = '{Quote(project)}'" };

        if (In("System.WorkItemType", type) is { } typeClause)
        {
            where.Add(typeClause);
        }
        if (In("System.State", state) is { } stateClause)
        {
            where.Add(stateClause);
        }
        if (areaPaths.Count > 0)
        {
            where.Add("(" + string.Join(" OR ", areaPaths.Select(p => $"[System.AreaPath] UNDER '{Quote(p)}'")) + ")");
        }
        if (assignedTo is { Length: > 0 })
        {
            where.Add(assignedTo.Trim().TrimStart('@').Equals("me", StringComparison.OrdinalIgnoreCase)
                ? "[System.AssignedTo] = @Me"
                // An email is exact. A bare name is almost always a fragment of a display name.
                : assignedTo.Contains('@', StringComparison.Ordinal)
                    ? $"[System.AssignedTo] = '{Quote(assignedTo)}'"
                    : $"[System.AssignedTo] CONTAINS '{Quote(assignedTo)}'");
        }
        if (changedSince is { } since)
        {
            where.Add($"[System.ChangedDate] >= '{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
        }
        if (titleContains is { Length: > 0 })
        {
            where.Add($"[System.Title] CONTAINS '{Quote(titleContains)}'");
        }

        return "SELECT [System.Id] FROM WorkItems WHERE " + string.Join(" AND ", where) +
               " ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>Comma-separated values become an IN clause. A single value stays an equality.</summary>
    private static string? In(string field, string? values)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }
        var parts = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => null,
            1 => $"[{field}] = '{Quote(parts[0])}'",
            _ => $"[{field}] IN ({string.Join(", ", parts.Select(p => $"'{Quote(p)}'"))})",
        };
    }

    /// <summary>WIQL string literals escape a quote by doubling it. There is no backslash escape.</summary>
    private static string Quote(string value) => value.Replace("'", "''");

    internal static DateTimeOffset? ParseTimestamp(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return ts;
        }
        throw new McpException($"Could not parse `{parameterName}` value '{value}' as an ISO-8601 timestamp.");
    }

    // ---------------------------------------------------------------- name resolution

    /// <summary>An id/name pair: what every resolver returns and what the log line reports.</summary>
    internal readonly record struct Named(string Id, string Name);

    /// <summary>
    /// The one lenient-resolution rule, shared by projects, repositories, pipelines and teams: an
    /// input that already looks like an id passes straight through, otherwise names are matched
    /// case-insensitively, exact first and substring second, and anything other than exactly one
    /// match is an error that lists what was available.
    /// </summary>
    internal static Named Resolve(
        string input, Func<string, bool> isId, IReadOnlyList<Named> candidates, string kind, ILogger log)
    {
        if (isId(input))
        {
            return new Named(input, input);
        }

        var matches = candidates.Where(c => string.Equals(c.Name, input, StringComparison.OrdinalIgnoreCase)).ToList();
        var how = "exact";
        if (matches.Count == 0)
        {
            matches = candidates.Where(c => c.Name.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
            how = "substring";
        }
        if (matches is [var single])
        {
            log.Line(LogLevel.Debug, Ev.Resolve,
                $"{kind} resolved" + AdoMcpLog.Arg("input", input) + AdoMcpLog.Arg("match", how) +
                AdoMcpLog.Arg("name", single.Name) + AdoMcpLog.Arg("id", single.Id) +
                AdoMcpLog.Arg("candidates", candidates.Count));
            return single;
        }
        throw matches.Count == 0
            ? new McpException(
                $"No {kind} matches '{input}'. Available: {string.Join(", ", candidates.Select(c => c.Name))}")
            : new McpException(
                $"{kind} name '{input}' is ambiguous: {string.Join(", ", matches.Select(c => c.Name))}. Use the id.");
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);

    private static bool IsNumber(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out _);

    private async Task<Named> ResolveProjectAsync(AdoClient client, string? project, CancellationToken ct)
    {
        var name = project ?? AdoContext.DefaultProject
            ?? throw new McpException(
                "No project given. Pass `project`, or set ADO_MCP_PROJECT in this server's environment " +
                "to make one the default.");
        var projects = await ListProjectsInternal(client, limit: 1000, ct);
        var candidates = projects.Where(p => p.Id is not null && p.Name is not null)
            .Select(p => new Named(p.Id!, p.Name!)).ToList();
        var resolved = Resolve(name, IsGuid, candidates, "project", log);
        // A GUID passes straight through Resolve, but the list already knows its display name,
        // which browser links and the Search service's Project filter need.
        if (IsGuid(resolved.Name))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Id, resolved.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return resolved;
    }

    private async Task<Named> ResolveRepoAsync(AdoClient client, string projectId, string repo, CancellationToken ct)
    {
        var repos = await ListReposInternal(client, projectId, ct);
        return Resolve(
            repo, IsGuid,
            repos.Where(r => r.Id is not null && r.Name is not null)
                .Select(r => new Named(r.Id!, r.Name!)).ToList(),
            "repository", log);
    }

    private async Task<Named> ResolvePipelineAsync(
        AdoClient client, string projectId, string pipeline, CancellationToken ct)
    {
        var pipelines = await ListPipelinesInternal(client, projectId, limit: 1000, ct);
        return Resolve(
            pipeline, IsNumber,
            pipelines.Where(p => p.Name is not null)
                .Select(p => new Named(p.Id.ToString(CultureInfo.InvariantCulture), p.Name!)).ToList(),
            "pipeline", log);
    }

    private async Task<Named> ResolveTypeAsync(
        AdoClient client, string projectId, string type, CancellationToken ct)
    {
        var response = await client.GetAsync<ListResponse<WireWorkItemType>>(
            $"{Escape(projectId)}/_apis/wit/workitemtypes?{Api}", ct);
        return Resolve(
            type, _ => false,
            (response.Value ?? []).Where(t => t.Name is not null)
                .Select(t => new Named(t.Name!, t.Name!)).ToList(),
            "work item type", log);
    }

    /// <summary>
    /// Resolves an assignee to a value identity fields accept. An email or a GUID passes through,
    /// since Azure DevOps resolves those exactly and fails loudly on a miss. A bare name goes
    /// through the identity service on the vssps host with the usual lenient-match rule. Because
    /// this feeds a write, an ambiguous name is an error listing the candidates, never a guess.
    /// </summary>
    private async Task<string> ResolveIdentityAsync(AdoClient client, string input, CancellationToken ct)
    {
        if (input.Contains('@', StringComparison.Ordinal) || IsGuid(input))
        {
            return input;
        }
        var response = await client.GetAsync<ListResponse<WireIdentitySearchResult>>(
            $"{Writes.VsspsBaseUrl(client.OrgUrl)}/_apis/identities?{IdentityApi}" +
            $"&searchFilter=General&filterValue={Uri.EscapeDataString(input)}", ct);
        var candidates = (response.Value ?? [])
            .Where(i => i.IsActive is not false)
            .Select(i => (Value: Writes.IdentityValue(i), Name: Writes.IdentityDisplayName(i)))
            .Where(c => c.Value is not null && c.Name is not null)
            .Select(c => new Named(c.Value!, c.Name!))
            .ToList();
        return Resolve(input, _ => false, candidates, "identity", log).Id;
    }

    private async Task<Named> ResolveTeamAsync(
        AdoClient client, string projectId, string team, CancellationToken ct)
    {
        var response = await client.GetAsync<ListResponse<WireTeam>>(
            $"_apis/projects/{Escape(projectId)}/teams?{Api}&$top=1000", ct);
        return Resolve(
            team, IsGuid,
            (response.Value ?? []).Where(t => t.Id is not null && t.Name is not null)
                .Select(t => new Named(t.Id!, t.Name!)).ToList(),
            "team", log);
    }

    /// <summary>
    /// The area paths a team owns. This is what "the work my team is doing" means in Azure DevOps:
    /// a team is defined by its area paths, not by a field on the work item.
    /// </summary>
    private async Task<List<string>> TeamAreaPathsAsync(
        AdoClient client, Named project, string team, CancellationToken ct)
    {
        var resolved = await ResolveTeamAsync(client, project.Id, team, ct);
        var values = await client.GetAsync<WireTeamFieldValues>(
            $"{Escape(project.Id)}/{Escape(resolved.Id)}/_apis/work/teamsettings/teamfieldvalues?{Api}", ct);
        var paths = (values.Values ?? [])
            .Select(v => v.Value)
            .OfType<string>()
            .Where(v => v.Length > 0)
            .ToList();
        if (paths.Count == 0 && values.DefaultValue is { Length: > 0 } fallback)
        {
            paths.Add(fallback);
        }
        log.Line(LogLevel.Debug, Ev.Resolve,
            "team area paths" + A("team", resolved.Name) + A("paths", string.Join(" | ", paths)));
        return paths;
    }

    // ------------------------------------------------------------------ list helpers

    private async Task<List<WireProject>> ListProjectsInternal(AdoClient client, int limit, CancellationToken ct)
    {
        var results = new List<WireProject>();
        string? token = null;
        do
        {
            var path = $"_apis/projects?{Api}&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WireProject>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page, "list_projects next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    private static async Task<List<WireRepo>> ListReposInternal(
        AdoClient client, string projectId, CancellationToken ct)
    {
        // This endpoint answers with every repository at once. There is no continuation to follow.
        var response = await client.GetAsync<ListResponse<WireRepo>>(
            $"{Escape(projectId)}/_apis/git/repositories?{Api}", ct);
        return response.Value ?? [];
    }

    private async Task<List<WirePipeline>> ListPipelinesInternal(
        AdoClient client, string projectId, int limit, CancellationToken ct)
    {
        var results = new List<WirePipeline>();
        string? token = null;
        do
        {
            var path = $"{Escape(projectId)}/_apis/pipelines?{Api}&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WirePipeline>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page, "list_pipelines next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    /// <summary>
    /// Reads work items in batches. The endpoint caps a request at 200 ids and answers 400 (not a
    /// truncated list) when given more, so the batching is required for correctness.
    /// </summary>
    private async Task<List<WireWorkItem>> GetWorkItemsAsync(
        AdoClient client, List<int> ids, CancellationToken ct)
    {
        const int batchSize = 200;
        var fields = string.Join(",", Mapping.ListFields);
        var results = new List<WireWorkItem>(ids.Count);
        for (var start = 0; start < ids.Count; start += batchSize)
        {
            var batch = ids.Skip(start).Take(batchSize);
            var path = $"_apis/wit/workitems?{Api}" +
                       $"&ids={string.Join(",", batch)}" +
                       $"&fields={Uri.EscapeDataString(fields)}";
            var page = await client.GetAsync<ListResponse<WireWorkItem>>(path, ct);
            results.AddRange(page.Value ?? []);
            if (start + batchSize < ids.Count)
            {
                log.Line(LogLevel.Debug, Ev.Page, "work item batch" + A("so far", results.Count));
            }
        }
        return results;
    }

    /// <summary>Accepts "main" as readily as "refs/heads/main". The API only understands the latter.</summary>
    internal static string FullBranch(string branch) =>
        branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
