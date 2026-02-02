
interface ProbabilityGaugeProps {
  value: number; // 0-1
  size?: number;
  label?: string;
}

export function ProbabilityGauge({ value, size = 140, label }: ProbabilityGaugeProps) {
  const radius = (size - 16) / 2;
  const cx = size / 2;
  const cy = size / 2 + 10;
  // Semi-circle arc (180 degrees)
  const circumference = Math.PI * radius;
  const dashOffset = circumference * (1 - Math.max(0, Math.min(1, value)));
  const pct = Math.round(value * 100);

  // Color based on value
  const color = value >= 0.7 ? 'var(--sb-danger)' : value >= 0.4 ? 'var(--sb-warning)' : 'var(--sb-success)';

  return (
    <div className="sb-gauge">
      <svg width={size} height={size * 0.65} viewBox={`0 0 ${size} ${size * 0.65}`}>
        {/* Background arc */}
        <path
          d={`M ${cx - radius} ${cy} A ${radius} ${radius} 0 0 1 ${cx + radius} ${cy}`}
          fill="none"
          stroke="var(--sb-border)"
          strokeWidth="10"
          strokeLinecap="round"
        />
        {/* Value arc */}
        <path
          d={`M ${cx - radius} ${cy} A ${radius} ${radius} 0 0 1 ${cx + radius} ${cy}`}
          fill="none"
          stroke={color}
          strokeWidth="10"
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={dashOffset}
          style={{ transition: 'stroke-dashoffset 0.8s ease' }}
        />
      </svg>
      <div className="sb-gauge-value" style={{ color }}>
        {pct}%
      </div>
      {label && <div className="sb-gauge-label">{label}</div>}
    </div>
  );
}
