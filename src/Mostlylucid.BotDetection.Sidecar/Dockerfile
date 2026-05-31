# StyloBot detection sidecar - gRPC (and optional REST) detection API.
#
# Build context is the repository root (the project pulls in the detection
# engine, the REST API, and sdk/proto/detection.proto):
#   docker build -f src/Mostlylucid.BotDetection.Sidecar/Dockerfile -t stylobot-sidecar .
#
# The sidecar is published as a self-contained single-file binary, so you can
# either run this image directly or lift the binary into your own image to run
# the sidecar and your app together in one container:
#   COPY --from=scottgal/stylobot-sidecar:latest /usr/local/bin/stylobot-sidecar /usr/local/bin/

# --- build -------------------------------------------------------------------
# Native AOT publish needs clang + zlib. Drop --platform=$BUILDPLATFORM so
# buildx targets the native arch under QEMU per platform - cross-compile AOT
# from x64 to arm64 would need a second toolchain (gcc-aarch64-linux-gnu,
# arm64 zlib, lld) and is brittle.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0
WORKDIR /src
# protobuf-compiler is the workaround for Grpc.Tools 2.80.0's bundled linux_arm64
# protoc segfaulting under QEMU. -p:Protobuf_ProtocFullPath= tells Grpc.Tools to
# use the system protoc instead of its (broken under QEMU) bundled one.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev protobuf-compiler \
 && rm -rf /var/lib/apt/lists/*
COPY . .
RUN dotnet publish src/Mostlylucid.BotDetection.Sidecar/Mostlylucid.BotDetection.Sidecar.csproj \
      --configuration Release \
      --self-contained true \
      -p:Version="$VERSION" \
      -p:Protobuf_ProtocFullPath=/usr/bin/protoc \
      --output /app

# --- runtime -----------------------------------------------------------------
# runtime-deps carries the native dependencies a self-contained .NET app needs
# without the managed runtime (which is bundled into the single file).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data && chown app /data
COPY --from=build /app/stylobot-sidecar /usr/local/bin/stylobot-sidecar

# Non-root. /data holds the SQLite state; WORKDIR makes relative DB paths land there.
USER app
WORKDIR /data
VOLUME ["/data"]

ENV STYLOBOT_PORT=5090
# Loopback-only by default (same-pod / same-container model). For a sidecar
# reached across a network, set STYLOBOT_BIND=any and configure BotDetection:ApiKeys.
EXPOSE 5090 5091

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -fsS --http2-prior-knowledge "http://127.0.0.1:${STYLOBOT_PORT:-5090}/health" || exit 1

ENTRYPOINT ["/usr/local/bin/stylobot-sidecar"]
