export interface WidgetRegistration {
    widgetId: string;
    template: string;
    resolve: (html: string) => void;
}
declare class SbElementsCoordinator {
    private endpoint;
    private pending;
    private scheduled;
    configure(endpoint: string): void;
    register(reg: WidgetRegistration): void;
    private flush;
}
export declare const sbCoordinator: SbElementsCoordinator;
export {};
