import { StyloBotClient, type WidgetTemplate } from '@stylobot/core';
export declare class SbSsrCoordinator {
    private readonly client;
    constructor(client: StyloBotClient);
    renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>>;
    renderWidget(widgetId: string, template?: string): Promise<string>;
}
//# sourceMappingURL=coordinator.d.ts.map