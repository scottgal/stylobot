# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is an ASP.NET Core 10 MVC web application with a modern frontend stack using Vite, TailwindCSS, DaisyUI, Alpine.js, and HTMX. The application uses Vite for frontend asset bundling with HMR (Hot Module Replacement) in development.

## Build Commands

### Backend (.NET)
```powershell
# Build the solution
dotnet build

# Run the application (development)
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj

# The app runs on:
# - HTTPS: https://localhost:7038
# - HTTP: http://localhost:5062
```

### Frontend (Vite)
```bash
# Install dependencies
npm install --prefix src/Stylobot.Website

# Run Vite dev server with HMR (runs on http://localhost:5173)
npm run dev --prefix src/Stylobot.Website

# Build production assets
npm run build --prefix src/Stylobot.Website

# Preview production build
npm run preview --prefix src/Stylobot.Website
```

### Development Workflow
For full development experience with HMR:
1. Start the Vite dev server: `npm run dev --prefix src/Stylobot.Website` (port 5173)
2. Start the .NET backend: `dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj`
3. Access the app through the .NET URL (https://localhost:7038 or http://localhost:5062)
4. Vite dev server scripts are referenced in _Layout.cshtml when in Development environment

## Architecture

### Frontend Asset Pipeline

**Development Mode:**
- _Layout.cshtml (lines 9-14) loads Vite dev server scripts directly from http://localhost:5173
- Vite provides HMR for instant updates without page refresh
- The Vite dev server proxies API requests to the .NET backend (configured in vite.config.ts:25-31)

**Production Mode:**
- _Layout.cshtml (lines 15-19, 55-58) loads built assets from wwwroot/dist/
- Vite builds assets to wwwroot/dist/ via `npm run build`
- Entry point: wwwroot/src/main.ts
- Output: wwwroot/dist/assets/index.js and wwwroot/dist/assets/index.css

### Frontend Stack Integration

**Entry Point:** wwwroot/src/main.ts
- Imports TailwindCSS styles from index.css
- Initializes Alpine.js and exposes as `window.Alpine`
- Initializes HTMX and exposes as `window.htmx`

**Styling:**
- TailwindCSS v4 with @tailwindcss/postcss plugin
- DaisyUI component library with 'light' and 'dark' themes (tailwind.config.cjs:11)
- Content sources: Views/**/*.cshtml and wwwroot/src/**/*.{js,ts,jsx,tsx}

**JavaScript Libraries:**
- **Alpine.js**: Reactive UI framework (started in main.ts:6)
- **HTMX**: Server-side driven interactivity (exposed globally in main.ts:8)

### Backend Structure

**ASP.NET Core MVC:**
- Target Framework: .NET 10
- Standard MVC pattern: Controllers → Views → Models
- Default route: {controller=Home}/{action=Index}/{id?}

**Controllers:**
- HomeController: Index, Privacy, Time (HTMX endpoint returning HTML fragment)
- Time endpoint (line 20-24) returns DaisyUI-styled HTML for server time display

**Views:**
- Razor views in Views/ directory
- Shared layout: Views/Shared/_Layout.cshtml
- Uses ASP.NET Core tag helpers (asp-controller, asp-action, etc.)

## Project Structure

```
src/Stylobot.Website/
├── Controllers/          # MVC Controllers
├── Models/              # View models
├── Views/               # Razor views (.cshtml)
│   ├── Home/           # Home controller views
│   └── Shared/         # Shared layouts and partials
├── wwwroot/            # Static files
│   ├── src/            # Frontend source (TypeScript, CSS)
│   │   ├── main.ts     # Entry point
│   │   └── index.css   # TailwindCSS imports
│   └── dist/           # Vite build output (generated)
├── Program.cs          # ASP.NET Core startup
├── package.json        # npm dependencies and scripts
├── vite.config.ts      # Vite configuration
├── tailwind.config.cjs # TailwindCSS configuration
└── postcss.config.cjs  # PostCSS configuration
```

## Important Configuration Details

### Vite Configuration (vite.config.ts)
- Entry: wwwroot/src/main.ts
- Output directory: wwwroot/dist/
- Dev server port: 5173 (strict)
- Proxies all requests to https://localhost:5001 in development (should match .NET HTTPS port)
- Note: Proxy target may need updating if launchSettings.json HTTPS port changes

### TailwindCSS
- Uses TailwindCSS v4 with @tailwindcss/postcss
- Scans Views/**/*.cshtml and wwwroot/src/**/*.{js,ts,jsx,tsx} for class names
- DaisyUI themes configured: 'light' and 'dark'
- HTML data-theme attribute set in _Layout.cshtml (line 3)

### ASP.NET Core
- Targets net10.0
- Nullable reference types enabled
- Implicit usings enabled
- Default HTTPS port: 7038, HTTP port: 5062 (from launchSettings.json)