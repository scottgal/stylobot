import { defineConfig, devices } from '@playwright/test'

const PORT = 3001
const STYLOBOT_URL = process.env.STYLOBOT_URL ?? 'http://localhost:5080'

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: 'list',
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'on-first-retry',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  webServer: {
    command: `node --experimental-strip-types src/server.ts`,
    port: PORT,
    reuseExistingServer: true,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      PORT: String(PORT),
      STYLOBOT_URL,
      STYLOBOT_API_KEY: process.env.STYLOBOT_API_KEY ?? 'SB-K6-FULL-DETECTION',
    },
  },
})
