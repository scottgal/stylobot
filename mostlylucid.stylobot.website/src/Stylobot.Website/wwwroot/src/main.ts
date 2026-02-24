// CSS is built separately by Tailwind CLI - don't import here
import Alpine from 'alpinejs';
import htmx from 'htmx.org';
import { registerLiveDemo } from './live-demo';
import { initCommandCenter } from './dashboard-viz';

const THEME_KEY = 'sb-theme';
const DARK_THEME = 'dark';
const LIGHT_THEME = 'light';

function systemPrefersDark(): boolean {
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function resolveInitialTheme(): 'dark' | 'light' {
  const saved = localStorage.getItem(THEME_KEY);
  if (saved === 'dark' || saved === 'light') {
    return saved;
  }

  return systemPrefersDark() ? 'dark' : 'light';
}

function applyTheme(mode: 'dark' | 'light'): void {
  const html = document.documentElement;
  const isDark = mode === 'dark';
  html.classList.toggle('dark', isDark);
  html.setAttribute('data-theme', isDark ? DARK_THEME : LIGHT_THEME);
}

// Initialize Alpine.js
(window as any).Alpine = Alpine;

const initialTheme = resolveInitialTheme();
applyTheme(initialTheme);

// Theme store
Alpine.store('theme', {
  current: initialTheme,
  toggle() {
    this.current = this.current === 'dark' ? 'light' : 'dark';
    localStorage.setItem(THEME_KEY, this.current);
    applyTheme(this.current);
  }
});

// Theme switcher component
Alpine.data('themeSwitcher', () => ({
  isDark: initialTheme === 'dark',
  init() {
    this.isDark = Alpine.store('theme').current === 'dark';
    this.apply();
  },
  toggle() {
    Alpine.store('theme').toggle();
    this.isDark = Alpine.store('theme').current === 'dark';
  },
  apply() {
    applyTheme(this.isDark ? 'dark' : 'light');
  }
}));

// Register page-specific Alpine components
registerLiveDemo();

Alpine.start();

// Initialize HTMX
(window as any).htmx = htmx;
htmx.config.allowEval = false;

// Initialize Command Center on dashboard pages (deferred to avoid blocking)
if (document.getElementById('command-map') || document.getElementById('countries-map')) {
    initCommandCenter();
}
