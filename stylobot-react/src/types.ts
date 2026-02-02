/** Detection result from the Stylobot API */
export interface DetectionResult {
  isBot: boolean;
  botProbability: number;
  humanProbability: number;
  confidence: number;
  riskBand: RiskBand;
  botType: string | null;
  botName: string | null;
  recommendedAction: string;
  actionReason: string | null;
  policyName: string | null;
  processingTimeMs: number;
  detectorsRan: number;
  aiRan: boolean;
  hitCount: number;
  detectorContributions: DetectorContribution[];
  signals: Record<string, string | number | boolean>;
  primarySignature: string;
}

export interface DetectorContribution {
  name: string;
  weight: number;
  rawScore: number;
  weightedScore: number;
  confidence: number;
  signals: Record<string, string | number | boolean>;
}

export type RiskBand = 'VeryLow' | 'Low' | 'Medium' | 'High' | 'VeryHigh';

export type Theme = 'dark' | 'light' | 'auto';

export interface StylobotWidgetProps {
  /** URL for the detection check API endpoint */
  apiUrl: string;
  /** URL for the SignalR hub (optional, enables live updates) */
  hubUrl?: string;
  /** Color theme */
  theme?: Theme;
  /** Compact mode shows minimal info */
  compact?: boolean;
  /** Callback when detection result is received */
  onDetection?: (result: DetectionResult) => void;
  /** Additional CSS class name */
  className?: string;
}
