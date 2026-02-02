import { jsxs as n, jsx as s } from "react/jsx-runtime";
import { useState as u, useCallback as M, useEffect as C } from "react";
function f(e) {
  return Array.isArray(e) ? e.map(f) : e !== null && typeof e == "object" ? Object.fromEntries(
    Object.entries(e).map(([a, c]) => [
      a.charAt(0).toLowerCase() + a.slice(1),
      f(c)
    ])
  ) : e;
}
function B({ apiUrl: e, onDetection: a }) {
  const [c, o] = u(null), [t, i] = u(!0), [b, h] = u(null), d = M(async () => {
    i(!0), h(null);
    try {
      const l = await fetch(e, {
        credentials: "include",
        headers: { Accept: "application/json" }
      });
      if (!l.ok) throw new Error(`HTTP ${l.status}`);
      const m = await l.json(), p = f(m);
      o(p), a == null || a(p);
    } catch (l) {
      h(l instanceof Error ? l.message : "Failed to fetch detection");
    } finally {
      i(!1);
    }
  }, [e, a]);
  return C(() => {
    d();
  }, [d]), { data: c, loading: t, error: b, refetch: d };
}
function g(e) {
  return Array.isArray(e) ? e.map(g) : e !== null && typeof e == "object" ? Object.fromEntries(
    Object.entries(e).map(([a, c]) => [
      a.charAt(0).toLowerCase() + a.slice(1),
      g(c)
    ])
  ) : e;
}
function A({ hubUrl: e, signature: a, onUpdate: c }) {
  const [o, t] = u(!1), [i, b] = u(null);
  return C(() => {
    const h = window.signalR;
    if (!(h != null && h.HubConnectionBuilder)) {
      b("SignalR not loaded. Include @microsoft/signalr via CDN or npm.");
      return;
    }
    const d = new h.HubConnectionBuilder().withUrl(e).withAutomaticReconnect().build();
    return d.on("BroadcastDetection", (l) => {
      const m = g(l);
      a && m.primarySignature !== a || c == null || c(m);
    }), d.onclose(() => t(!1)), d.onreconnecting(() => t(!1)), d.onreconnected(() => t(!0)), d.start().then(() => t(!0)).catch((l) => b(l.message)), () => {
      d.stop();
    };
  }, [e, a, c]), { connected: o, error: i };
}
function L({ value: e, size: a = 140, label: c }) {
  const o = (a - 16) / 2, t = a / 2, i = a / 2 + 10, b = Math.PI * o, h = b * (1 - Math.max(0, Math.min(1, e))), d = Math.round(e * 100), l = e >= 0.7 ? "var(--sb-danger)" : e >= 0.4 ? "var(--sb-warning)" : "var(--sb-success)";
  return /* @__PURE__ */ n("div", { className: "sb-gauge", children: [
    /* @__PURE__ */ n("svg", { width: a, height: a * 0.65, viewBox: `0 0 ${a} ${a * 0.65}`, children: [
      /* @__PURE__ */ s(
        "path",
        {
          d: `M ${t - o} ${i} A ${o} ${o} 0 0 1 ${t + o} ${i}`,
          fill: "none",
          stroke: "var(--sb-border)",
          strokeWidth: "10",
          strokeLinecap: "round"
        }
      ),
      /* @__PURE__ */ s(
        "path",
        {
          d: `M ${t - o} ${i} A ${o} ${o} 0 0 1 ${t + o} ${i}`,
          fill: "none",
          stroke: l,
          strokeWidth: "10",
          strokeLinecap: "round",
          strokeDasharray: b,
          strokeDashoffset: h,
          style: { transition: "stroke-dashoffset 0.8s ease" }
        }
      )
    ] }),
    /* @__PURE__ */ n("div", { className: "sb-gauge-value", style: { color: l }, children: [
      d,
      "%"
    ] }),
    c && /* @__PURE__ */ s("div", { className: "sb-gauge-label", children: c })
  ] });
}
const S = {
  VeryLow: "Very Low",
  Low: "Low",
  Medium: "Medium",
  High: "High",
  VeryHigh: "Very High"
};
function y({ riskBand: e }) {
  const a = `sb-risk-badge sb-risk-${(e || "").toLowerCase()}`;
  return /* @__PURE__ */ s("span", { className: a, children: S[e] || e || "Unknown" });
}
function H({ contributions: e, maxItems: a = 5 }) {
  if (!(e != null && e.length)) return null;
  const c = [...e].sort((t, i) => Math.abs(i.weightedScore) - Math.abs(t.weightedScore)).slice(0, a), o = Math.max(...c.map((t) => Math.abs(t.weightedScore)), 0.01);
  return /* @__PURE__ */ n("div", { className: "sb-detectors", children: [
    /* @__PURE__ */ s("div", { className: "sb-detectors-title", children: "Top Detectors" }),
    c.map((t) => {
      const i = Math.abs(t.weightedScore) / o * 100, b = t.weightedScore >= 0;
      return /* @__PURE__ */ n("div", { className: "sb-detector-row", children: [
        /* @__PURE__ */ s("span", { className: "sb-detector-name", children: t.name.replace(/Contributor$/, "") }),
        /* @__PURE__ */ s("div", { className: "sb-detector-bar-track", children: /* @__PURE__ */ s(
          "div",
          {
            className: `sb-detector-bar ${b ? "sb-bar-bot" : "sb-bar-human"}`,
            style: { width: `${i}%` }
          }
        ) }),
        /* @__PURE__ */ n("span", { className: "sb-detector-score", children: [
          t.weightedScore >= 0 ? "+" : "",
          t.weightedScore.toFixed(2)
        ] })
      ] }, t.name);
    })
  ] });
}
function k({ connected: e }) {
  return /* @__PURE__ */ n("span", { className: `sb-live ${e ? "sb-live-active" : ""}`, children: [
    /* @__PURE__ */ s("span", { className: "sb-live-dot" }),
    e ? "LIVE" : "OFFLINE"
  ] });
}
function O({
  apiUrl: e,
  hubUrl: a,
  theme: c = "dark",
  compact: o = !1,
  onDetection: t,
  className: i
}) {
  var w;
  const [b, h] = u(null), d = M(
    ($) => {
      h($), t == null || t($);
    },
    [t]
  ), { data: l, loading: m, error: p } = B({ apiUrl: e, onDetection: t }), { connected: v } = a ? A({ hubUrl: a, signature: l == null ? void 0 : l.primarySignature, onUpdate: d }) : { connected: !1 }, r = b || l, N = c === "auto" ? "" : `sb-theme-${c}`;
  return m && !r ? /* @__PURE__ */ s("div", { className: `sb-widget ${N} ${i || ""}`, children: /* @__PURE__ */ n("div", { className: "sb-loading", children: [
    /* @__PURE__ */ s("div", { className: "sb-spinner" }),
    /* @__PURE__ */ s("span", { children: "Analyzing..." })
  ] }) }) : p && !r ? /* @__PURE__ */ s("div", { className: `sb-widget ${N} ${i || ""}`, children: /* @__PURE__ */ s("div", { className: "sb-error", children: "Detection unavailable" }) }) : r ? o ? /* @__PURE__ */ s("div", { className: `sb-widget sb-compact ${N} ${i || ""}`, children: /* @__PURE__ */ n("div", { className: "sb-compact-row", children: [
    /* @__PURE__ */ s("span", { className: `sb-type-badge ${r.isBot ? "sb-is-bot" : "sb-is-human"}`, children: r.isBot ? "BOT" : "HUMAN" }),
    /* @__PURE__ */ n("span", { className: "sb-compact-prob", children: [
      Math.round(r.botProbability * 100),
      "%"
    ] }),
    /* @__PURE__ */ s(y, { riskBand: r.riskBand }),
    r.botName && /* @__PURE__ */ s("span", { className: "sb-compact-name", children: r.botName }),
    /* @__PURE__ */ n("span", { className: "sb-compact-time", children: [
      r.processingTimeMs,
      "ms"
    ] }),
    a && /* @__PURE__ */ s(k, { connected: v })
  ] }) }) : /* @__PURE__ */ n("div", { className: `sb-widget ${N} ${i || ""}`, children: [
    /* @__PURE__ */ n("div", { className: "sb-header", children: [
      /* @__PURE__ */ s("span", { className: `sb-type-badge ${r.isBot ? "sb-is-bot" : "sb-is-human"}`, children: r.isBot ? "BOT" : "HUMAN" }),
      /* @__PURE__ */ s(y, { riskBand: r.riskBand }),
      a && /* @__PURE__ */ s(k, { connected: v })
    ] }),
    /* @__PURE__ */ n("div", { className: "sb-body", children: [
      /* @__PURE__ */ s("div", { className: "sb-gauge-section", children: /* @__PURE__ */ s(L, { value: r.botProbability, label: "Bot Probability" }) }),
      /* @__PURE__ */ n("div", { className: "sb-stats", children: [
        /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Confidence" }),
          /* @__PURE__ */ n("span", { className: "sb-stat-value", children: [
            Math.round(r.confidence * 100),
            "%"
          ] })
        ] }),
        /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Detectors" }),
          /* @__PURE__ */ s("span", { className: "sb-stat-value", children: r.detectorsRan })
        ] }),
        /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Processing" }),
          /* @__PURE__ */ n("span", { className: "sb-stat-value", children: [
            r.processingTimeMs,
            "ms"
          ] })
        ] }),
        /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Action" }),
          /* @__PURE__ */ s("span", { className: "sb-stat-value", children: r.recommendedAction })
        ] }),
        r.hitCount > 0 && /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Hits" }),
          /* @__PURE__ */ s("span", { className: "sb-stat-value", children: r.hitCount })
        ] }),
        r.botName && /* @__PURE__ */ n("div", { className: "sb-stat", children: [
          /* @__PURE__ */ s("span", { className: "sb-stat-label", children: "Bot Name" }),
          /* @__PURE__ */ s("span", { className: "sb-stat-value", children: r.botName })
        ] })
      ] })
    ] }),
    ((w = r.detectorContributions) == null ? void 0 : w.length) > 0 && /* @__PURE__ */ s(H, { contributions: r.detectorContributions }),
    /* @__PURE__ */ s("div", { className: "sb-footer", children: /* @__PURE__ */ s("span", { className: "sb-brand", children: "stylobot" }) })
  ] }) : null;
}
export {
  H as DetectorBreakdown,
  k as LiveIndicator,
  L as ProbabilityGauge,
  y as RiskBadge,
  O as StylobotWidget,
  B as useDetection,
  A as useSignalR
};
