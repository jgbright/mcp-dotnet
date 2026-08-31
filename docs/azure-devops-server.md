# Azure DevOps server

`src/AzureDevOpsMcp`, command `ado-mcp`. Reads and writes one Azure DevOps organization, fixed by
`ADO_MCP_ORG_URL`, through the REST API.

Shared conventions are in [tool-contract.md](tool-contract.md); sign-in is in
[authentication.md](authentication.md).

## No Azure DevOps client SDK, on purpose

`AdoClient` is a thin typed `HttpClient` wrapper, roughly 150 lines in `AdoContext.cs`, instead of
`Microsoft.TeamFoundationServer.Client`. The tools need control over three things that SDK hides:
paging, which fields are requested, and the HTTP logging handler.

| Method | For |
| --- | --- |
| `GetAsync<T>` | The common case |
| `GetPageAsync<T>` | Returns the `x-ms-continuationtoken` header alongside the body |
| `PostAsync<T>` | JSON bodies (WIQL, search) |
| `JsonPatchAsync<T>` | JSON Patch, see [writes](#writes) |
| `GetTextAsync` | Build logs, which are not JSON |

A relative path resolves against the organization URL; an absolute one passes through, so a log url
read off a timeline record is fetched as-is.

Failures become `AdoApiException`, carrying the status, the service's own `message`, its
machine-readable `typeKey` (e.g. `WorkItemDoesNotExistException`) and the request path. `Run` maps
that to the model-facing error.

A sign-in page comes back with a success status, not a 401 (see
[authentication.md](authentication.md#azure-devops-one-resource-scope-and-a-200-that-means-401)).
`ThrowIfSignInPage` guards both the JSON and the plain-text paths.

## Four hosts, one organization

Azure DevOps splits its APIs across hosts derived from the organization URL. Each derivation handles
the legacy `{org}.visualstudio.com` spelling as well as `dev.azure.com/{org}`.

| Host | Answers | Derived by |
| --- | --- | --- |
| `dev.azure.com/{org}` | Core: projects, git, work items, build, TFVC | `AdoContext.RequireOrgUrl` |
| `vsrm.dev.azure.com/{org}` | Release definitions, releases, deployments, approvals | `Deployments.VsrmBaseUrl` |
| `almsearch.dev.azure.com/{org}` | Code, work item and wiki search | `Search.BaseUrl` |
| `vssps.dev.azure.com/{org}` | Identities | `Writes.VsspsBaseUrl` |

## API versions

`api-version=7.1` for everything, with preview exceptions that are **not** interchangeable. Each of
these rejects a bare `7.1`:

| Constant | Value | For |
| --- | --- | --- |
| `Api` | `7.1` | Everything else |
| `CommentsApi` | `7.1-preview.3` | Work item comments; there is no GA version |
| `SearchApi` | `7.1-preview.1` | The three search endpoints |
| `IdentityApi` | `7.1-preview.1` | The vssps identity service |
| `VariableGroupsApi` | `7.1-preview.2` | Variable group names, on the task agent service |

`selftest` also hits `_apis/connectionData?api-version=7.1-preview`, preview-only for the same
reason.

Release Management is **not** an exception, though it was preview-only for years: its definitions,
releases, environment-update and approval-update endpoints all answer a bare `7.1`.

## Paging strategies

| Strategy | Endpoints |
| --- | --- |
| `x-ms-continuationtoken` header, followed until `limit` | projects, pipelines |
| `$top` / `$skip`, with a client-side scan cap | pull requests |
| One over the limit, answering `hasMore` in a single request | builds, WIQL ids |
| Batched by id | work item field reads; the endpoint **answers 400** rather than truncating when given more than 200 ids, so the batching is required for correctness |

`list_pull_requests` sizes its page by whether an author filter is in play. Without one every
returned pull request is a result, so `limit + 1` answers `hasMore` immediately; with one, full pages
are scanned until the limit or the 500 cap.

## Runs and builds are the same number

`list_pipelines` uses the pipelines API, but **runs are read through the build API**. A run id and a
build id are the same number, and the build API takes it without also needing the pipeline id,
filters and pages properly, and carries the timeline.

`get_pipeline_run` reads the build and its timeline, reporting each failed task with its stage, job
and recorded errors. `include_logs` costs one request per failed task and fetches the tail of its
log, where the error text lives; each step carries its own record's log url, so fetching is a walk
with no re-matching by name. Passed records are counted in `skipped.succeeded`; records that never
ran are neither listed nor counted.

`wait_for_pipeline_run` polls **only the build**; the timeline and logs cost extra requests and are
no use until a run finishes. It then calls the same `ReadRunAsync`, so waiting for a run and asking
about one report identically.

## Waiting on a pull request

`wait_for_pull_request` is the run waiter's shape: it polls **only the pull request** until the
status leaves `active`, then calls the same `ReadPullRequestAsync` as `get_pull_request`. `completed`
and `abandoned` are both terminal and the returned status says which; an unrecognized status is
terminal too, so a status the service invents does not poll to the timeout.

The waiters and `run_pipeline` are primitives; the server never models a delivery flow.

## Classic release pipelines

Azure DevOps has two unrelated things called a pipeline, and this server keeps them apart by name.
The `_pipeline` tools mean build/YAML pipelines and their runs. The `_release` tools mean classic
Release Management: a **release definition** is the pipeline, a **release** is one instance of it,
and its stages are **environments**, each deploying separately. A release definition never appears in
`list_pipelines`, which is why `ServerInstructions` says so: a model that concludes "no such
pipeline" from an empty `list_pipelines` is wrong in a way that reads like a correct answer.

The vocabulary is the API's and the deployment map's, kept rather than translated, so one word means
one thing across the server.

### The wire shape is not the build API's

Each of these costs a wrong answer rather than an error, so each has a test:

- **A release environment has no `failed` status.** `EnvironmentStatus` is `notStarted`,
  `inProgress`, `queued`, `scheduled`, `succeeded`, `partiallySucceeded`, `canceled`, `rejected`.
  A deployment that *failed* reports as **`rejected`**, indistinguishable from an approval somebody
  turned down except by `operationStatus`: `PhaseFailed` versus `Rejected`. Both are surfaced.
  Verified against a real failed deployment; the documentation does not say this.
- **`$expand=tasks` is what makes the per-task detail arrive.** Without it the single-release GET
  returns `deploySteps` with empty `releaseDeployPhases`, and a failed deployment looks like it ran
  no steps. `ReadReleaseWireAsync` always asks for it.
- **A task's verdict has two spellings apiece.** Release Management's `TaskStatus` enum carries both
  `failed` and `failure`, and both `succeeded` and `success`. `Mapping.IsReleaseTaskFailure` and
  `IsReleaseTaskSuccess` match both; matching one spelling silently halves what gets reported.
- **A redeploy adds an attempt rather than replacing one.** `deploySteps` keeps every attempt, so a
  stage that has since gone green still carries the failed first one. `Mapping.LatestAttempt` takes
  the highest-numbered, and the DTO reports `attempt` only when it is not 1, since a second attempt
  means a person retried.

A build timeline is a flat record list walked by `parentId`; a release nests phase → deployment job →
task. `Mapping.ReleaseFailedSteps` walks it into the same `FailedStepDto` the build side produces
(`stage` = phase, `job` = job, `task` = task), so a failed deployment and a failed build read
identically, `include_logs` works the same way, and passing tasks land in the same
`skipped.succeeded`.

### Configuration, not history

`get_release`, `list_releases` and `deployment_status` answer what a deploy *did*, not what it is
*set up to do*. A definition can override a setting at deploy time, which makes editing the
checked-in `appsettings.json` a silent no-op, and a deployed value identical to the repository value
proves nothing either way.

`get_release_definition` reads one definition whole:

- Variables at **both scopes**, definition and per environment, with `isSecret` and `allowOverride`.
- The **variable groups** each scope pulls in, as id and name. Contents are never read; the question
  a definition raises is which groups it references. The names cost one extra request to the task
  agent service, one of the two name lookups in this server whose failure is logged and swallowed
  (the deployment group names are the other): the ids identify the groups without them, and a
  missing permission must not fail a definition read.
- Per environment, the deploy phases and every task in them: name, version, disabled state, and
  **inputs**.

The inputs are the load-bearing part. A File Transform, Replace Tokens or JSON variable substitution
task carries its target file globs in `inputs`, which answers "is `appsettings.json` transformed at
all, and which keys are in scope". No variable list settles that, since substitution can match
variable names against the file rather than use a per-key mapping. Inputs the definition left empty
are dropped: a task's schema contributes every input it declares whether or not the definition set
one.

The listing endpoint returns a summary with no variables and no deploy phases, and no `$expand`
carries them, so this is a by-id read. That is also why `search_release_definitions` costs one
request per definition: it reads each in full, caps the scan at `ReleaseConfig.ScanCap` (200) with a
Warning, and sets `hasMore`. Thirteen definitions take about three seconds.

**No tool returns a value Azure DevOps marked secret**, and that binds the passthrough too (see
[the escape hatch](#the-escape-hatch-and-the-credential)). A secret's name and `isSecret: true` are
the whole answer. `search_release_definitions` matches a secret on its name only, never its value:
matching on a value the tool refuses to return would leak it a bit at a time.

### Where a stage lands

What a stage *runs* and where it *runs it* are separate configuration. Its deploy phase carries a
`deploymentInput` (`queueId`, `tags`, health option, timeout, condition), none of which names
machines. A deployment group is a resource of the task agent service, project-scoped on the core host
under `distributedtask/deploymentgroups`, and **not the ADO Environment a YAML pipeline deploys to**,
which lives under `distributedtask/environments` on the same service. The two share a word and
nothing else.

- `get_release_definition` reports each phase's `target`: the deployment group (id and name, the name
  from one `deploymentgroups?ids=` lookup swallowed on failure like the variable group names) and
  either its `tags` or `allMachines: true`. `queueId` means a deployment group only on a
  `machineGroupBasedDeployment` phase; on an `agentBasedDeployment` phase it is an agent queue,
  reported as `agentQueue`. `Targeting.IsMachineGroup` tells them apart.
- `list_deployment_groups` lists the groups and their machines: name, tags, status when not online,
  `disabled` when switched off, agent version and OS. **The listing refuses `$expand=machines`**
  (400: "no longer supported. You need to query individual deployment group"), so machines are one
  by-id read per group, and the tool's description says what that costs.
- `get_release_definition_targets` gives, per environment in rank order and per phase, the group with
  its `machineCount` and the machines the tags select now. The same answer by hand through the
  escape hatch took about a dozen requests and a manual intersection.

Each half of the selection rule was pinned against the service. A machine is selected when it
carries **all** of the phase's tags (the targets endpoint documents its `tags` filter as "contain all
these tags"; live, two tags selected only the machine carrying both), and the comparison is
**case-insensitive** (the deployment-group docs say so; live, a stage tagged `clients` deployed to
the machines tagged `Clients`). **No tags means every machine in the group**, including any
registered later, which is `Enumerable.All` on an empty list; the result spells that out as
`allMachines: true` rather than omitting an empty list. `Targeting.cs` is pure and tested against
fixtures.

Two results:

- **A phase whose tags select nothing** reports `machines: []` beside the group's `machineCount`.
  This is the one place the server emits an empty array on purpose: a deploy against zero targets
  succeeds and reads as a successful deployment. Measured: a definition whose two stages both name
  the production web group with the same tag, so "Deploy to Staging" lands on production servers and
  QA gets nothing.
- **A group that cannot be read**, deleted since the definition was saved or blocked by permissions,
  lands in that phase's `error`, the way `deployment_status` reports one broken deployable. The other
  stages still have answers.

**Agent capabilities are never requested.** An agent's `systemCapabilities` are its process
environment variables: useful (`DOTNET_ENVIRONMENT`, `USERDOMAIN`, the `Path`) and full of secrets,
with a license key sitting in one on a real agent next to `NUMBER_OF_PROCESSORS`. Azure DevOps does
**not** mark them secret, so the `isSecret` rule cannot catch them and a value heuristic would miss
whatever it did not anticipate. The by-id group read with `$expand=machines` does not carry them
anyway; only `deploymentgroups/{id}/targets?$expand=capabilities` does, and no tool asks for it.
The escape hatch serves the rare case, with the caller deciding what they are looking at.

`deployment_status` answers what *version* is out, through the external deployment map, because which
deployables exist is organization-specific. Deployment groups and tags are Azure DevOps' own data and
need no data file.

### Task detail on a release

`get_release` reports failures and counts what passed, which is right by default and wrong when the
question is "what did this stage actually run". `include_tasks=true` lists every task of the latest
attempt with its status and times, and `skipped.succeeded` then stops counting them: a task cannot
be both listed and reported as filtered out.

`task_log` fetches one task's log, often the most direct statement of what a deploy wrote (the File
Transform task logs every key it substituted). **Neither half of a release task's identity is
unique**: ids restart per stage, and a stage deploying to several machines runs the *same task name*
more than once within itself. Measured: one production stage ran two tasks both called
`File Transform: application.json`, ids 10 and 16, while id 10 in the other stage was
`Finalize Job`. So `task_log` takes an id, a name, or `stage / id`, resolves the stage first when
given, and lists the candidates with their ids when either half is ambiguous.

### Two ids for one stage

A stage has a **release environment id**, unique to the release, and a **definition environment id**,
stable across every release of the definition. The deploy endpoint addresses the former. `Resolve`
passes a number straight through, so `ResolveReleaseEnvironment` re-checks the result against the
release in hand and refuses an id that release does not have. Without it, a plausible number would
PATCH another release's stage.

### Deploying and approving

`deploy_release` is the Deploy button: it starts one environment of an existing release, and does not
create releases; one is normally created by the definition's own CI trigger. Both write tools PATCH
plain JSON through `AdoClient.PatchAsync`, added because `JsonPatchAsync` hardcodes
`application/json-patch+json`, which the release endpoints reject, then re-read so each returns the
release exactly as `get_release` would.

`approve_release` is gated twice: `RequireWriteEnabled()` **and** `RequireApprovalEnabled()`
(`ADO_MCP_ALLOW_APPROVE=true`). The write gate answers "may this server change what other people
see". An approval exists to require a human, and answering one records the signed-in person as having
authorized that deployment whether or not they read it. Enabling writes so an agent can file work
items is not agreement to that, so one variable cannot carry both permissions.

Which approval to act on is never guessed: exactly one pending approval is acted on, none is an error
saying so, and several (parallel approvers, or a pre- and a post-deploy approval at once) is an error
listing them with their ids and approvers until `approval_id` names one. Automated placeholder
approvals, the ones Azure DevOps records for a stage that needs no approval, are filtered out and
never count toward that ambiguity.

`wait_for_release` waits on one environment. A stage nobody triggered stays at `notStarted`
indefinitely and a stage held at an approval stays `queued`, so neither is terminal and waiting runs
to the timeout; the tool's description says to check `pendingApprovals` first. An unrecognized status
is terminal, as with the other waiters. The environment is resolved once against the first read, not
per poll, so a rename mid-wait cannot fail the wait.

## WIQL construction

`list_work_items` takes either a full `wiql` query or filter arguments. When it builds the query
itself it **echoes it back in `wiql`**, so a filter that matched nothing can be inspected, refined
and passed back in.

`BuildWiql` is pure and directly tested:

- Comma-separated `type`/`state` become an `IN (…)` clause; a single value stays an equality.
- `assigned_to` is `@Me` for "me", an equality for anything containing `@` (an email is exact), and
  `CONTAINS` otherwise, since a bare name is almost always a fragment of a display name.
- WIQL escapes a quote by **doubling** it. There is no backslash escape.

`team` restricts to the area paths that team owns: in Azure DevOps a team is defined by its area
paths, not by a field on the work item.

The WIQL endpoint returns ids only; fields come from a separate batched read that answers in id
order, so results are reordered back to the query's ordering before mapping.

## Search

Three tools, one request shape (`Search.Request`), one host. All scope to a project through the
route, ask for `limit` results in a single request, and answer `hasMore` from the service's own
`total` rather than fetching more. An empty list with `total: 0` really means nothing matched.

Easy to get wrong:

- `$top` and `$skip` are **literal property names** in this API, hence the `[JsonPropertyName]`
  attributes.
- Filter keys are case-sensitive identifiers (`Project`, `Repository`, `Path`, `Branch`). They
  survive the camelCase serializer because they are dictionary keys.
- `IncludeSnippet` exists only on code search, so it stays off the wire for the other two.
- **The service refuses a `Path` filter without a `Repository` filter.** A TFVC server path names its
  own repository (`Search.TfvcRepository`: `$/Core/Schema` → `$/Core`); anything else needs `repo`
  from the caller, and `search_code` says so rather than silently matching nothing.
- The service also refuses a `Repository` filter without a `Project` filter, so `Project` is always
  sent even though the route already scopes it.
- `search_code` needs the free Code Search extension installed in the organization.
- Hits arrive with matched terms wrapped in `<highlighthit>` markers and the surrounding text
  HTML-encoded. `Text.FromHighlight` strips the tags **before** decoding, so an encoded angle bracket
  in the text itself, routine in code, survives as text instead of being mistaken for markup.

## Writes

Six tools behind `ADO_MCP_ALLOW_WRITE=true`: `update_work_item`, `create_work_item`,
`add_pull_request_comment`, `run_pipeline`, `deploy_release`, `approve_release`. Each calls
`AdoTools.RequireWriteEnabled()` before anything else, including validating its own arguments, so the
refusal is the same regardless of what was passed. `approve_release` then calls a second gate,
`RequireApprovalEnabled()` (`ADO_MCP_ALLOW_APPROVE=true`); see
[Deploying and approving](#deploying-and-approving) for why it is a separate permission.

Every write returns the post-write state in the read tools' DTO shapes, so no follow-up read is
needed. Deleting things, voting on or completing pull requests, and creating releases are not
offered.

The `destructive` annotation separates writes that replace something from writes that only add.
`update_work_item` overwrites fields, `deploy_release` replaces what is running in an environment,
and `approve_release` lets that deployment happen. Filing a work item, commenting and queuing a run
are additive. An MCP client gates its confirmation prompt on the annotation.

`run_pipeline` queues a run, optionally on a branch, through the build API, the same API runs are
read through, so the queued run comes back in `list_pipeline_runs`'s shape carrying the id
`wait_for_pipeline_run` takes. It sits behind the write gate because queuing consumes agents and
can deploy things: a read-only registration must not be able to start builds.

### What the write tools reach is measured against what the read tools report

A field a model can see and cannot fix sends the work out through `az rest`. **Every field
`WorkItemDetailDto` carries is writable by one of the two work item tools**, and a new field added to
that DTO should arrive with the argument that sets it.

### JSON Patch

Work item writes go over JSON Patch: PATCH updates, POST creates, `application/json-patch+json` both
ways. Azure DevOps rejects the document under a plain `application/json`, which is why
`AdoClient.JsonPatchAsync` takes the method as a parameter.

`Writes.PatchOp` is `(Op, Path, Value)` with the value omitted when null; `remove` carries no value at
all, and writing one as `null` is rejected. `add` covers every field write. On a work item field it
creates or replaces alike, and it appends when the path is `/relations/-`.

**Priority is an integer field.** `PatchOp.Value` is `object?` rather than `string` so the op carries
a JSON number. Omitting it on a create does not mean "no priority": the process template's default
lands instead, usually 2, which `create_work_item` says in the argument's own description.

**The estimates are doubles, and there are five of them because the processes disagree.**
`Writes.Estimates` carries `Microsoft.VSTS.Scheduling.OriginalEstimate`, `RemainingWork` and
`CompletedWork` (hours, on a Task), `StoryPoints` (Agile User Story) and `Effort` (Scrum backlog
item), as one group on both patch builders. They read back through `Mapping.Number`, not
`Mapping.Int`: half an hour of remaining work and half a story point are ordinary values and an int
would round them away silently. Writing a field the type does not define is refused by Azure DevOps
naming the field, so that is left to the service. Nothing in `AddEstimates` couples them: writing an
original estimate does not touch remaining work, the field a sprint burndown reads.

**The parent is a relation addressed by index**, not by name: `System.LinkTypes.Hierarchy-Reverse` in
the item's `relations` array, of which there is at most one. So `parent` reads before it writes
(`$expand=relations`, which **cannot be combined with a `fields` list**, so one read covers the tag
merge too), matches the existing link **by work item id rather than by url spelling**, and emits
remove-then-add. Already-parented-there is zero operations and returns the item that was read rather
than PATCHing an empty document.

`Hierarchy-Forward` is a *child*. Removing one because the rel looked close enough would unparent
somebody else's work item.

### Tags and identities

`System.Tags` is one semicolon-joined field, so `add_tags`/`remove_tags` are a read-merge-write.
`Writes.MergeTags` matches case-insensitively, keeps existing casing and order, appends additions in
the order given, and returns an empty string when the last tag was removed, which is how the field is
cleared.

The merged value has to go out as a **`replace`** op, not the `add` every other field uses. Azure
DevOps unions an `add` on `System.Tags` with the tags already on the item, so an `add` can only grow
the list: a removal is accepted with a 200, leaves `System.ChangedDate` untouched, and changes
nothing. The response body echoes the item as it still is, so nothing short of re-reading the tags
catches it.

This deviates from RFC 6902, where `add` on an existing member replaces its value, so following the
standard gives the wrong answer here. Microsoft documents it only by example: the
[Work Items - Update](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1)
page runs *Add a tag* and *Update a tag* against the same fixture, and the only thing distinguishing
them is that `Tag0` survives the `add` and is gone after the `replace`. There is no *Remove a tag*
example at all.

Tag *suggestion* stays out of this server: `add_tags` applies the caller's explicit list, and deciding
what to tag is agent-side tooling's concern.

`assigned_to` accepts a display name, an email or an identity GUID. An email or GUID passes through,
since Azure DevOps resolves those exactly and fails loudly on a miss. A bare name goes through the
vssps identity service with the usual lenient-match rule, and because this feeds a write, **an
ambiguous name is an error listing the candidates, never a guess**. The value written is the account
(UPN or email) where the identity has one, because it stays readable in the work item's history; the
identity id is the fallback. The identity service wraps property values in a `{$type, $value}`
envelope, which `Writes.Property` unwraps.

`type` resolves against the project's own work item types, with the same rule.

## The deployment map

`deployment_status` is the model for **organization-specific knowledge is configuration, never
code**. Which deployables exist and what ships each one is organization-specific, so the server
knows *mechanisms* only and the list lives in an external JSON file.

### The mechanism

A deployable takes one of two forms, decided by which field names its pipeline.

**Classic**: `releaseDefinition` + `environment`. The chain is release definition → environment →
latest succeeded deployment → release → Build artifact → build.

**Pipeline**: `pipeline`, optionally through an ADO Environment named in `environment`, optionally
pinned to a `branch`. With an environment, the chain reads that environment's deployment records
(newest first, capped at 100) for the first succeeded record of this pipeline; without one, it takes
the latest succeeded run straight off the build API.

Both converge on `VersionStateAsync`, which asks what the deployed build pins down and what has
landed since:

| Build's `sourceVersion` | Means | "Since" is answered by |
| --- | --- | --- |
| numeric | a TFVC changeset | changesets under the deployable's `paths` newer than it, one query per path |
| anything else | a git commit | commits on the branch, walked newest-first until the deployed one appears |

`paths` are TFVC server-path prefixes, not globs, and are optional: when omitted they are derived at
call time from the build definition's own TFVC workspace mappings (`repository.properties.tfvcMapping`,
which arrives as a JSON string, `map` entries only, not cloaked ones). The file only has to say what
the build definition cannot.

With `changeset: N`, every TFVC-built deployable also answers `containsChangeset` (is it at or below
the deployed changeset) and `affects` (did it touch any of the deployable's paths). The asked
changeset's own paths are fetched once, outside the loop, since they answer `affects` for every
deployable.

The git walk reports `hasMore` when it does **not** find the deployed commit: either the page ran out
or the commit is no longer on the branch, and either way an exact count is not known.

A per-deployable failure is caught, logged as a Warning, and returned as that entry's `error` field,
so one broken entry does not cost the rest of the fleet answer.

### The file

`DataFile<T>` is the one mechanism for externally configured data: JSON at a well-known default path
beside the auth record (`%LOCALAPPDATA%\ado-mcp\deployments.json`), overridable by
`ADO_MCP_DEPLOYMENTS`, parsed once and **re-read when the file's timestamp changes**, so the data can
be edited without restarting a server an MCP client is holding open. Missing or invalid is an
`McpException` carrying the expected format, because the fix is operator action rather than a retry.

`Deployments.Parse` validates: a name is required and must be unique, exactly one of
`releaseDefinition` / `pipeline`, `environment` required for the classic form, `branch` rejected on
it, and every path must start with `$/`. **Unknown fields are ignored**, so the same file can carry
data for other consumers, a git-tagging script for instance. `note` is opaque passthrough.

`-- config` loads and validates every data file and prints what each says, so a data edit can be
checked without an MCP client. A new `DataFile<T>` means a new section there.

**No TFVC path, release definition or heuristic from any one organization belongs in this
repository.** Extend the mechanism, or regenerate the data.

## The escape hatch and the credential

When a typed tool does not cover something, the alternative is a shell and `AZURE_DEVOPS_PAT`: a
second, usually staler credential, failing for a reason nobody has checked, while a live token sits
unused in this process. `ado_api_request` makes it another tool call.

- **Only this organization is reachable.** A relative path is hung off the resolved host; an absolute
  url is accepted only when its host and path prefix match one of the four hosts derived from
  `ADO_MCP_ORG_URL`. The request carries this server's bearer token, so following a caller's url
  anywhere else would hand that token over.
- **`host` is inferred from the path** (`/_apis/release/` → vsrm, `/_apis/search/` → search,
  `/_apis/identities` → vssps) and an explicit value wins. Getting it wrong is a 404, not a redirect.
  Most resources are project-scoped, so a path usually starts with the project:
  `Core/_apis/release/definitions/31`, not `_apis/release/definitions/31`.
- **`api-version=7.1` is appended** when the path names no version; the service refuses a request
  without one.
- **The body's media type is inferred from the body.** A non-empty array of objects each carrying
  `op` is a JSON Patch document (RFC 6902) and goes as `application/json-patch+json`; anything else,
  including a body that will not parse, goes as `application/json`. Without this the escape hatch
  cannot reach a work item endpoint at all: every PATCH is answered 400 on the content type before
  the document is looked at. An explicit `content_type` wins, the same way an explicit `host` does.
- **`ApiRequest.Mask` walks the parsed body** and replaces the `value` of any object carrying
  `isSecret: true` with `[redacted]`. The walk is over the shape, not the endpoint, since a
  passthrough has no type for the response.
- **`filter` is a projection, not jq**: dot-separated names, `[]` to map over an array (flattening one
  level, so `environments[].deployPhases[].workflowTasks[].name` reads as one list) and `[n]` to
  index one. A filter matching nothing yields `json: null`, which is an answer.
- **Non-GET requires `ADO_MCP_ALLOW_WRITE=true`**, checked before anything else. The tool is annotated
  `ReadOnly` because it reads under every configuration this server ships with; a client gating
  confirmation on that annotation will not prompt for a write made through it with the gate open,
  which is why the gate is there and why the description names it.

`ado_auth_status` answers the other half: which credential, which app registration and tenant, when
the token expires, which organization and project it resolves to, and who Azure DevOps says it is
(`connectionData`, because a record can name an account the organization has never seen). A dead
sign-in is reported as `signedIn: false` with the reason rather than thrown; throwing would make it
indistinguishable from the failures it is called to explain. If `AZURE_DEVOPS_PAT` is set it is
probed separately and reported under `pat`; no `pat` field means the variable is unset, and no other
tool ever uses the PAT.

An expired token is answered with a whole HTML error page, stylesheet and script and navigation,
around a sentence like *"Access Denied: The Personal Access Token used has expired."*
`Text.ErrorFromHtml` strips the script and style blocks, whose contents would otherwise be quoted
first, converts what is left, and keeps the first three lines capped at 300 characters.
`AdoClient.ErrorAsync` uses it for any HTML error body, and falls back to a short plain-text body as
well: a path sent to the wrong host answers `The controller for path '…' was not found`, which says
considerably more than `Not Found (404)`.

## Caps

| Cap | Value | Behaviour at the cap |
| --- | --- | --- |
| pull request scan | 500 | `hasMore` + Warning |
| release definitions per project (`deployment_status`) | 500 | Warning: resolution may be incomplete |
| release definitions per project (`list_release_definitions`) | `limit`, default 200, max 1000 | Paged to the limit |
| release definitions read in full (`search_release_definitions`) | 200 | `hasMore` + Warning |
| deployment groups per project (`list_deployment_groups`) | `limit`, default 100, max 200 | Paged to the limit; one more request per group when machines are included |
| deployment group reads (`get_release_definition_targets`) | one per distinct group | A group that fails to read is that phase's `error` |
| `ado_api_request` response | `max_chars`, default 20000, max 200000 | Returned as truncated text instead of json |
| build definitions per project | 1000 | Warning: resolution may be incomplete |
| environment deployment records | 100 | Reported as "no succeeded deployment in the last 100 records" |
| TFVC paths searched per deployable | 10 | `hasMore` + Warning |
| work item ids per batch read | 200 | Batched; the endpoint 400s above this |
| waiter timeout (all three waiters) | 1–21600 s | Clamped; returns `timedOut: true` |
| waiter interval (all three waiters) | 5–600 s | Clamped |

## Tool inventory

Read: `list_projects`, `list_repos`, `list_pull_requests`, `get_pull_request`,
`wait_for_pull_request`, `list_work_items`, `get_work_item`, `list_pipelines`, `list_pipeline_runs`,
`get_pipeline_run`, `wait_for_pipeline_run`, `list_release_definitions`, `get_release_definition`,
`get_release_definition_targets`, `list_deployment_groups`, `search_release_definitions`, `list_releases`,
`get_release`, `wait_for_release`, `search_code`, `search_work_items`, `search_wiki`,
`deployment_status`, `ado_api_request`, `ado_auth_status`.

Write (`ADO_MCP_ALLOW_WRITE=true`): `update_work_item`, `create_work_item`,
`add_pull_request_comment`, `run_pipeline`, `deploy_release`.

Write, and additionally `ADO_MCP_ALLOW_APPROVE=true`: `approve_release`.
