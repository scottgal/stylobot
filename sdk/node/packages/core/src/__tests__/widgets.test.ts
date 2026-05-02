import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import type { WidgetTemplate, WidgetRenderRequest } from '../types.ts'

describe('WidgetTemplate types', () => {
  it('builds a WidgetRenderRequest from WidgetTemplate array', () => {
    const templates: WidgetTemplate[] = [
      { widgetId: 'summary', template: '{{ bot_requests }} bots' },
      { widgetId: 'topbots' },
    ]
    const req: WidgetRenderRequest = {
      widgets: Object.fromEntries(templates.map(w => [w.widgetId, w.template ?? '']))
    }
    assert.equal(req.widgets['summary'], '{{ bot_requests }} bots')
    assert.equal(req.widgets['topbots'], '')
  })
})
