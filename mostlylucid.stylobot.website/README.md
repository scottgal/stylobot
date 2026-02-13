# Stylobot Website

User-friendly website for Stylobot bot detection.

## Use This First

- In-app docs: `/docs`
- Live demo page: `/Home/LiveDemo`
- Health endpoint: `/health`

## What you get

- Marketing + product pages
- Live detection demo and dashboard
- Built-in bot detection middleware
- Friendly docs rendered from markdown

## Friendly Docs System (Markdown -> HTML)

The website has an integrated docs renderer at `/docs`:

- Markdown parsed with **Markdig**
- Code highlighting with **highlight.js**
- Diagrams with **Mermaid** code blocks

Add or edit markdown files in:

- `src/Stylobot.Website/Docs/*.md`

They are automatically shown in the docs navigation.

## Quick Local Run

```bash
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj
```

Open:

- `https://localhost:7038`
- `https://localhost:7038/docs`
- `https://localhost:7038/_stylobot`

## Quick Docker Run

```bash
docker compose up -d
```

Then open:

- `http://localhost`

## Configuration

Copy `.env.example` to `.env` and set secrets:

- `BOTDETECTION_SIGNATURE_HASH_KEY`
- `BOTDETECTION_CLIENTSIDE_TOKEN_SECRET`

Optional database settings can be enabled later.

## Where to find deeper technical docs

- Repo docs: `../docs/README.md`
- Bot detection technical docs: `../Mostlylucid.BotDetection/docs/`
- Gateway technical docs: `../Stylobot.Gateway/docs/`
