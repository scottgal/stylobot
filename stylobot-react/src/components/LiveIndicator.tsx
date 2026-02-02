
interface LiveIndicatorProps {
  connected: boolean;
}

export function LiveIndicator({ connected }: LiveIndicatorProps) {
  return (
    <span className={`sb-live ${connected ? 'sb-live-active' : ''}`}>
      <span className="sb-live-dot" />
      {connected ? 'LIVE' : 'OFFLINE'}
    </span>
  );
}
