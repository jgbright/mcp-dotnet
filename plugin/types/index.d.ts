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

/** A send instruction the user typed: the self-chat drafts already posted when they typed it, each one it may approve. */
export type TypedSend = { seq: number; drafts: string[] }

declare module 'claude-code' {
  interface PluginState {
    'mcp-dotnet': {
      drafts: Drafts | null
      selfDrafts: SelfDraft[]
      approved: string[]
      typed: TypedSend[]
      selfChatId: string | null
    }
  }
}
