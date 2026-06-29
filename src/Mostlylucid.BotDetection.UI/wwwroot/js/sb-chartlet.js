(function () {
  'use strict';

  // Resolve a CSS custom-property token (e.g. `--sb-color-bot`) against the
  // active theme. Done at runtime so dark-mode + per-tier theme overrides are
  // picked up without rebuilding the chart on theme change.
  function colorFor(token) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(token).trim();
    return v || '#888';
  }

  function buildDatasets(model) {
    return model.series.map(function (s) {
      return {
        label: s.label,
        data: s.buckets,
        backgroundColor: colorFor(s.colorToken),
        borderColor: colorFor(s.colorToken),
        borderWidth: 0,
        stack: 'series',
        // Stash the series key on the dataset so the onClick handler can
        // route a bar click back to its filter without re-walking the model.
        _seriesKey: s.key
      };
    });
  }

  function formatY(v, fmt) {
    if (fmt === 'percent') return Math.round(v) + '%';
    if (fmt === 'ms') return v + ' ms';
    if (fmt === 'bytes') return (v / 1024).toFixed(1) + ' KB';
    return Number(v).toLocaleString();
  }

  function chartType(kind) {
    switch (kind) {
      case 'StackedBar': return 'bar';
      case 'StackedArea': return 'line';
      case 'HorizontalBar': return 'bar';
      case 'Donut': return 'doughnut';
      case 'Line': return 'line';
      default: return 'bar';
    }
  }

  function buildOptions(model, onBarClick) {
    const stacked = model.kind === 'StackedBar' || model.kind === 'StackedArea';
    return {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      plugins: {
        // Alpine renders the legend in Razor — Chart.js' built-in legend is
        // suppressed so the two never fight over interaction state.
        legend: { display: false },
        tooltip: {
          mode: 'index',
          intersect: false,
          callbacks: {
            footer: function (ctx) {
              const total = ctx.reduce(function (a, c) { return a + (c.parsed.y || 0); }, 0);
              return 'total: ' + total.toLocaleString();
            }
          }
        }
      },
      scales: {
        x: {
          stacked: stacked,
          title: { display: !!model.axes.xLabel, text: model.axes.xLabel },
          // No rotation so the time axis is always horizontally legible.
          ticks: { maxRotation: 0, autoSkip: true, autoSkipPadding: 12 },
          grid: { display: false }
        },
        y: {
          stacked: stacked,
          beginAtZero: true,
          title: { display: !!model.axes.yLabel, text: model.axes.yLabel },
          grid: {
            display: !!model.axes.gridLines,
            color: 'rgba(127,127,127,0.15)'
          },
          ticks: { callback: function (v) { return formatY(v, model.axes.yFormat); } }
        }
      },
      onClick: function (evt, items) {
        if (!items.length || !onBarClick) return;
        const ds = this.data.datasets[items[0].datasetIndex];
        if (ds && ds._seriesKey) onBarClick(ds._seriesKey);
      }
    };
  }

  // Alpine factory. Each <sb-chartlet> partial calls `sbChartlet({...})` once
  // via x-data; `init()` runs on x-init (so it also re-runs when HTMX swaps a
  // fresh node into the DOM — no manual rebind needed for OOB-swap freshness).
  window.sbChartlet = function (opts) {
    return {
      hidden: {},
      chart: null,
      init: function () {
        const canvas = document.getElementById(opts.id);
        if (!canvas) return;
        // If a previous Chart instance is still bound to this canvas (e.g.
        // the surrounding tile was HTMX-swapped in place), tear it down
        // before constructing a new one — Chart.js otherwise throws on a
        // second new Chart(canvas, ...).
        if (window.Chart && typeof window.Chart.getChart === 'function') {
          const prev = window.Chart.getChart(canvas);
          if (prev) prev.destroy();
        }
        let model;
        try {
          model = JSON.parse(canvas.dataset.chartlet);
        } catch (e) {
          return;
        }
        const self = this;
        const onBarClick = function (seriesKey) {
          if (!opts.drill) return;
          const u = new URL(opts.drill.url, window.location.href);
          u.searchParams.set(opts.drill.paramKey, seriesKey);
          if (window.htmx && typeof window.htmx.ajax === 'function') {
            window.htmx.ajax('GET', u.toString(), {
              target: opts.drill.panelTarget,
              swap: 'innerHTML'
            });
          }
          // Bookmarkable filter state — back/forward navigation restores the
          // last-applied filter via the existing HTMX boost handlers.
          window.history.pushState({}, '', u.toString());
        };
        const config = {
          type: chartType(model.kind),
          data: {
            labels: model.bucketLabels,
            datasets: buildDatasets(model)
          },
          options: buildOptions(model, onBarClick)
        };
        // Horizontal bar special-case: flip the index axis and turn stacking
        // off on both scales so the bars stand alone per row.
        if (model.kind === 'HorizontalBar') {
          config.options.indexAxis = 'y';
          config.options.scales.x.stacked = false;
          config.options.scales.y.stacked = false;
        }
        if (!window.Chart) return;
        self.chart = new window.Chart(canvas, config);
      },
      toggle: function (key) {
        this.hidden[key] = !this.hidden[key];
        if (!this.chart) return;
        const ds = this.chart.data.datasets.find(function (d) { return d._seriesKey === key; });
        if (ds) ds.hidden = this.hidden[key];
        this.chart.update();
      }
    };
  };
})();