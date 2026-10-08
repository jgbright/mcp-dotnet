export const TEAMS = 'mcp__plugin_mcp-dotnet_teams__' as const

/** The line teams-followup appends under a draft so the user knows a reaction posts it. */
const CUE = /\n\s*-{3,}\s*\n\s*\*React to approve[^\n]*\*\s*$/

export const hasCue = (body: string): boolean => /\*React to approve/.test(body)

export const normalize = (body: string): string => body.replace(/\r\n/g, '\n').replace(CUE, '').trim()

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
