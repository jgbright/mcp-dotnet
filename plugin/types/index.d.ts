export type DraftDestination = {
  label: string
  chat?: string
  team?: string
  channel?: string
  reply_to?: string
}

export type DraftVariant = { label: string; body: string; sentTo?: string }

export type Drafts = {
  topic: string
  version: number
  destination: DraftDestination
  variants: DraftVariant[]
}

export type SelfDraft = { id: string; body: string }

declare module 'claude-code' {
  interface PluginState {
    'mcp-dotnet': {
      drafts: Drafts | null
      selfDrafts: SelfDraft[]
      approved: string[]
      selfChatId: string | null
    }
  }
}
