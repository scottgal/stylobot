# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for the TrailblazorDemo and its referenced FOSS libs.
# Lean dependency set: the demo references only Mostlylucid.BotDetection
# and Mostlylucid.BotDetection.UI, which in turn pull in a handful of
# siblings. The Demo Dockerfile is the reference for shape.
COPY ["Mostlylucid.BotDetection.TrailblazorDemo/Mostlylucid.BotDetection.TrailblazorDemo.csproj", "Mostlylucid.BotDetection.TrailblazorDemo/"]
COPY ["Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj", "Mostlylucid.BotDetection.UI/"]
COPY ["Mostlylucid.BotDetection.OpenApi/Mostlylucid.BotDetection.OpenApi.csproj", "Mostlylucid.BotDetection.OpenApi/"]
COPY ["Mostlylucid.BotDetection.ApiHolodeck/Mostlylucid.BotDetection.ApiHolodeck.csproj", "Mostlylucid.BotDetection.ApiHolodeck/"]
COPY ["Mostlylucid.GeoDetection/Mostlylucid.GeoDetection.csproj", "Mostlylucid.GeoDetection/"]
COPY ["Mostlylucid.GeoDetection.Contributor/Mostlylucid.GeoDetection.Contributor.csproj", "Mostlylucid.GeoDetection.Contributor/"]
COPY ["Mostlylucid.Common/Mostlylucid.Common.csproj", "Mostlylucid.Common/"]

COPY ["Mostlylucid.BotDetection/ReleaseNotes.txt", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.ApiHolodeck/ReleaseNotes.txt", "Mostlylucid.BotDetection.ApiHolodeck/"]
COPY ["Mostlylucid.GeoDetection/ReleaseNotes.txt", "Mostlylucid.GeoDetection/"]

RUN dotnet restore "Mostlylucid.BotDetection.TrailblazorDemo/Mostlylucid.BotDetection.TrailblazorDemo.csproj"

COPY . .

WORKDIR "/src/Mostlylucid.BotDetection.TrailblazorDemo"
RUN dotnet build "Mostlylucid.BotDetection.TrailblazorDemo.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Mostlylucid.BotDetection.TrailblazorDemo.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:CopyOutputSymbolsToPublishDirectory=false /p:GeneratePackageOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

RUN mkdir -p /app/data /app/logs && \
    chown -R app:app /app

USER app

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "Mostlylucid.BotDetection.TrailblazorDemo.dll"]