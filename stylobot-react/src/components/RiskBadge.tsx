import type { RiskBand } from '../types';

interface RiskBadgeProps {
  riskBand: RiskBand | string;
}

const riskLabels: Record<string, string> = {
  VeryLow: 'Very Low',
  Low: 'Low',
  Medium: 'Medium',
  High: 'High',
  VeryHigh: 'Very High',
};

export function RiskBadge({ riskBand }: RiskBadgeProps) {
  const className = `sb-risk-badge sb-risk-${(riskBand || '').toLowerCase()}`;
  return (
    <span className={className}>
      {riskLabels[riskBand] || riskBand || 'Unknown'}
    </span>
  );
}
