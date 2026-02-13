import daisyui from 'daisyui';

export default {
  content: [
    './Views/**/*.cshtml',
    './wwwroot/src/**/*.{js,ts,jsx,tsx}',
  ],
  theme: {
    extend: {},
  },
  plugins: [daisyui],
  daisyui: {
    themes: ["light", "dark"],
    darkTheme: "dark",
  },
};
