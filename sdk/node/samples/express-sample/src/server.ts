import express from 'express'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { StyloBotClient, type Verdict } from '@stylobot/core'
import { SbSsrCoordinator, styloBotMiddleware } from '@stylobot/node'

const dir = dirname(fileURLToPath(import.meta.url))
const STYLOBOT_URL = process.env.STYLOBOT_URL ?? 'http://localhost:5080'
const STYLOBOT_API_KEY = process.env.STYLOBOT_API_KEY
const PORT = Number(process.env.PORT ?? 3000)

const app = express()
const client = new StyloBotClient({ endpoint: STYLOBOT_URL, apiKey: STYLOBOT_API_KEY })
const coordinator = new SbSsrCoordinator(client)

const summaryTemplate = readFileSync(join(dir, 'templates/summary.liquid'), 'utf8')
const topbotsTemplate = readFileSync(join(dir, 'templates/topbots.liquid'), 'utf8')

app.use(express.static(join(dir, '../public')))

// Sidecar mode: calls POST /api/v1/detect per request, populates req.stylobot
app.use(styloBotMiddleware({ mode: 'api', endpoint: STYLOBOT_URL, apiKey: STYLOBOT_API_KEY }))

const layout = (body: string, verdictScript: string, verdict: Verdict) => {
  const { isBot, botProbability, riskBand } = verdict
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>StyloBot Sidecar Demo</title>
  <style>
    body { font-family: sans-serif; max-width: 960px; margin: 2rem auto; padding: 0 1rem; }
    .sb-card { border: 1px solid #ddd; border-radius: 8px; padding: 1rem; margin: 1rem 0; }
    .verdict-badge { padding: 0.75rem 1.25rem; border-radius: 6px; margin-bottom: 1rem;
      background: ${isBot ? '#fee' : '#efe'}; border: 1px solid ${isBot ? '#c33' : '#3a3'}; }
    nav a { margin-right: 1.25rem; font-weight: 500; }
    .alert { color: #c00; font-weight: bold; }
  </style>
</head>
<body>
  ${verdictScript}
  <nav>
    <a href="/">Home</a>
    <a href="/protected">Protected</a>
    <a href="/debug">Debug</a>
    <a href="/api/verdict">API</a>
    <a href="/csr.html">CSR demo</a>
  </nav>
  <div class="verdict-badge"
       data-isbot="${isBot}"
       data-risk="${riskBand}"
       data-prob="${botProbability.toFixed(4)}">
    <strong>You are: ${isBot ? 'a bot' : 'human'}</strong>
    &middot; probability: ${(botProbability * 100).toFixed(1)}%
    &middot; risk: ${riskBand}
  </div>
  ${body}
</body>
</html>`
}

app.get('/', async (req, res) => {
  const { verdict, reasons, meta } = req.stylobot

  let widgets: Record<string, string> = {}
  try {
    widgets = await coordinator.renderWidgets([
      { widgetId: 'summary', template: summaryTemplate },
      { widgetId: 'topbots', template: topbotsTemplate },
    ])
  } catch {
    // /_stylobot/partials/render unavailable; fallback widgets rendered below
  }

  const reasonsHtml = reasons.length
    ? `<div class="sb-card"><h3>Detection reasons</h3><ul id="reasons-list">
        ${reasons.map(r => `<li><strong>${r.detector}</strong>: ${r.detail} (impact: ${r.impact.toFixed(3)})</li>`).join('')}
       </ul></div>`
    : ''

  const metaHtml = meta
    ? `<p id="meta-info">${meta.detectorsRun} detectors &middot; ${meta.processingTimeMs.toFixed(2)} ms &middot; policy: ${meta.policyName}</p>`
    : ''

  const body = `
    <h1>StyloBot Sidecar Demo</h1>
    ${metaHtml}
    ${widgets['summary'] ?? `<div class="sb-card" data-sb-widget="summary"><em>Widget unavailable (is the dashboard UI running at ${STYLOBOT_URL}?)</em></div>`}
    ${widgets['topbots'] ?? '<div class="sb-card" data-sb-widget="topbots"><em>Widget unavailable</em></div>'}
    ${reasonsHtml}`

  res.send(layout(body, client.verdictGlobal(verdict), verdict))
})

app.get('/protected', (req, res) => {
  const { verdict } = req.stylobot

  if (verdict.isBot && verdict.botProbability > 0.8) {
    res.status(403).send(layout(
      `<h1 id="blocked-heading">Access denied</h1>
       <p>Bot detected (probability: ${(verdict.botProbability * 100).toFixed(1)}%, risk: ${verdict.riskBand})</p>`,
      client.verdictGlobal(verdict),
      verdict,
    ))
    return
  }

  const body = `
    <h1 id="protected-heading">Protected Content</h1>
    <div class="sb-card">
      <p>You passed the bot check.</p>
      <p>Probability: ${(verdict.botProbability * 100).toFixed(1)}% &middot; Risk: ${verdict.riskBand} &middot; Action: ${verdict.recommendedAction}</p>
    </div>`

  res.send(layout(body, client.verdictGlobal(verdict), verdict))
})

app.get('/debug', (req, res) => {
  res.json(req.stylobot)
})

app.get('/api/verdict', (req, res) => {
  res.json(req.stylobot.verdict)
})

app.listen(PORT, () => {
  console.log(`StyloBot sidecar sample: http://localhost:${PORT}`)
  console.log(`StyloBot URL: ${STYLOBOT_URL}`)
})
