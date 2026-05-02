export interface WidgetRegistration {
  widgetId: string
  template: string
  resolve: (html: string) => void
}

class SbElementsCoordinator {
  private endpoint = ''
  private pending: WidgetRegistration[] = []
  private scheduled = false

  configure(endpoint: string) {
    this.endpoint = endpoint.replace(/\/$/, '')
  }

  register(reg: WidgetRegistration) {
    this.pending.push(reg)
    if (!this.scheduled) {
      this.scheduled = true
      queueMicrotask(() => this.flush())
    }
  }

  private async flush() {
    if (this.pending.length === 0) return
    const batch = [...this.pending]
    this.pending = []
    this.scheduled = false

    // Deduplicate: first template wins for each widgetId
    const seen = new Map<string, string>()
    for (const reg of batch) {
      if (!seen.has(reg.widgetId)) seen.set(reg.widgetId, reg.template)
    }

    const body = { widgets: Object.fromEntries(seen) }

    try {
      const res = await fetch(`${this.endpoint}/_stylobot/partials/render`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'accept': 'text/html' },
        body: JSON.stringify(body)
      })
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const html = await res.text()
      const fragments = parseFragments(html)
      // Resolve ALL registrations for each widgetId (not just first)
      for (const reg of batch) reg.resolve(fragments[reg.widgetId] ?? '')
    } catch {
      for (const reg of batch) reg.resolve('')
    }
  }
}

function parseFragments(html: string): Record<string, string> {
  const result: Record<string, string> = {}
  const doc = new DOMParser().parseFromString(html, 'text/html')
  for (const el of Array.from(doc.body.children)) {
    const id = el.getAttribute('data-sb-widget')
    if (id) result[id] = el.outerHTML
  }
  return result
}

export const sbCoordinator = new SbElementsCoordinator()
