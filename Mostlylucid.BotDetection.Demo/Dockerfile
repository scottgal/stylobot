# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj", "Mostlylucid.BotDetection.Demo/"]
COPY ["Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj", "Mostlylucid.BotDetection.UI/"]
COPY ["Mostlylucid.BotDetection.ApiHolodeck/Mostlylucid.BotDetection.ApiHolodeck.csproj", "Mostlylucid.BotDetection.ApiHolodeck/"]
COPY ["Mostlylucid.GeoDetection/Mostlylucid.GeoDetection.csproj", "Mostlylucid.GeoDetection/"]
COPY ["Mostlylucid.GeoDetection.Contributor/Mostlylucid.GeoDetection.Contributor.csproj", "Mostlylucid.GeoDetection.Contributor/"]
COPY ["Mostlylucid.Common/Mostlylucid.Common.csproj", "Mostlylucid.Common/"]

# Copy ReleaseNotes.txt files (required by .csproj files during restore)
COPY ["Mostlylucid.BotDetection/ReleaseNotes.txt", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.ApiHolodeck/ReleaseNotes.txt", "Mostlylucid.BotDetection.ApiHolodeck/"]
COPY ["Mostlylucid.GeoDetection/ReleaseNotes.txt", "Mostlylucid.GeoDetection/"]

# Restore dependencies (ignoring missing mock LLM API - not needed for demo)
RUN dotnet restore "Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj" --ignore-failed-sources ||  dotnet restore "Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj"

# Copy everything else
COPY . .

# Build
WORKDIR "/src/Mostlylucid.BotDetection.Demo"
RUN dotnet build "Mostlylucid.BotDetection.Demo.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "Mostlylucid.BotDetection.Demo.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:CopyOutputSymbolsToPublishDirectory=false /p:GeneratePackageOnBuild=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl for healthchecks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=publish /app/publish .

# Expose ports
EXPOSE 5000
EXPOSE 5001

# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "Mostlylucid.BotDetection.Demo.dll"]
