import { useState, useEffect, useCallback } from 'react';
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

interface UseDetectionOptions {
  apiUrl: string;
  onDetection?: (result: DetectionResult) => void;
}

interface UseDetectionReturn {
  data: DetectionResult | null;
  loading: boolean;
  error: string | null;
  refetch: () => void;
}

export function useDetection({ apiUrl, onDetection }: UseDetectionOptions): UseDetectionReturn {
  const [data, setData] = useState<DetectionResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchDetection = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(apiUrl, {
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const raw = await res.json();
      const result = toCamel(raw) as DetectionResult;
      setData(result);
      onDetection?.(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to fetch detection');
    } finally {
      setLoading(false);
    }
  }, [apiUrl, onDetection]);

  useEffect(() => {
    fetchDetection();
  }, [fetchDetection]);

  return { data, loading, error, refetch: fetchDetection };
}
