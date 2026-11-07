#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Lukas Körber
# SPDX-License-Identifier: AGPL-3.0-only
#
# Runs once after the dev container is created. Restores the NuGet packages so the first build
# and test run start warm. Nothing here is required for the app to start, so a failure does not
# block the Codespace.
set -u

cd "$(dirname "$0")/.."

dotnet restore StudyLife.sln || echo "dotnet restore failed - run it again manually."
