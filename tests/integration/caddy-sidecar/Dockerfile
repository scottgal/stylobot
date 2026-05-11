FROM caddy:2-builder AS builder

WORKDIR /build/caddy-plugin
COPY sdk/caddy/ .

RUN xcaddy build \
    --with github.com/scottgal/caddy-stylobot=/build/caddy-plugin

FROM caddy:2
COPY --from=builder /build/caddy-plugin/caddy /usr/bin/caddy
COPY tests/integration/caddy-sidecar/Caddyfile /etc/caddy/Caddyfile
