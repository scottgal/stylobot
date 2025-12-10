# StyloBot Website

Commercial website for StyloBot - Intelligent Bot Detection & Protection powered by StyloFlow Ephemeral.

## Quick Start

### Docker Compose (Recommended)

The easiest way to run the site with YARP gateway bot protection:

```bash
docker-compose up -d
```

This starts:
- **YARP Gateway** (`scottgal/mostlylucid.yarpgateway`) - Reverse proxy with bot detection on port 80/443
- **Stylobot Website** - ASP.NET Core 10 MVC site behind the gateway

Access the site at `http://localhost` (or `https://localhost` if SSL is configured).

### Local Development

#### Prerequisites
- .NET 10 SDK (preview)
- Node.js 22+

#### Run Backend
```bash
cd src/Stylobot.Website
dotnet run
```

Site runs on:
- HTTPS: https://localhost:7038
- HTTP: http://localhost:5062

#### Run Frontend (Hot Module Replacement)
In a separate terminal:
```bash
cd src/Stylobot.Website
npm install
npm run dev
```

Vite dev server runs on http://localhost:5173 with HMR enabled.

### Simple Build & Run

```bash
# Build frontend assets
cd src/Stylobot.Website
npm install
npm run build

# Run the site
cd ../..
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj
```

The site automatically starts Vite in watch mode (builds on file changes).

## Architecture

### Frontend Stack
- **Vite** - Fast build tool with HMR
- **TailwindCSS v3** - Utility-first CSS
- **DaisyUI** - Component library
- **Alpine.js** - Reactive UI framework
- **HTMX** - Server-side driven interactivity

### Backend Stack
- **ASP.NET Core 10** - MVC framework
- **Razor Views** - Server-rendered HTML
- **.NET 10** - Latest .NET runtime

### Deployment Stack
- **YARP Gateway** - Reverse proxy with bot detection
- **StyloFlow Ephemeral** - Workflow engine for adaptive scaling
- **Docker** - Containerization
- **Docker Compose** - Multi-container orchestration

## Bot Detection Testing

The YARP gateway (`scottgal/mostlylucid.yarpgateway`) includes live bot detection powered by the Mostlylucid.BotDetection library. This is the **same technology** being showcased on the website.

### Features Being Tested Live:
- Zero-config bot detection
- Automatic endpoint learning
- Pattern reputation tracking
- Adaptive ML model
- Sub-millisecond inference
- Self-cleaning architecture (StyloFlow Ephemeral)

## Project Structure

```
src/Stylobot.Website/
├── Controllers/          # MVC Controllers
├── Models/              # View models
├── Views/               # Razor views
│   ├── Home/           # Home controller views
│   │   ├── Index.cshtml       # Homepage
│   │   ├── Features.cshtml    # Features page
│   │   ├── Detectors.cshtml   # Detection methods
│   │   └── Enterprise.cshtml  # Enterprise info
│   └── Shared/         # Shared layouts
├── wwwroot/            # Static files
│   ├── src/            # Frontend source
│   │   ├── main.ts     # TypeScript entry point
│   │   └── index.css   # TailwindCSS imports
│   └── dist/           # Vite build output (gitignored)
├── Program.cs          # ASP.NET Core startup
├── package.json        # npm dependencies
├── vite.config.ts      # Vite configuration
└── tailwind.config.cjs # TailwindCSS configuration
```

## Environment Variables

### Gateway Configuration
```bash
# YARP Gateway
ReverseProxy__Clusters__stylobot-cluster__Destinations__destination1__Address=http://website:8080
ReverseProxy__Routes__stylobot-route__ClusterId=stylobot-cluster
ReverseProxy__Routes__stylobot-route__Match__Path={**catch-all}
```

### Website Configuration
```bash
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
```

## Volume Mounts

Bot detection patterns are persisted in a Docker volume:
```yaml
volumes:
  - bot-detection-data:/app/data
```

This ensures learned patterns survive container restarts.

## Health Checks

The website includes a health endpoint:
```bash
curl http://localhost:8080/health
```

Docker automatically monitors this for container health.

## Scaling

### Horizontal Scaling (Kubernetes)

The site is stateless and can scale horizontally. Shared state (learned bot patterns) can use:
- **SQLite with volume mounts** (single-node)
- **Redis** (multi-node clusters)

### Adaptive Resource Usage

Powered by **StyloFlow Ephemeral**, the bot detection engine automatically:
- Bounds concurrency to prevent overload
- Self-cleans operations to prevent memory leaks
- Scales from Raspberry Pi to enterprise clusters
- Optimizes resource usage dynamically

See [Ephemeral documentation](https://github.com/scottgal/mostlylucid.atoms/blob/main/mostlylucid.ephemeral/README.md) for details.

## Build Commands

### Frontend Only
```bash
cd src/Stylobot.Website
npm install
npm run build        # Production build
npm run dev          # Development with HMR
npm run preview      # Preview production build
```

### Backend Only
```bash
dotnet build
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj
```

### Docker
```bash
# Build image
docker build -t stylobot-website .

# Run container
docker run -p 8080:8080 stylobot-website

# Or use docker-compose
docker-compose up -d
```

## Theme Support

The site supports light and dark themes via DaisyUI:
- Themes configured in `tailwind.config.cjs`
- Theme toggle in navigation (Alpine.js store)
- Persists preference in localStorage

## Contributing

This is the commercial website for StyloBot. For bot detection library issues/PRs, see:
- [Mostlylucid.BotDetection](https://github.com/scottgal/mostlylucid.nugetpackages)

## License

Copyright © 2025 MostlyLucid. All rights reserved.
