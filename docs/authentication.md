# Authentication

Both servers sign in to Entra ID as the user, through `Azure.Identity`, and both split that into two
flows that share one on-disk cache.

## Why it is split

An MCP stdio server cannot prompt. Its stdout is the JSON-RPC transport and its stderr is usually
discarded by the client, so a device code printed from server mode reaches nobody, and an
interactive browser flow launched from server mode blocks a call that the client will eventually
give up on.

So interaction happens in a separate process run by hand:

| | `-- auth` | Server mode |
| --- | --- | --- |
| Credential | `DeviceCodeCredential` (default) or `InteractiveBrowserCredential` (`…_AUTH=browser`) | `DeviceCodeCredential` with `DisableAutomaticAuthentication = true` |
| May prompt | yes, on the console | never — it throws instead |
| Writes | the authentication record, the token cache, and (Teams) the consented scopes | refreshes the cache; may correct the scope record |
| Failure mode | the exception, printed | an `McpException` telling the caller to run `-- auth` |

`DisableAutomaticAuthentication` is the load-bearing flag. With it, a missing or expired sign-in
surfaces as `AuthenticationRequiredException`, which `Run` maps to an instruction; without it the
credential would try to start a device-code flow from inside the server.

The token is acquired **up front**, in `GetClientAsync`, rather than lazily on the first service
call. An auth problem should be reported as an auth problem at a known point, not as a confusing
failure inside an unrelated Graph or REST call.

## What is on disk

