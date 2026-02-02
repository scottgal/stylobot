// Components
export { StylobotWidget } from './components/DetectionWidget';
export { ProbabilityGauge } from './components/ProbabilityGauge';
export { RiskBadge } from './components/RiskBadge';
export { DetectorBreakdown } from './components/DetectorBreakdown';
export { LiveIndicator } from './components/LiveIndicator';

// Hooks
export { useDetection } from './hooks/useDetection';
export { useSignalR } from './hooks/useSignalR';

// Types
export type {
  DetectionResult,
  DetectorContribution,
  RiskBand,
  Theme,
  StylobotWidgetProps,
} from './types';
