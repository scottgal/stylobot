(function () {
  'use strict';

  // Resolve a CSS custom-property token (e.g. `--sb-color-bot`) against the
  // active theme. Done at runtime so dark-mode + per-tier theme overrides are
  // picked up without rebuilding the chart on theme change.
  function colorFor(token) {
    const v = getComputedStyle(document.documentElement).getPropertyValue(token).trim();
    return v || '#888';
  }

  // Categorical palette for single-series charts where each BAR is its own
  // category (by-country, by-bot-type). These are NOT risk-semantic colours —
  // they only make adjacent bars distinct. Theme-owned as CSS custom properties
  // (--sb-chart-cat-N in sb-chartlet.css) so they follow light/dark + tier.
  var CATEGORICAL_TOKENS = [
    '--sb-chart-cat-1', '--sb-chart-cat-2', '--sb-chart-cat-3', '--sb-chart-cat-4',
    '--sb-chart-cat-5', '--sb-chart-cat-6', '--sb-chart-cat-7', '--sb-chart-cat-8'
  ];
  function categoricalColor(i) {
    return colorFor(CATEGORICAL_TOKENS[i % CATEGORICAL_TOKENS.length]);
  }

  // A single-series horizontal bar is categorical: each bar is a distinct
  // bucket, so every bar gets its own colour instead of one flat series colour.
  function isCategorical(model) {
    return model.kind === 'HorizontalBar' && model.series.length === 1;
  }

  function buildDatasets(model) {
    var categorical = isCategorical(model);
    return model.series.map(function (s) {
      var bg = categorical
        ? s.buckets.map(function (_, i) { return categoricalColor(i); })
        : colorFor(s.colorToken);
      return {
        label: s.label,
        data: s.buckets,
        backgroundColor: bg,
        borderColor: bg,
        borderWidth: 0,
        stack: 'series',
        // UX2: honour the server-side `hidden` flag on initial paint so
        // default-hidden series (Internal on Hits-per-period) start
        // unselected without the operator clicking once first. Chart.js
        // reads `dataset.hidden` exactly once at construction.
        hidden: !!s.hidden,
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
    const isLog = model.axes && model.axes.yScale === 'logarithmic';
    const yScale = {
      stacked: stacked,
      title: { display: !!model.axes.yLabel, text: model.axes.yLabel },
      grid: {
        display: !!model.axes.gridLines,
        color: 'rgba(127,127,127,0.15)'
      },
      ticks: { callback: function (v) { return formatY(v, model.axes.yFormat); } }
    };
    if (isLog) {
      // Chart.js v4 logarithmic axis rejects 0 (log(0) = -Infinity). Force the
      // floor to 1 so empty buckets still render at the baseline instead of
      // throwing the whole stack off. `beginAtZero` is meaningless on a log
      // scale and would conflict with `min`, so we omit it.
      yScale.type = 'logarithmic';
      yScale.min = 1;
    } else {
      yScale.beginAtZero = true;
    }
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
        y: yScale
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
    // UX2: seed the legend's hidden-state map from the server-emitted
    // initialHidden dictionary so a series the builder marked Hidden=true
    // renders with the legend pill already in the "off" state. Shallow
    // copy so subsequent toggle() calls don't mutate the shared object.
    var seed = (opts && opts.initialHidden) ? Object.assign({}, opts.initialHidden) : {};
    // The Chart.js instance is held in a per-factory closure variable, NOT on the
    // returned x-data object. If it lived on the reactive object, Alpine would wrap
    // it in a reactive Proxy; then toggle()'s `chart.update()` — running inside an
    // Alpine click effect — reads Chart.js internals THROUGH the proxy, Alpine tracks
    // those reads as dependencies, and the `hidden` mutation re-runs the effect →
    // update() again → infinite recursion ("Maximum call stack size exceeded" on
    // every series-toggle / bar click). The `hidden` legend map stays reactive (the
    // Razor legend pills bind to it); only the chart instance must be non-reactive.
    var chart = null;
    return {
      hidden: seed,
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
          // With the index axis flipped, Y is the CATEGORY axis and X is the
          // VALUE axis. buildOptions put the value-formatter on Y, which would
          // format the bar INDEX as a number (0, 1, 2...) instead of showing
          // the bucket label. Move the formatter to X and let Y render labels.
          config.options.scales.x.ticks = {
            callback: function (v) { return formatY(v, model.axes.yFormat); },
            maxRotation: 0, autoSkip: true, autoSkipPadding: 12
          };
          config.options.scales.x.beginAtZero = true;
          config.options.scales.y.ticks = { autoSkip: false };
          // The value grid belongs on the value (X) axis now, not the category axis.
          config.options.scales.x.grid = { display: !!model.axes.gridLines, color: 'rgba(127,127,127,0.15)' };
          config.options.scales.y.grid = { display: false };
        }
        if (!window.Chart) return;
        chart = new window.Chart(canvas, config);
      },
      toggle: function (key) {
        this.hidden[key] = !this.hidden[key];
        if (!chart) return;
        const ds = chart.data.datasets.find(function (d) { return d._seriesKey === key; });
        if (ds) ds.hidden = this.hidden[key];
        chart.update();
      }
    };
  };
})();