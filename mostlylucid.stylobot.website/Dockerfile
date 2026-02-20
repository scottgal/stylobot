# Base image for runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8443

# Install curl for healthchecks
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Build image with SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release

# Install Node.js v22.x
RUN apt-get update && apt-get install -y curl \
    && curl -fsSL https://deb.nodesource.com/setup_22.x -o nodesource_setup.sh \
    && bash nodesource_setup.sh \
    && apt-get install -y nodejs \
    && rm -f nodesource_setup.sh \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy csproj files for restore (layer caching)
COPY ["mostlylucid.stylobot.website/src/Stylobot.Website/Stylobot.Website.csproj", "mostlylucid.stylobot.website/src/Stylobot.Website/"]
COPY ["Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection/ReleaseNotes.txt", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.Llm/Mostlylucid.BotDetection.Llm.csproj", "Mostlylucid.BotDetection.Llm/"]
COPY ["Mostlylucid.BotDetection.Llm.LlamaSharp/Mostlylucid.BotDetection.Llm.LlamaSharp.csproj", "Mostlylucid.BotDetection.Llm.LlamaSharp/"]
COPY ["Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj", "Mostlylucid.BotDetection.UI/"]
COPY ["Mostlylucid.BotDetection.UI.PostgreSQL/Mostlylucid.BotDetection.UI.PostgreSQL.csproj", "Mostlylucid.BotDetection.UI.PostgreSQL/"]
COPY ["Mostlylucid.Common/Mostlylucid.Common.csproj", "Mostlylucid.Common/"]
COPY ["Mostlylucid.GeoDetection/Mostlylucid.GeoDetection.csproj", "Mostlylucid.GeoDetection/"]
COPY ["Mostlylucid.GeoDetection/ReleaseNotes.txt", "Mostlylucid.GeoDetection/"]
RUN dotnet restore "mostlylucid.stylobot.website/src/Stylobot.Website/Stylobot.Website.csproj"

# Copy entire source
COPY . .

# Install NPM dependencies and build assets
WORKDIR "/src/mostlylucid.stylobot.website/src/Stylobot.Website"
RUN npm install
RUN npm run build

# Build .NET project
RUN dotnet build "Stylobot.Website.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish stage
FROM build AS publish
WORKDIR "/src/mostlylucid.stylobot.website/src/Stylobot.Website"
RUN dotnet publish "Stylobot.Website.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# Final runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create data directories with correct permissions for built-in app user
RUN mkdir -p /app/data /app/logs && \
    chown -R app:app /app

# Switch to built-in non-root user (.NET 8+ images include 'app' user)
USER app

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Stylobot.Website.dll"]
