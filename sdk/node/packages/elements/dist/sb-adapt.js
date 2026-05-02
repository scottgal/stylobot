const RISK_ORDER = {
    Unknown: 0, VeryLow: 1, Low: 2, Elevated: 3, Medium: 4, High: 5, VeryHigh: 6, Verified: 7
};
function riskLevel(band) {
    const key = Object.keys(RISK_ORDER)
        .find(k => k.toLowerCase() === band.toLowerCase());
    return key !== undefined ? RISK_ORDER[key] : 0;
}
export class SbCase extends HTMLElement {
}
export class SbAdapt extends HTMLElement {
    connectedCallback() {
        this.evaluate();
        window.addEventListener('sb:verdict', () => this.evaluate());
    }
    evaluate() {
        const verdict = window.__sb;
        const current = verdict ? riskLevel(verdict.riskBand ?? 'Unknown') : 0;
        let matched = false;
        for (const child of Array.from(this.children)) {
            if (!(child instanceof SbCase))
                continue;
            const el = child;
            const maxRisk = child.getAttribute('max-risk');
            const fits = maxRisk === null || current <= riskLevel(maxRisk);
            el.style.display = (!matched && fits) ? '' : 'none';
            if (!matched && fits)
                matched = true;
        }
    }
}
