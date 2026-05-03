.PHONY: test build docker clean

MODULE := github.com/scottgal/caddy-stylobot
VERSION ?= dev

test:
	go test ./... -v -count=1

# Build a local Caddy binary with this module at current working tree state.
# Uses replace directive so xcaddy picks up local changes without pushing.
build:
	xcaddy build --with $(MODULE)=.

# Build for a specific released version.
build-release:
	xcaddy build --with $(MODULE)@$(VERSION)

# Run tests then build.
all: test build

# Build and tag Docker image locally.
docker:
	docker build --build-arg MODULE_VERSION=$(VERSION) -t caddy-stylobot:$(VERSION) .

clean:
	rm -f caddy caddy-stylobot_*
