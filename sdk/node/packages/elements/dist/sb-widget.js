import { sbCoordinator } from './coordinator.js';
export class SbWidget extends HTMLElement {
    connectedCallback() {
        const widgetId = this.getAttribute('data-sb-widget') ?? this.getAttribute('id') ?? '';
        if (!widgetId)
            return;
        const templateEl = this.querySelector('template');
        const liquid = templateEl?.innerHTML.trim() ?? '';
        sbCoordinator.register({
            widgetId,
            template: liquid,
            resolve: (html) => {
                if (html)
                    this.outerHTML = html;
            }
        });
    }
}
