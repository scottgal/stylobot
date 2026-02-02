import { useState, useCallback } from 'react';
import type { StylobotWidgetProps, DetectionResult } from '../types';
import { useDetection } from '../hooks/useDetection';
import { useSignalR } from '../hooks/useSignalR';
import { ProbabilityGauge } from './ProbabilityGauge';
import { RiskBadge } from './RiskBadge';
import { DetectorBreakdown } from './DetectorBreakdown';
import { LiveIndicator } from './LiveIndicator';
import '../styles/widget.css';

export function StylobotWidget({
  apiUrl,
  hubUrl,
  theme = 'dark',
  compact = false,
  onDetection,
  className,
}: StylobotWidgetProps) {
  const [liveData, setLiveData] = useState<DetectionResult | null>(null);

  const handleUpdate = useCallback(
    (result: DetectionResult) => {
      setLiveData(result);
      onDetection?.(result);
    },
    [onDetection]
  );

  const { data: initialData, loading, error } = useDetection({ apiUrl, onDetection });
  const { connected } = hubUrl
    ? useSignalR({ hubUrl, signature: initialData?.primarySignature, onUpdate: handleUpdate })
    : { connected: false };

  const data = liveData || initialData;

  const themeClass = theme === 'auto' ? '' : `sb-theme-${theme}`;

  if (loading && !data) {
    return (
      <div className={`sb-widget ${themeClass} ${className || ''}`}>
        <div className="sb-loading">
          <div className="sb-spinner" />
          <span>Analyzing...</span>
        </div>
      </div>
    );
  }

  if (error && !data) {
    return (
      <div className={`sb-widget ${themeClass} ${className || ''}`}>
        <div className="sb-error">Detection unavailable</div>
      </div>
    );
  }

  if (!data) return null;

  if (compact) {
    return (
      <div className={`sb-widget sb-compact ${themeClass} ${className || ''}`}>
        <div className="sb-compact-row">
          <span className={`sb-type-badge ${data.isBot ? 'sb-is-bot' : 'sb-is-human'}`}>
            {data.isBot ? 'BOT' : 'HUMAN'}
          </span>
          <span className="sb-compact-prob">
            {Math.round(data.botProbability * 100)}%
          </span>
          <RiskBadge riskBand={data.riskBand} />
          {data.botName && <span className="sb-compact-name">{data.botName}</span>}
          <span className="sb-compact-time">{data.processingTimeMs}ms</span>
          {hubUrl && <LiveIndicator connected={connected} />}
        </div>
      </div>
    );
  }

  return (
    <div className={`sb-widget ${themeClass} ${className || ''}`}>
      <div className="sb-header">
        <span className={`sb-type-badge ${data.isBot ? 'sb-is-bot' : 'sb-is-human'}`}>
          {data.isBot ? 'BOT' : 'HUMAN'}
        </span>
        <RiskBadge riskBand={data.riskBand} />
        {hubUrl && <LiveIndicator connected={connected} />}
      </div>

      <div className="sb-body">
        <div className="sb-gauge-section">
          <ProbabilityGauge value={data.botProbability} label="Bot Probability" />
        </div>

        <div className="sb-stats">
          <div className="sb-stat">
            <span className="sb-stat-label">Confidence</span>
            <span className="sb-stat-value">{Math.round(data.confidence * 100)}%</span>
          </div>
          <div className="sb-stat">
            <span className="sb-stat-label">Detectors</span>
            <span className="sb-stat-value">{data.detectorsRan}</span>
          </div>
          <div className="sb-stat">
            <span className="sb-stat-label">Processing</span>
            <span className="sb-stat-value">{data.processingTimeMs}ms</span>
          </div>
          <div className="sb-stat">
            <span className="sb-stat-label">Action</span>
            <span className="sb-stat-value">{data.recommendedAction}</span>
          </div>
          {data.hitCount > 0 && (
            <div className="sb-stat">
              <span className="sb-stat-label">Hits</span>
              <span className="sb-stat-value">{data.hitCount}</span>
            </div>
          )}
          {data.botName && (
            <div className="sb-stat">
              <span className="sb-stat-label">Bot Name</span>
              <span className="sb-stat-value">{data.botName}</span>
            </div>
          )}
        </div>
      </div>

      {data.detectorContributions?.length > 0 && (
        <DetectorBreakdown contributions={data.detectorContributions} />
      )}

      <div className="sb-footer">
        <span className="sb-brand">stylobot</span>
      </div>
    </div>
  );
}
