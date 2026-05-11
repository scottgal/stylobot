FROM caddy:2-builder AS builder

WORKDIR /build
COPY sdk/caddy/ caddy-plugin/
COPY sdk/go/ go/

WORKDIR /build/caddy-plugin

RUN xcaddy build \
    --with github.com/scottgal/caddy-stylobot=/build/caddy-plugin \
    --with github.com/scottgal/stylobot-go=/build/go

FROM caddy:2
COPY --from=builder /build/caddy-plugin/caddy /usr/bin/caddy
COPY tests/integration/caddy-sidecar/Caddyfile /etc/caddy/Caddyfile
