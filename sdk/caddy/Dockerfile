# syntax=docker/dockerfile:1

# Stage 1: build Caddy with the stylobot module
FROM caddy:builder AS builder

ARG MODULE_VERSION=latest

RUN xcaddy build \
    --with github.com/scottgal/caddy-stylobot@${MODULE_VERSION}

# Stage 2: minimal runtime image
FROM caddy:latest

COPY --from=builder /usr/bin/caddy /usr/bin/caddy

# Verify the module is compiled in
RUN caddy list-modules | grep stylobot

EXPOSE 80 443 2019

CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
