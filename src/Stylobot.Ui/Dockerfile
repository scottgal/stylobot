# syntax=docker/dockerfile:1
# stylobot-ui: dashboard-host image. Run it, hit http://<host>:8080/_stylobot, see a
# dashboard of traffic it detects on the same port. Reverse-proxy your real traffic
# through it, or hit it directly for a self-monitoring instance.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Stylobot.Ui/Stylobot.Ui.csproj", "Stylobot.Ui/"]
COPY ["Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj", "Mostlylucid.BotDetection/"]
COPY ["Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj", "Mostlylucid.BotDetection.UI/"]
COPY ["Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj", "Mostlylucid.BotDetection.Api/"]
COPY ["Mostlylucid.BotDetection.OpenApi/Mostlylucid.BotDetection.OpenApi.csproj", "Mostlylucid.BotDetection.OpenApi/"]
COPY ["Mostlylucid.BotDetection.Llm/Mostlylucid.BotDetection.Llm.csproj", "Mostlylucid.BotDetection.Llm/"]
COPY ["Mostlylucid.BotDetection.Llm.Tunnel/Mostlylucid.BotDetection.Llm.Tunnel.csproj", "Mostlylucid.BotDetection.Llm.Tunnel/"]
COPY ["Mostlylucid.Common/Mostlylucid.Common.csproj", "Mostlylucid.Common/"]

COPY ["Mostlylucid.BotDetection/ReleaseNotes.txt", "Mostlylucid.BotDetection/"]

RUN dotnet restore "Stylobot.Ui/Stylobot.Ui.csproj"

COPY . .
WORKDIR "/src/Stylobot.Ui"
RUN dotnet publish "Stylobot.Ui.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:CopyOutputSymbolsToPublishDirectory=false /p:GeneratePackageOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN mkdir -p /app/data /app/logs && chown -R app:app /app
USER app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/_stylobot || exit 1

ENTRYPOINT ["dotnet", "stylobot-ui.dll"]
