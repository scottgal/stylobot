// Enums as string unions matching .NET
export type BotType = 'Unknown' | 'SearchEngine' | 'SocialMediaBot' | 'MonitoringBot' | 'Scraper' | 'MaliciousBot' | 'GoodBot' | 'VerifiedBot' | 'AiBot' | 'Tool' | 'ExploitScanner';
export type RiskBand = 'Unknown' | 'VeryLow' | 'Low' | 'Elevated' | 'Medium' | 'High' | 'VeryHigh' | 'Verified';
export type RecommendedAction = 'Allow' | 'Throttle' | 'Challenge' | 'Block';
export type ThreatBand = 'None' | 'Low' | 'Elevated' | 'High' | 'Critical';

// Detection request/response
export interface TlsInfo { version?: string; cipher?: string; ja3?: string; ja4?: string; }

export interface DetectRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  remoteIp: string;
  protocol?: string;
  tls?: TlsInfo;
}

export interface Verdict {
  isBot: boolean;
  botProbability: number;
  confidence: number;
  botType: BotType | null;
  botName: string | null;
  riskBand: RiskBand;
  recommendedAction: RecommendedAction;
  threatScore: number;
  threatBand: ThreatBand;
}

export interface DetectionReason { detector: string; detail: string; impact: number; }
export interface DetectionMeta { processingTimeMs: number; detectorsRun: number; policyName: string | null; aiRan: boolean; requestId?: string; }
export interface DetectResponse { verdict: Verdict; reasons: DetectionReason[]; signals: Record<string, unknown>; meta: DetectionMeta; }

// Pagination
export interface PaginationInfo { offset: number; limit: number; total: number; }
export interface ResponseMeta { generatedAt: string; }
export interface PaginatedResponse<T> { data: T[]; pagination: PaginationInfo; meta: ResponseMeta; }
export interface SingleResponse<T> { data: T; meta: ResponseMeta; }

// Dashboard types
export interface Summary { totalRequests: number; botRequests: number; humanRequests: number; uncertainRequests: number; riskBandCounts: Record<string, number>; topBotTypes: Record<string, number>; uniqueSignatures: number; }
export interface TimeseriesPoint { timestamp: string; total: number; bots: number; humans: number; }
export interface CountryStat { countryCode: string; total: number; botCount: number; botRate: number; }
export interface EndpointStat { method: string; path: string; total: number; botCount: number; botRate: number; }
export interface BotStat { signatureId: string; botName: string | null; botType: string | null; hitCount: number; lastSeen: string; }
export interface Threat { timestamp: string; path: string; threatType: string; threatScore: number; signatureId: string; }
export interface ApiKeyInfo { keyName: string; disabledDetectors: string[]; weightOverrides: Record<string, number>; detectionPolicyName: string | null; actionPolicyName: string | null; tags: string[]; disablesAllDetectors: boolean; }

// Client options
export interface StyloBotClientOptions { endpoint: string; apiKey?: string; bearerToken?: string; timeout?: number; retries?: number; }

// Query params
export interface PaginationQuery { limit?: number; offset?: number; }
export interface DetectionsQuery extends PaginationQuery { isBot?: boolean; since?: string; }
export interface SignaturesQuery extends PaginationQuery { isBot?: boolean; }
export interface SummaryQuery { period?: '1h' | '24h' | '7d'; }
export interface TimeseriesQuery { interval?: '1m' | '5m' | '15m' | '1h'; since?: string; until?: string; }
export interface ThreatsQuery extends PaginationQuery { severity?: string; since?: string; until?: string; }

// Widget rendering
export type ClientMode = 'gateway' | 'sidecar' | 'ssr'

export interface WidgetTemplate {
  widgetId: string
  template?: string
  params?: Record<string, string>
}

export interface WidgetRenderRequest {
  widgets: Record<string, string>
}
