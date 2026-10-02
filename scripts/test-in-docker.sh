#!/usr/bin/env sh
# Runs the test suite in a Linux container (official .NET 10 SDK image).
# Useful on Windows machines where Smart App Control blocks unsigned dev builds.
# Usage: scripts/test-in-docker.sh [extra dotnet test args]
set -eu
repo="$(cd "$(dirname "$0")/.." && pwd)"
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$repo:/repo:ro" \
  -v resetme-nuget:/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'mkdir /src && cd /repo && tar --exclude=bin --exclude=obj --exclude=.git -cf - . | tar -xf - -C /src && cd /src && dotnet test "$@"' -- "$@"
