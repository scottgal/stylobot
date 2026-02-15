# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files and restore (layer cached if csproj unchanged)
COPY Stylobot.Gateway/Stylobot.Gateway.csproj Stylobot.Gateway/
COPY Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj Mostlylucid.BotDetection/
COPY Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj Mostlylucid.BotDetection.UI/
COPY Mostlylucid.BotDetection.UI.PostgreSQL/Mostlylucid.BotDetection.UI.PostgreSQL.csproj Mostlylucid.BotDetection.UI.PostgreSQL/
COPY Mostlylucid.Common/Mostlylucid.Common.csproj Mostlylucid.Common/
COPY Mostlylucid.GeoDetection/Mostlylucid.GeoDetection.csproj Mostlylucid.GeoDetection/
COPY Mostlylucid.GeoDetection.Contributor/Mostlylucid.GeoDetection.Contributor.csproj Mostlylucid.GeoDetection.Contributor/
# csproj files read ReleaseNotes.txt during restore for PackageReleaseNotes
COPY Mostlylucid.BotDetection/ReleaseNotes.txt Mostlylucid.BotDetection/
COPY Mostlylucid.GeoDetection/ReleaseNotes.txt Mostlylucid.GeoDetection/
RUN dotnet restore Stylobot.Gateway/Stylobot.Gateway.csproj

# Copy source and publish with optimizations
COPY Stylobot.Gateway/ Stylobot.Gateway/
COPY Mostlylucid.BotDetection/ Mostlylucid.BotDetection/
COPY Mostlylucid.BotDetection.UI/ Mostlylucid.BotDetection.UI/
COPY Mostlylucid.BotDetection.UI.PostgreSQL/ Mostlylucid.BotDetection.UI.PostgreSQL/
COPY Mostlylucid.Common/ Mostlylucid.Common/
COPY Mostlylucid.GeoDetection/ Mostlylucid.GeoDetection/
COPY Mostlylucid.GeoDetection.Contributor/ Mostlylucid.GeoDetection.Contributor/
RUN dotnet publish Stylobot.Gateway/Stylobot.Gateway.csproj -c Release -o /app \
    /p:DebugType=none \
    /p:DebugSymbols=false

# Stage 2: Runtime (Alpine-based, smallest practical image)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Create non-root user for security
RUN addgroup -g 1000 gateway && \
    adduser -u 1000 -G gateway -s /bin/sh -D gateway

WORKDIR /app

# Copy published app from build stage
COPY --from=build /app .

# Create standard directories with correct permissions
RUN mkdir -p /app/config /app/data /app/logs /app/plugins && \
    chown -R gateway:gateway /app

# Switch to non-root user
USER gateway

# Expose port
EXPOSE 8080

# Liveness check using lightweight /admin/alive (no DB hit, fast response)
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD wget -q -O /dev/null http://localhost:8080/admin/alive || exit 1

# Environment defaults
ENV ASPNETCORE_URLS=http://+:8080 \
    GATEWAY_HTTP_PORT=8080 \
    GATEWAY_CONFIG_PATH=/app/config \
    GATEWAY_DATA_PATH=/app/data \
    GATEWAY_LOGS_PATH=/app/logs \
    GATEWAY_PLUGINS_PATH=/app/plugins \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

ENTRYPOINT ["dotnet", "Stylobot.Gateway.dll"]
