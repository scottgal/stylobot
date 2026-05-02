export class SbSsrCoordinator {
    client;
    constructor(client) {
        this.client = client;
    }
    async renderWidgets(widgets) {
        if (widgets.length === 0)
            return {};
        return this.client.renderWidgets(widgets);
    }
    async renderWidget(widgetId, template) {
        const results = await this.renderWidgets([{ widgetId, template }]);
        return results[widgetId] ?? '';
    }
}
//# sourceMappingURL=coordinator.js.map