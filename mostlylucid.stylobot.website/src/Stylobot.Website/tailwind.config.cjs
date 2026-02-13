module.exports = {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/src/**/*.{js,ts,jsx,tsx}',
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
