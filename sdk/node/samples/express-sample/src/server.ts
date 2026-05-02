import express from 'express'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { StyloBotClient } from '@stylobot/core'
import { SbSsrCoordinator, sbVerdictInjector, styloBotMiddleware } from '@stylobot/node'

const dir = dirname(fileURLToPath(import.meta.url))
const STYLOBOT_URL = process.env.STYLOBOT_URL ?? 'http://localhost:5080'

const app = express()
const client = new StyloBotClient({ endpoint: STYLOBOT_URL })
const coordinator = new SbSsrCoordinator(client)

app.use(express.static(join(dir, '../public')))
app.use(styloBotMiddleware({ mode: 'headers' }))
app.use(sbVerdictInjector({ mode: 'gateway' }))

app.get('/', async (req, res) => {
  const summaryTemplate = readFileSync(join(dir, 'templates/summary.liquid'), 'utf8')
  const topbotsTemplate = readFileSync(join(dir, 'templates/topbots.liquid'), 'utf8')

  const widgets = await coordinator.renderWidgets([
    { widgetId: 'summary', template: summaryTemplate },
    { widgetId: 'topbots', template: topbotsTemplate },
  ])

  const { isBot, verdict } = req.stylobot
  const verdictScript = res.locals.sbVerdictScript as string

  res.send(`<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>StyloBot SSR Demo</title>
  <style>
    body { font-family: sans-serif; max-width: 900px; margin: 2rem auto; padding: 0 1rem; }
    .sb-card { border: 1px solid #ddd; border-radius: 8px; padding: 1rem; margin: 1rem 0; }
    .alert { color: red; font-weight: bold; }
    .verdict { background: ${isBot ? '#fee' : '#efe'}; padding: 0.5rem 1rem; border-radius: 4px; margin-bottom: 1rem; }
  </style>
</head>
<body>
  ${verdictScript}
  <div class="verdict">
    You are: <strong>${isBot ? 'a bot' : 'human'}</strong> - risk: ${verdict.riskBand}
  </div>
  <h1>SSR Widgets (Liquid rendered server-side)</h1>
  ${widgets['summary'] ?? '<p>Summary unavailable</p>'}
  ${widgets['topbots'] ?? '<p>Top bots unavailable</p>'}
  <p><a href="/csr.html">View CSR demo (web components)</a></p>
</body>
</html>`)
})

app.listen(process.env.PORT ?? 3000, () => {
  console.log(`StyloBot sample: http://localhost:${process.env.PORT ?? 3000}`)
  console.log(`StyloBot URL: ${STYLOBOT_URL}`)
})
