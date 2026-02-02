import type { DetectorContribution } from '../types';

interface DetectorBreakdownProps {
  contributions: DetectorContribution[];
  maxItems?: number;
}

export function DetectorBreakdown({ contributions, maxItems = 5 }: DetectorBreakdownProps) {
  if (!contributions?.length) return null;

  const sorted = [...contributions]
    .sort((a, b) => Math.abs(b.weightedScore) - Math.abs(a.weightedScore))
    .slice(0, maxItems);

  const maxScore = Math.max(...sorted.map(c => Math.abs(c.weightedScore)), 0.01);

  return (
    <div className="sb-detectors">
      <div className="sb-detectors-title">Top Detectors</div>
      {sorted.map((c) => {
        const pct = (Math.abs(c.weightedScore) / maxScore) * 100;
        const isPositive = c.weightedScore >= 0;
        return (
          <div key={c.name} className="sb-detector-row">
            <span className="sb-detector-name">{c.name.replace(/Contributor$/, '')}</span>
            <div className="sb-detector-bar-track">
              <div
                className={`sb-detector-bar ${isPositive ? 'sb-bar-bot' : 'sb-bar-human'}`}
                style={{ width: `${pct}%` }}
              />
            </div>
            <span className="sb-detector-score">
              {c.weightedScore >= 0 ? '+' : ''}{c.weightedScore.toFixed(2)}
            </span>
          </div>
        );
      })}
    </div>
  );
}
