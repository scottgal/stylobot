import { StyloBotClient, type WidgetTemplate } from '@stylobot/core'

export class SbSsrCoordinator {
  private readonly client: StyloBotClient

  constructor(client: StyloBotClient) {
    this.client = client
  }

  async renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>> {
    if (widgets.length === 0) return {}
    return this.client.renderWidgets(widgets)
  }

  async renderWidget(widgetId: string, template?: string): Promise<string> {
    const results = await this.renderWidgets([{ widgetId, template }])
    return results[widgetId] ?? ''
  }
}
