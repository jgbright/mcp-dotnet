import type { Register } from 'claude-code'

import { draftsPane } from './drafts'
import { gate } from './gate'

export const register: Register = (on, options) => {
  if (options.sendGate !== false) gate(on)
  draftsPane(on)
}
