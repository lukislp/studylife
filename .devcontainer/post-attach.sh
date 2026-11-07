#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only
#
# Runs on every attach: prints how to start the app and where it will be reachable.
set -u

if [ -n "${CODESPACE_NAME:-}" ]; then
  url="https://${CODESPACE_NAME}-53964.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}"
else
  url="http://localhost:53964"
fi

cat <<MSG

StudyLife dev container
  Start the app : dotnet run --project src/StudyLife.Server
  Open it at    : ${url}
  First start   : the one-time setup code is printed in the server log; enter it on /register.
  Tests         : dotnet test StudyLife.sln

MSG
