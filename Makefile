# LaunchDarkly SDK Quickstart - everything runs in containers via Podman.
# No .NET SDK, no Flutter SDK, no compose provider needed on the host.

-include .env
export

FLAG_KEY    ?= combo-upsell-banner
DEMO_UNREACHABLE ?= false
BASE        ?= http://localhost:$(SERVER_PORT)
REQUESTS    ?= 200
CONCURRENCY ?= 10
SERVER_PORT ?= 5080
WEB_PORT     ?= 8081
SERVER_IMAGE ?= ld-quickstart-server
WEB_IMAGE    ?= ld-quickstart-web

.DEFAULT_GOAL := help

.PHONY: help
help:
	@echo "LaunchDarkly SDK Quickstart - containerised demo"
	@echo
	@echo "  make build        build both images"
	@echo "  make up           start both (server :$(SERVER_PORT), web :$(WEB_PORT))"
	@echo "  make check        curl every flag scenario and show the result"
	@echo "  make traffic      generate checkout traffic for a guarded rollout"
	@echo "  make provision    create the LaunchDarkly project, flags and metrics"
	@echo "  make open         print the URL to open in your browser"
	@echo "  make fallback     rebuild+run both pointed at a dead host (fallback demo)"
	@echo "  make normal       undo 'make fallback'"
	@echo "  make logs         follow the server log"
	@echo "  make down         stop and remove the containers"
	@echo "  make clean        also remove the images"
	@echo
	@echo "Credentials come from .env (see .env.example). Never committed."

# ---------------------------------------------------------------- guards
.PHONY: require-sdk-key require-client-id
require-sdk-key:
	@test -n "$(LD_SDK_KEY)" || { \
	  echo "LD_SDK_KEY is not set. Copy .env.example to .env and fill it in."; exit 1; }
	@case "$(LD_SDK_KEY)" in sdk-*) ;; *) \
	  echo "LD_SDK_KEY does not look like a server SDK key (expected sdk-...)."; exit 1;; esac
require-client-id:
	@test -n "$(LD_CLIENT_SIDE_ID)" || { \
	  echo "LD_CLIENT_SIDE_ID is not set. Copy .env.example to .env and fill it in."; exit 1; }

# ---------------------------------------------------------------- build
.PHONY: build server-image web-image
build: server-image web-image

server-image:
	podman build -t $(SERVER_IMAGE) ./server

# The client-side ID is public and is compiled into the web bundle, so it is a
# build arg. Change it and you rebuild. DEMO_UNREACHABLE flips the fallback demo.
web-image: require-client-id
	podman build \
	  --build-arg LD_CLIENT_SIDE_ID="$(LD_CLIENT_SIDE_ID)" \
	  --build-arg LD_FLAG_KEY="$(FLAG_KEY)" \
	  --build-arg LD_DEMO_UNREACHABLE="$(DEMO_UNREACHABLE)" \
	  -t $(WEB_IMAGE) ./mobile

# ---------------------------------------------------------------- run
.PHONY: up down logs open
up: down require-sdk-key
	podman run -d --name $(SERVER_IMAGE) -p $(SERVER_PORT):8080 \
	  -e LD_SDK_KEY="$(LD_SDK_KEY)" \
	  -e LaunchDarkly__FlagKey="$(FLAG_KEY)" \
	  -e LD_DEMO_UNREACHABLE="$(DEMO_UNREACHABLE)" \
	  $(SERVER_IMAGE)
	podman run -d --name $(WEB_IMAGE) -p $(WEB_PORT):80 $(WEB_IMAGE)
	@echo
	@echo "server : http://localhost:$(SERVER_PORT)/"
	@echo "web    : http://localhost:$(WEB_PORT)/"

down:
	-@podman rm -f $(SERVER_IMAGE) $(WEB_IMAGE) 2>/dev/null || true

logs:
	podman logs -f $(SERVER_IMAGE)

open:
	@echo "http://localhost:$(WEB_PORT)/"

# ---------------------------------------------------------------- demos
.PHONY: fallback normal
fallback:
	@$(MAKE) --no-print-directory web-image up DEMO_UNREACHABLE=true

normal:
	@$(MAKE) --no-print-directory web-image up DEMO_UNREACHABLE=false

# ---------------------------------------------------------------- checks
.PHONY: check
check:
	@echo "== 1 RELEASE  /combo-upsell (north -> true, RULE_MATCH) =="
	@curl -s '$(BASE)/combo-upsell?userKey=amy&storeRegion=north'; echo
	@echo "   south -> false, FALLTHROUGH"
	@curl -s '$(BASE)/combo-upsell?userKey=amy&storeRegion=south'; echo
	@echo
	@echo "== 2 OPERATIONAL  /loyalty/points (turn the flag off to bypass) =="
	@curl -s '$(BASE)/loyalty/points?userKey=amy'; echo
	@echo
	@echo "== 3 REMOTE CONFIG  /orders/queue-config (JSON flag) =="
	@curl -s '$(BASE)/orders/queue-config?userKey=amy'; echo
	@echo
	@echo "== 4 MIGRATION  /checkout (multivariate + guarded-rollout metrics) =="
	@curl -s '$(BASE)/checkout?userKey=amy'; echo
	@echo
	@echo "== 5 ENTITLEMENT  /staff/discount (segment: manager -> true) =="
	@curl -s '$(BASE)/staff/discount?userKey=amy&role=manager'; echo
	@echo "   role=cashier -> false"
	@curl -s '$(BASE)/staff/discount?userKey=ben&role=cashier'; echo
	@echo
	@echo "== health =="
	@curl -s '$(BASE)/health'; echo

# Generate metric events so a guarded rollout has data to judge.
.PHONY: traffic
traffic:
	@./scripts/traffic.sh $(REQUESTS) $(CONCURRENCY)

# Create the LaunchDarkly project, flags, segment and metrics. Idempotent.
.PHONY: provision
provision:
	@test -n "$(LD_API_TOKEN)" || { \
	  echo "LD_API_TOKEN is not set. Add it to .env (a Writer token is enough)."; exit 1; }
	@python3 scripts/provision.py

# ---------------------------------------------------------------- cleanup
.PHONY: clean
clean: down
	-@podman rmi -f $(SERVER_IMAGE) $(WEB_IMAGE) 2>/dev/null || true
	@echo "Images removed. 'podman image prune -f' clears the build cache too."
