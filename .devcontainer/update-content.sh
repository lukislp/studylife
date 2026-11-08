#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only
#
# updateContentCommand: runs when the dev container is built (also inside a Codespaces prebuild)
# and again when a Codespace opens from a prebuild that is older than the branch, so the restored
# packages always match the checked-out sources. Restores the NuGet packages so the first build and
# test run start warm, and creates the ASP.NET Core development certificate. launchSettings.json
# binds both https://0.0.0.0:53963 and http://0.0.0.0:53964, and Kestrel refuses to start without
# a certificate for the HTTPS binding. Codespaces only forwards the HTTP port (GitHub terminates
# TLS), so the certificate is never trusted or used by a browser. A failure here does not block
# the Codespace.
set -u

cd "$(dirname "$0")/.."

# --locked-mode like every restore in CI: the packages.lock.json files pin each package by hash.
dotnet restore StudyLife.sln --locked-mode || echo "dotnet restore failed - run it again manually."
dotnet dev-certs https || echo "dotnet dev-certs failed - run it again before dotnet run."
