const RISK_ORDER: Record<string, number> = {
  Unknown: 0, VeryLow: 1, Low: 2, Elevated: 3, Medium: 4, High: 5, VeryHigh: 6, Verified: 7
}

function riskLevel(band: string): number {
  const key = Object.keys(RISK_ORDER)
    .find(k => k.toLowerCase() === band.toLowerCase())
  return key !== undefined ? RISK_ORDER[key] : 0
}

export class SbGate extends HTMLElement {
  connectedCallback() {
    this.evaluate()
    window.addEventListener('sb:verdict', () => this.evaluate())
  }

  private evaluate() {
    const maxRisk = this.getAttribute('max-risk') ?? 'low'
    const verdict = (window as any).__sb
    if (!verdict) return
    this.style.display =
      riskLevel(verdict.riskBand ?? 'Unknown') <= riskLevel(maxRisk) ? '' : 'none'
  }
}
