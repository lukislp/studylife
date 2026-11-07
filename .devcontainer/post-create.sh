#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only
#
# Runs once after the dev container is created: restores the NuGet packages so the first build
# and test run start warm, and creates the ASP.NET Core development certificate. launchSettings.json
# binds both https://0.0.0.0:53963 and http://0.0.0.0:53964, and Kestrel refuses to start without
# a certificate for the HTTPS binding. Codespaces only forwards the HTTP port (GitHub terminates
# TLS), so the certificate is never trusted or used by a browser. A failure here does not block
# the Codespace.
set -u

cd "$(dirname "$0")/.."

dotnet restore StudyLife.sln || echo "dotnet restore failed - run it again manually."
dotnet dev-certs https || echo "dotnet dev-certs failed - run it again before dotnet run."
