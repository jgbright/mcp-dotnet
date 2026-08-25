# CLAUDE.md — AzureDevOpsMcp

Azure DevOps server specifics. The repository-wide rules are in the root `CLAUDE.md` and still
apply; `docs/azure-devops-server.md` is the long form of what follows.

## Architecture constraints

**Azure DevOps deliberately does not use the Azure DevOps client SDK.** `AdoClient` is a thin typed
`HttpClient` wrapper over the REST API, because the tools need control over paging, over which
fields are requested, and over the HTTP logging handler — all of which that SDK hides. Two
consequences worth knowing before debugging something odd:
- An unauthenticated request is answered with a **sign-in page and a success status**, not a 401.
  `AdoClient.ReadAsync` detects `text/html` and throws an `AdoApiException` saying so; without that
  check it surfaces as an unintelligible JSON parse error.
- **A run id and a build id are the same number.** `list_pipelines` uses the pipelines API, but
  runs are read through the build API, which takes a run id without also needing its pipeline id,
  filters and pages properly, and carries the timeline.

**Classic releases are a second service, not a second view of the pipelines API.** They answer on
the `vsrm` host (`Deployments.VsrmBaseUrl`), and four things about them cost a wrong answer rather
than an error if assumed away:
- **A release environment has no `failed` status.** A deployment that failed reports as `rejected`
  — the same status a turned-down approval produces — and only `operationStatus` (`PhaseFailed`
  against `Rejected`) separates them. Verified against a real failed deployment, not inferred from
  the docs. Both are surfaced, and `get_release`'s description says so, because a model that reads
  `rejected` as "a person said no" will report the wrong cause.
- **`$expand=tasks` is what makes the per-task detail arrive.** Without it `releaseDeployPhases` is
  empty and a failed deployment looks like it ran no steps, so `ReadReleaseWireAsync` always asks
  for it.
- **A task's verdict has two spellings apiece** — `failed`/`failure`, `succeeded`/`success`, both
  in Release Management's own enum. `Mapping.IsReleaseTaskFailure` matches both; matching one
  silently halves the failures reported.
- **A redeploy adds a `deploySteps` attempt rather than replacing the last one**, so the stage that
  has since gone green still carries the failed first attempt. `Mapping.LatestAttempt` takes the
  highest, and the DTO reports `attempt` only when it is not 1.

**A release definition is configuration and a release is history**, and the tools that read the
first one have their own traps:
- **The listing is a summary and there is no `$expand` that carries the rest.** Variables and deploy
  phases arrive only from the by-id read, which is why `search_release_definitions` costs one
  request per definition and caps its scan.
- **Neither half of a release task's identity is unique.** Ids restart per stage, and a stage
  deploying to several machines runs the *same task name* twice within itself — measured: one
  production stage with two `File Transform: application.json` tasks, ids 10 and 16, while id 10 in
  the other stage was `Finalize Job`. `get_release`'s `task_log` therefore takes an id, a name, or
  `stage / id`, and lists candidates carrying their own ids rather than guessing.
- **Variable group names are a separate, preview-only request** (`VariableGroupsApi`) to the task
  agent service, and the one call in this server whose failure is logged and swallowed: the ids
  identify the groups without the names, and a missing permission there must not turn a definition
  read into an error. The groups' contents are never read at all.
- **Release paths are project-scoped on the vsrm host.** `{vsrm}/{project}/_apis/release/…`; without
  the project segment the service answers 404 with a plain-text body, which is why
  `AdoClient.ErrorAsync` surfaces a short plain-text error rather than `Not Found (404)`.

Also: the *release* environment id and the *definition* environment id are different numbers for
the same stage, and the deploy endpoint takes the former. `ResolveReleaseEnvironment` resolves
against the release in hand and refuses a numeric id that release does not have, because `Resolve`
passes a number straight through and the PATCH would otherwise land on another release's stage.

**Approving is gated separately from writing, and that is not redundancy.** `approve_release` calls
`RequireWriteEnabled()` and then `RequireApprovalEnabled()` (`ADO_MCP_ALLOW_APPROVE`). The write
gate answers "may this server change things other people see"; an approval exists specifically to
require a human, and answering one records the signed-in person as having authorized that
deployment. Someone who enabled writes so an agent could file work items has not agreed to that.
This is the one place a mutating tool consults a policy beyond `RequireWriteEnabled`, and the
reason is the audit trail rather than the blast radius — do not fold it back into the write gate,
and do not add a third gate for anything whose only argument is that it feels risky.

**What the write tools reach is measured against what the read tools report**, because a field a
model can see and cannot fix sends the work out through `az rest` instead. Every field
`WorkItemDetailDto` carries is writable by one of the two work item tools; a new field added to
that DTO should arrive with the argument that sets it. The escape hatch is held to the same
standard: `ado_api_request` sends a JSON Patch body as `application/json-patch+json`
(`ApiRequest.ContentType`, overridable), because a hardcoded `application/json` there put every
work item endpoint out of reach and sent the work out to a shell anyway — which is the one thing
that tool exists to prevent. Two of the writable things are not fields at all and each
has its own trap:
- **Priority is an integer field**, so `PatchOp.Value` is `object?` rather than `string` and the
  op carries a JSON number. Omitting it on a create does not mean "no priority" — the process
  template's default lands instead (usually 2), which is why `create_work_item` says so in the
  argument's own description.
- **The parent is a relation addressed by index**, not a name: `System.LinkTypes.Hierarchy-Reverse`
  in the item's `relations` array, of which there is at most one. So `parent` reads before it
  writes (`$expand=relations`, which cannot be combined with a `fields` list — one read covers the
  tag merge too), matches the existing link **by work item id rather than by url spelling**, and
  emits remove-then-add. Already-parented-there is zero operations and returns the item that was
  read rather than PATCHing an empty document. `Hierarchy-Forward` is a *child*: removing one
  because the rel looked close enough would unparent somebody else's work item.
