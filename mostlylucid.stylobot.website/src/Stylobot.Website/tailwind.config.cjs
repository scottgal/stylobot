module.exports = {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/src/**/*.{js,ts,jsx,tsx}',
    // BotDetection.UI dashboard partials (so Tailwind includes their utility classes)
    '../../../../Mostlylucid.BotDetection.UI/Views/**/*.cshtml',
  ],
  safelist: ['dark'],
  darkMode: 'class',
  theme: {
    extend: {},
  },
  plugins: [require('daisyui')],
  daisyui: {
    themes: ['light', 'dark'],
    darkTheme: 'dark',
  },
};
