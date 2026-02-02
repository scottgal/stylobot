import { JSX as JSX_2 } from 'react/jsx-runtime';

/** Detection result from the Stylobot API */
export declare interface DetectionResult {
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

export declare function DetectorBreakdown({ contributions, maxItems }: DetectorBreakdownProps): JSX_2.Element | null;

declare interface DetectorBreakdownProps {
    contributions: DetectorContribution[];
    maxItems?: number;
}

export declare interface DetectorContribution {
    name: string;
    weight: number;
    rawScore: number;
    weightedScore: number;
    confidence: number;
    signals: Record<string, string | number | boolean>;
}

export declare function LiveIndicator({ connected }: LiveIndicatorProps): JSX_2.Element;

declare interface LiveIndicatorProps {
    connected: boolean;
}

export declare function ProbabilityGauge({ value, size, label }: ProbabilityGaugeProps): JSX_2.Element;

declare interface ProbabilityGaugeProps {
    value: number;
    size?: number;
    label?: string;
}

export declare function RiskBadge({ riskBand }: RiskBadgeProps): JSX_2.Element;

declare interface RiskBadgeProps {
    riskBand: RiskBand | string;
}

export declare type RiskBand = 'VeryLow' | 'Low' | 'Medium' | 'High' | 'VeryHigh';

export declare function StylobotWidget({ apiUrl, hubUrl, theme, compact, onDetection, className, }: StylobotWidgetProps): JSX_2.Element | null;

export declare interface StylobotWidgetProps {
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

export declare type Theme = 'dark' | 'light' | 'auto';

export declare function useDetection({ apiUrl, onDetection }: UseDetectionOptions): UseDetectionReturn;

declare interface UseDetectionOptions {
    apiUrl: string;
    onDetection?: (result: DetectionResult) => void;
}

declare interface UseDetectionReturn {
    data: DetectionResult | null;
    loading: boolean;
    error: string | null;
    refetch: () => void;
}

/**
 * Hook for connecting to the Stylobot SignalR hub for live detection updates.
 * Requires @microsoft/signalr to be loaded globally or available via CDN.
 */
export declare function useSignalR({ hubUrl, signature, onUpdate }: UseSignalROptions): UseSignalRReturn;

declare interface UseSignalROptions {
    hubUrl: string;
    signature?: string;
    onUpdate?: (result: DetectionResult) => void;
}

declare interface UseSignalRReturn {
    connected: boolean;
    error: string | null;
}

export { }
