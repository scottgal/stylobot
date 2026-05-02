import type { Request, Response, NextFunction, RequestHandler } from 'express'
import { parseStyloBotHeaders, type Verdict } from '@stylobot/core'

export interface SbVerdictInjectorOptions {
  mode: 'gateway' | 'sidecar'
  endpoint?: string
  apiKey?: string
  timeout?: number
}

export function sbVerdictInjector(options: SbVerdictInjectorOptions): RequestHandler {
  if (options.mode === 'sidecar') {
    if (!options.endpoint) throw new Error('endpoint is required for sidecar mode')
    const base = options.endpoint.replace(/\/$/, '')
    const { apiKey, timeout = 3000 } = options

    return async (_req: Request, res: Response, next: NextFunction) => {
      let verdict: Verdict | null = null
      try {
        const controller = new AbortController()
        const timer = setTimeout(() => controller.abort(), timeout)
        const headers: Record<string, string> = { accept: 'application/json' }
        if (apiKey) headers['x-sb-api-key'] = apiKey
        const r = await fetch(`${base}/_stylobot/me`, { headers, signal: controller.signal })
        clearTimeout(timer)
        if (r.ok) verdict = (await r.json()) as Verdict
      } catch { /* fail open */ }
      res.locals.sbVerdict = verdict
      res.locals.sbVerdictScript = buildVerdictScript(verdict)
      next()
    }
  }

  return (req: Request, res: Response, next: NextFunction) => {
    const verdict = parseStyloBotHeaders(req.headers as Record<string, string>)
    res.locals.sbVerdict = verdict
    res.locals.sbVerdictScript = buildVerdictScript(verdict)
    next()
  }
}

function buildVerdictScript(verdict: Verdict | null): string {
  const data = verdict ?? {
    isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
    riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None'
  }
  return `<script>window.__sb=${JSON.stringify(data)}</script>`
}
