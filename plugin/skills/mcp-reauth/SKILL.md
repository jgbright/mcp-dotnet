---
name: mcp-reauth
description: Re-authenticate the Teams MCP server with Claude driving the Microsoft device-code flow end to end — background-runs `teams-mcp auth`, scrapes the device code, stages and drives the login page in the browser, and leaves the user only the final biometric. Use when Teams MCP tools fail with auth or disconnect errors, when the user asks to "log in to Teams MCP", "re-auth MCP", "fix Teams auth", or when a pre-drafting health probe fails.
user_invocable: true
---

# MCP re-auth, browser-driven

Converts the Teams MCP re-auth from a manual context-switch into a mostly-automated flow where the user's only action is a biometric or MFA approval.

`teams-mcp` signs in as a public client against the tenant and app registration named by `TEAMS_MCP_TENANT_ID` and `TEAMS_MCP_CLIENT_ID` (environment variables — the server refuses to start the flow without them). The resulting record and token cache persist under `%LOCALAPPDATA%\teams-mcp\`, so this flow is needed once per machine and then only when the record dies.

## Step 0 — probe before you authenticate

Call a cheap Teams tool first (`mcp__teams__get_current_user`), or from a shell run `teams-mcp selftest`, which does a silent-auth Graph round-trip and prints raw errors. **Only run the flow below if the probe fails.** An existing token cache does NOT short-circuit `teams-mcp auth` — it runs the full interactive flow regardless, so re-running it against healthy auth costs the user an entire sign-in for nothing.

## The flow

1. **Start the sign-in in the background** (`run_in_background: true`):

   ```
   teams-mcp auth
   ```

   Device-code is the default. (`TEAMS_MCP_AUTH=browser` switches to a browser pop-up flow — that variant needs no driving at all beyond the user completing the pop-up, so the rest of this skill is about the device-code path.)

2. **Scrape the device code from the task output.** Azure.Identity prints the standard Microsoft instruction line — a URL (typically `https://microsoft.com/devicelogin`) and a code. Take both from the actual output rather than assuming the URL; Microsoft has used more than one.

3. **Drive the browser** (claude-in-chrome; pair/select the extension instance first):
   - Navigate to the device URL, `read_page` (interactive filter), `form_input` the code into the "Code" textbox, click **Next**.
   - If an account picker appears (work + personal tiles), click the **work account** tile for the tenant being signed into.
   - **Entra remembers the last verification method.** If the last run used Windows Hello, the account click goes straight to the FIDO bridge page — no detour needed. If it lands on the Authenticator push screen instead, **don't send the push**: click **"Sign in another way"** → **"Face, fingerprint, PIN or security key"**.
   - Tell the user the Windows Security window is popping — their biometric finishes it. The OS dialog takes foreground focus by itself, so no tab-focus gymnastics are needed. While that dialog is open, the browser page behind it renders greyed-out — a screenshot showing the faded FIDO page means "waiting on the human", not "stuck".
   - After the biometric, one last page appears: **"Are you trying to sign in to <the app registration's display name>?"** with Cancel/Continue — device-code anti-phishing confirmation. With the user present and having initiated the run, click **Continue** to finish; otherwise leave it for them.

4. **Confirm completion** from the background task output: `teams-mcp auth` blocks until sign-in completes and exits 0 on success (it prints the path of its log file at startup; failures land there in full). Follow up with `teams-mcp selftest` or a cheap MCP tool call to confirm the record works.

## Fallback: the Authenticator push route

If Windows Hello isn't available, click **Send notification** instead. The page then shows a boxed two-digit number-match code — read it off a screenshot and relay it to the user **immediately**; pushes expire in about a minute. The user approves on their phone and types that number.

## Boundaries

- Claude fills the device code, clicks through navigation, and selects verification method. Claude never enters a password, never completes a biometric or MFA approval, and never interacts with the OS security dialog — that is the user's step, by design.
- The device code is a short-lived pairing code, not a secret credential; relaying it in chat is fine.
- The tenant and client ids are configuration, not secrets, but they are the org's — they live in environment variables, never in this file.

## Gotchas

- **Token cache doesn't prevent the interactive flow** — hence step 0's probe-first rule.
- **A stale record for a different tenant/client** — the server logs a mismatch and re-prompts when the environment variables change out from under an existing record. Re-running `teams-mcp auth` replaces the record.
- **Extension instances are named generically** ("Browser 1"); different browsers are indistinguishable until named via the pairing broadcast. Single-instance setups: select it and go.
- **Extension tools cannot steal OS window focus.** The Hello route sidesteps this because the OS dialog self-foregrounds; for anything else, "the tab is ready, click X" is the reliable handoff.
