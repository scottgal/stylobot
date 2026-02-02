import { useState, useEffect } from 'react';
import type { DetectionResult } from '../types';

/** Normalize PascalCase API keys to camelCase */
function toCamel(obj: unknown): unknown {
  if (Array.isArray(obj)) return obj.map(toCamel);
  if (obj !== null && typeof obj === 'object') {
    return Object.fromEntries(
      Object.entries(obj as Record<string, unknown>).map(([k, v]) => [
        k.charAt(0).toLowerCase() + k.slice(1),
        toCamel(v),
      ])
    );
  }
  return obj;
}

interface UseSignalROptions {
  hubUrl: string;
  signature?: string;
  onUpdate?: (result: DetectionResult) => void;
}

interface UseSignalRReturn {
  connected: boolean;
  error: string | null;
}

/**
 * Hook for connecting to the Stylobot SignalR hub for live detection updates.
 * Requires @microsoft/signalr to be loaded globally or available via CDN.
 */
export function useSignalR({ hubUrl, signature, onUpdate }: UseSignalROptions): UseSignalRReturn {
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    // Check if signalR is available globally
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const signalR = (window as unknown as Record<string, any>).signalR;

    if (!signalR?.HubConnectionBuilder) {
      setError('SignalR not loaded. Include @microsoft/signalr via CDN or npm.');
      return;
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build() as {
        on: (event: string, handler: (data: unknown) => void) => void;
        onclose: (handler: () => void) => void;
        onreconnecting: (handler: () => void) => void;
        onreconnected: (handler: () => void) => void;
        start: () => Promise<void>;
        stop: () => Promise<void>;
      };

    connection.on('BroadcastDetection', (raw: unknown) => {
      const detection = toCamel(raw) as DetectionResult;
      if (signature && detection.primarySignature !== signature) return;
      onUpdate?.(detection);
    });

    connection.onclose(() => setConnected(false));
    connection.onreconnecting(() => setConnected(false));
    connection.onreconnected(() => setConnected(true));

    connection.start()
      .then(() => setConnected(true))
      .catch((e: Error) => setError(e.message));

    return () => {
      connection.stop();
    };
  }, [hubUrl, signature, onUpdate]);

  return { connected, error };
}
