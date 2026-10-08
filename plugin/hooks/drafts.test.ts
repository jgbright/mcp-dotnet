import type { On } from 'claude-code'
import { expect, test } from 'claude-code/testing'

const T = 'mcp__plugin_mcp-dotnet_teams__'

const teams = (on: On) => {
  const sent: Record<string, unknown>[] = []
  on('tool.call', ($, e) => {
    if (e.tool === `${T}send_chat_message`) {
      sent.push(e as unknown as Record<string, unknown>)
      return { result: { id: `m${sent.length}`, webUrl: `https://teams.example/m${sent.length}` } }
    }
    return { deny: `unexpected ${e.tool}` }
  })
  on('ui.open', () => ({ value: { isPlaced: true as const } }))
  on('ui.toast', () => ({ value: undefined }))

  return sent
}

const SHOW = {
  tool: 'mcp__mcp-dotnet__show_drafts' as const,
  topic: 'release note',
  version: 2,
  destination: { label: 'Mike', chat: 'Mike' },
  variants: [
    { label: 'Short', body: 'It shipped.' },
    { label: 'Fuller', body: 'It shipped to Dev today.' },
  ],
}

const PANE = (surface: 'terminal' | 'desktop') => ({
  plugin: 'mcp-dotnet',
  surface,
  component: 'Pane' as const,
  requestId: 'teams-drafts',
  props: { title: 'Teams', isFocused: true, bodyColumns: 80, placement: 'dock' } as never,
})

for (const surface of ['terminal', 'desktop'] as const) {
  test(`Send in the pane posts that exact variant to the destination (${surface})`, async ($, on) => {
    const sent = teams(on)
    await $.tool.call(SHOW)

    const ui = await $.ui.mount(PANE(surface))
    expect((await ui.find({ type: 'Markdown', text: 'It shipped to Dev today.' }))).toBeDefined()

    await ui.press({ key: 'send-1' })
    expect(sent).toHaveLength(1)
    expect(sent[0]).toMatchObject({ chat: 'Mike', body: 'It shipped to Dev today.', format: 'markdown' })
    expect(await ui.find({ text: /sent to Mike/ })).toBeDefined()
  })

  test(`Self sends a variant to the self chat (${surface})`, async ($, on) => {
    const sent = teams(on)
    await $.tool.call(SHOW)
    const ui = await $.ui.mount(PANE(surface))
    await ui.press({ key: 'self-0' })
    expect(sent[0]).toMatchObject({ chat: 'self', body: 'It shipped.' })
  })

  test(`revision notes come back as a prompt (${surface})`, async ($, on) => {
    teams(on)
    const prompts: string[] = []
    on('prompt.submit', ($, e) => {
      prompts.push(e.text)
      return { text: e.text }
    })
    await $.tool.call(SHOW)
    const ui = await $.ui.mount(PANE(surface))
    await ui.input({ key: 'revise', text: 'drop the date' })
    expect(prompts[0]).toContain('drop the date')
  })
}
