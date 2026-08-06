#!/usr/bin/env bash
# =============================================================================
# StudyLife -- Docker Setup
#
# Starts: Server (Blazor WASM + ASP.NET Core API), Watchtower
#
# Usage:
#   chmod +x setup.sh && ./setup.sh
# =============================================================================

set -e

BOLD="\033[1m"
GREEN="\033[32m"
YELLOW="\033[33m"
RESET="\033[0m"

echo -e "${BOLD}=== StudyLife -- Docker Setup ===${RESET}"
echo ""

# --- Load or create .env ------------------------------------------------
if [ ! -f .env ]; then
    cp .env.example .env
    echo -e "${YELLOW}[Info] Created .env from .env.example.${RESET}"
fi

# Strip CRLF (Windows line endings) and convert .env in-place
tr -d '\r' < .env > .env.tmp && mv .env.tmp .env
set -a; source .env; set +a

# --- Optional: only log in if a private registry override is configured ---
# The default image (ghcr.io/lukislp/studylife-server) is public - no login needed to
# pull it. This block only runs for people who set SERVER_IMAGE to their own fork's
# image on a private registry (see .env.example).
if [ -n "${REGISTRY_URL:-}" ] && [ -n "${REGISTRY_USER:-}" ] && [ -n "${REGISTRY_PASSWORD:-}" ]; then
    echo -e "${BOLD}[1/3] Logging into ${REGISTRY_URL}...${RESET}"
    echo "${REGISTRY_PASSWORD}" | docker login "${REGISTRY_URL}" -u "${REGISTRY_USER}" --password-stdin

    # Make Docker credentials available to Watchtower under /root/.docker/config.json
    if [ ! -f /root/.docker/config.json ] && [ -f ~/.docker/config.json ]; then
        mkdir -p /root/.docker
        cp ~/.docker/config.json /root/.docker/config.json
    fi
else
    echo -e "${BOLD}[1/3] Pulling public image (no registry login needed)...${RESET}"
fi

docker compose pull server

# --- Start the stack ------------------------------------------------------------
echo -e "${BOLD}[2/3] Starting stack...${RESET}"
docker compose up -d

# --- Done -------------------------------------------------------------------
echo ""
echo -e "${BOLD}[3/3] Done!${RESET}"
echo ""
echo -e "${GREEN}${BOLD}=== Stack is running ===${RESET}"
echo ""
echo "  App     : http://localhost:${PORT:-8080}"
echo ""
echo "  Logs    : docker compose logs -f"
echo "  Stop    : docker compose down"
echo "  Update  : docker compose pull && docker compose up -d"
echo ""
