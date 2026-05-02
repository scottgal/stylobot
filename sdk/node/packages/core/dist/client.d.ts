import type { StyloBotClientOptions, DetectRequest, DetectResponse, PaginatedResponse, SingleResponse, Summary, TimeseriesPoint, CountryStat, EndpointStat, BotStat, Threat, ApiKeyInfo, DetectionsQuery, SignaturesQuery, SummaryQuery, TimeseriesQuery, PaginationQuery, ThreatsQuery, Verdict, WidgetTemplate } from './types.js';
export declare class StyloBotClient {
    private readonly endpoint;
    private readonly apiKey?;
    private readonly bearerToken?;
    private readonly timeout;
    private readonly retries;
    constructor(options: StyloBotClientOptions);
    detect(request: DetectRequest): Promise<DetectResponse>;
    detectBatch(requests: DetectRequest[]): Promise<DetectResponse[]>;
    detections(params?: DetectionsQuery): Promise<PaginatedResponse<unknown>>;
    signatures(params?: SignaturesQuery): Promise<PaginatedResponse<unknown>>;
    summary(params?: SummaryQuery): Promise<SingleResponse<Summary>>;
    timeseries(params?: TimeseriesQuery): Promise<PaginatedResponse<TimeseriesPoint>>;
    countries(params?: PaginationQuery): Promise<PaginatedResponse<CountryStat>>;
    endpoints(params?: PaginationQuery): Promise<PaginatedResponse<EndpointStat>>;
    topBots(params?: PaginationQuery): Promise<PaginatedResponse<BotStat>>;
    threats(params?: ThreatsQuery): Promise<PaginatedResponse<Threat>>;
    me(): Promise<SingleResponse<ApiKeyInfo>>;
    renderWidgets(widgets: WidgetTemplate[]): Promise<Record<string, string>>;
    verdictGlobal(verdict: Verdict | null): string;
    private postHtml;
    private headers;
    private request;
    private get;
    private post;
}
export declare class StyloBotApiError extends Error {
    readonly status: number;
    readonly body: string;
    readonly url: string;
    constructor(status: number, body: string, url: string);
}
//# sourceMappingURL=client.d.ts.map