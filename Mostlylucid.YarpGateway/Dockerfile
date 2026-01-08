# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files and restore (layer cached if csproj unchanged)
COPY Mostlylucid.YarpGateway/Mostlylucid.YarpGateway.csproj Mostlylucid.YarpGateway/
COPY Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj Mostlylucid.BotDetection/
# BotDetection.csproj reads ReleaseNotes.txt during restore for PackageReleaseNotes
COPY Mostlylucid.BotDetection/ReleaseNotes.txt Mostlylucid.BotDetection/
RUN dotnet restore Mostlylucid.YarpGateway/Mostlylucid.YarpGateway.csproj

# Copy source and publish with optimizations
COPY Mostlylucid.YarpGateway/ Mostlylucid.YarpGateway/
COPY Mostlylucid.BotDetection/ Mostlylucid.BotDetection/
RUN dotnet publish Mostlylucid.YarpGateway/Mostlylucid.YarpGateway.csproj -c Release -o /app --no-restore \
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

# Health check using busybox wget (already included in Alpine, no extra install needed)
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q -O /dev/null http://localhost:8080/admin/health || exit 1

# Environment defaults
ENV ASPNETCORE_URLS=http://+:8080 \
    GATEWAY_HTTP_PORT=8080 \
    GATEWAY_CONFIG_PATH=/app/config \
    GATEWAY_DATA_PATH=/app/data \
    GATEWAY_LOGS_PATH=/app/logs \
    GATEWAY_PLUGINS_PATH=/app/plugins \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

ENTRYPOINT ["dotnet", "Mostlylucid.YarpGateway.dll"]
