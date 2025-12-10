// CSS is built separately by Tailwind CLI - don't import here
import Alpine from 'alpinejs';
import htmx from 'htmx.org';

// Initialize Alpine.js
(window as any).Alpine = Alpine;

// Theme store - always dark mode
Alpine.store('theme', {
  current: 'dark',
  toggle() {
    // Do nothing - dark mode only
  }
});

// Theme switcher component - always dark mode
Alpine.data('themeSwitcher', () => ({
  isDark: true,
  init() {
    // Always dark mode
    this.isDark = true;
    this.apply();
  },
  toggle() {
    // Do nothing - dark mode only
  },
  apply() {
    const html = document.documentElement;
    html.classList.add('dark');
    html.setAttribute('data-theme', 'dark');
  }
}));

Alpine.start();

// Initialize HTMX
(window as any).htmx = htmx;