Under `%LOCALAPPDATA%\{teams-mcp|ado-mcp}\` — .NET's local-application-data folder, so
`$XDG_DATA_HOME` (default `~/.local/share`) on Linux and `~/Library/Application Support` on macOS:

| File | Written by | Holds |
| --- | --- | --- |
| MSAL persistent token cache (named `teams-mcp` / `ado-mcp`) | `Azure.Identity` | The refresh token. OS-protected: DPAPI on Windows, Keychain on macOS, libsecret on Linux |
| `auth-record.json` | `-- auth` | The `AuthenticationRecord`: username, tenant, client, authority — identity, not scopes |
| `auth-scopes.json` | Teams only | The scopes the last sign-in's token actually carried |
| `logs/{teams,ado}-mcp.log` | both modes | See [observability.md](observability.md) |

`deployments.json` also defaults to this directory, but it is operator data rather than auth
material — see [azure-devops-server.md](azure-devops-server.md#the-deployment-map).

On headless Linux with no keyring, `auth` fails with a cache-persistence error. Neither server opts
into the unencrypted-file fallback, so the fix is to provide a keyring. Sign-in itself works over
SSH: the device-code flow needs a console here and a browser somewhere.

## Two settings must stay in sync or the cache silently misses

**The cache `Name` and the `AuthenticationRecord`.** Both flows use the same `CacheName` constant
and the server reloads the record the `auth` flow serialized. A mismatch means the silent path
looks in a cache partition that has never held this account's refresh token.

**`isCaeEnabled` must be `false` on both sides.** MSAL partitions the persisted cache by CAE flag,
so a CAE-enabled request would not find a refresh token cached by a non-CAE one. Teams sets
`isCaeEnabled: false` on its `AzureIdentityAuthenticationProvider`; Azure DevOps routes every
request through `AdoContext.RequestContext`, which constructs the `TokenRequestContext` with the
flag off. Neither is a default — both are explicit, and both are easy to lose by constructing a
`TokenRequestContext(scopes)` inline.

A third mismatch is only diagnosed, not prevented: if `…_TENANT_ID` or `…_CLIENT_ID` changed since
sign-in, the cached refresh token belongs to a different app or tenant and will never be found.
`GetClientAsync` compares the record against the environment and logs `auth.mismatch` before
attempting, because without that the failure is silent.

## Teams: the scope list follows the send gate

`GraphContext.Scopes` is computed, not constant: `ScopesFor(SendEnabled)` — the five read scopes,
plus `ChannelMessage.Send` and `ChatMessage.Send` only when `TEAMS_MCP_ALLOW_SEND=true`. So a
read-only deployment never asks anyone to consent to posting as the signed-in user.

Three things about that were measured rather than assumed, and a change here should keep them true.

**Narrowing the request narrows consent, not the token.** Entra returns every scope the user has
already granted the app registration. A five-scope read-only request against a consented tenant came
back carrying `ChannelMessage.Send`, `ChatMessage.Send` and several scopes this server never asks
for. Least privilege here is about what a *first* sign-in grants; it cannot take a permission back.

**The gate can therefore outrun consent.** `auth` and the server each compute the scope list when
they run, so turning the gate on after a read-only sign-in asks for scopes that sign-in may never
have consented to. That is the failure mode the reduction introduces, and `ScopeConsent` is what
catches it: `auth` writes the granted set to `auth-scopes.json` beside the authentication record
(which carries identity but not scopes), and `GetClientAsync` compares before acquiring. On a
shortfall it logs `auth.mismatch` and then either succeeds — consent was already in place, and the
record is corrected — or fails with an `McpException` naming the missing scopes.

**Missing or unreadable means unknown, never empty.** A sign-in from before the file existed
consented to everything it needed, and a warning on every startup of a server that works is worse
than no warning. `ScopeConsent.Read` returns `null` for absent *and* for corrupt, and `Missing`
answers "nothing missing" for a null.

**The recorded set is read off the token's `scp` claim**, not off the request, because those differ.
`ScopeConsent.FromToken` base64url-decodes the JWT payload and reads `scp`, tolerating both the
space-delimited string and the array form; it returns `null` for anything it cannot parse, and the
token never leaves that method. The file is written through a temporary file and moved into place,
because several server processes share the directory and a torn read would look exactly like "never
consented".

`-- auth` and `-- selftest` both print requested next to granted, which is the fastest way to see
where a send refusal is coming from.

## Azure DevOps: one resource scope, and a 200 that means 401

There is no scope list. `AdoContext.Scopes` is a single `…/.default` against Azure DevOps' own Entra
application id, `499b84ac-1321-427f-aa17-267ca6975798` — a fixed, first-party, public identifier for
the resource being requested, and the one hardcoded id in this repository. It is not this server's
client id, which comes from `ADO_MCP_CLIENT_ID`. Nothing there can be reduced, so there is no
consent record to keep.

Authentication is against Entra rather than a personal access token deliberately: a PAT is a
long-lived bearer secret that would sit in the MCP client's config, whereas the refresh token here
lives in the OS-protected cache and follows the organization's conditional-access policy. The app
registration needs delegated permission to Azure DevOps (`user_impersonation`), and the organization
must allow Entra access (Organization settings → Security → Policies).

Two implementation details follow from the service:

- **`BearerTokenHandler` caches the token itself** rather than calling the credential per request.
  `Azure.Identity` caches too, but every call goes through its internal lock, which would serialize
  concurrent requests. The cached value is an immutable record read from a `volatile` field, so the
  fast path is lock-free and cannot tear the way a bare `AccessToken` struct could; the refresh
  happens behind a `SemaphoreSlim` five minutes before expiry.
- **An unauthenticated request is answered with a sign-in page and a success status**, not a 401, on
  JSON and plain-text endpoints alike. `AdoClient.ThrowIfSignInPage` detects `text/html` and throws
  an `AdoApiException` saying the token was rejected. Without that check it surfaces as an
  unintelligible JSON parse error.

## Changing what a server can reach

| Server | To add a capability |
| --- | --- |
| Teams | Add the scope to `GraphContext.ReadScopes`, add the delegated permission to the app registration, re-run `-- auth` to re-consent. Update the README's permission list. |
| Azure DevOps | Add the delegated permission to the app registration (possibly an organization policy change), re-run `-- auth`. Nothing in code changes. |

The app registration must be a public client in both cases: device code flow enabled, plus a
`http://localhost` Mobile/Desktop redirect URI if browser mode will be used.

## Failure modes and where they show

| Symptom | Log line | Cause |
| --- | --- | --- |
| "Not signed in" `McpException` | `auth.fail` with `record=…` | `auth-record.json` does not exist |
| Tool fails with "Sign-in expired or additional consent required" | `tool.fail … auth-required` | `AuthenticationRequiredException` from the silent path |
| Works, but logs a warning at startup | `auth.mismatch` with `env.tenant`/`record.tenant` | The environment changed since sign-in |
| Send refused after enabling the gate | `auth.mismatch` with `missing=…` | Consent predates the gate — re-run `-- auth` |
| "Azure DevOps returned a sign-in page" | `http.fail` on the failing path | Token rejected: no access to the organization, or the wrong `ADO_MCP_ORG_URL` |

`-- selftest` is the first thing to run for any of these: it exercises the same silent credential
path in console mode, where the exception and the output are visible.
