export const TEAMS = 'mcp__plugin_mcp-dotnet_teams__' as const

/** The line teams-followup appends under a draft so the user knows a reaction posts it. */
const CUE = /\n\s*-{3,}\s*\n\s*\*React to approve[^\n]*\*\s*$/

export const hasCue = (body: string): boolean => /\*React to approve/.test(body)

export const normalize = (body: string): string => body.replace(/\r\n/g, '\n').replace(CUE, '').trim()

/** A line that labels a variant rather than opening it: a heading, a wholly bold or italic line, or one led by a label word. */
const LABEL = /^(#{1,6} .+|([*_]{1,2}).+\2|[#*_ ]*(option|variant|draft|version|v\d+)\b.*)$/i

/**
 * Whether a recorded self-chat draft is the outgoing text: the same text, or a label line and a blank
 * line above it ("Option A, shortest\n\n<text>"). Both sides normalized. An opening line that is no
 * label is part of the message, so "Hi\n\nYes." never approves "Yes." on its own.
 */
export const isDraftOf = (draft: string, text: string): boolean => {
  if (draft === text) return true
  if (!draft.endsWith(`\n\n${text}`)) return false
  const label = draft.slice(0, -text.length - 2)

  return !label.includes('\n') && LABEL.test(label)
}

/** Whether a prompt tells the session to send. Which draft and where is the model's call; the gate only needs the turn. */
export const saysSend = (prompt: string): boolean => /\b(send|post|go ahead|ship it)\b/i.test(prompt)

/** An MCP result as an object: the structured content when the engine hands it over, else the text parsed. */
export const parsed = (ran: { result?: unknown; text?: string }): Record<string, any> => {
  if (ran.result && typeof ran.result === 'object' && !Array.isArray(ran.result)) {
    return ran.result as Record<string, any>
  }
  try {
    return JSON.parse(ran.text ?? '{}')
  } catch {
    return {}
  }
}
