#!/usr/bin/env sh
# Runs any `dotnet` command in a Linux container (official .NET 10 SDK image) on a clean copy of
# the repo. Outputs written to /out inside the container land in ./artifacts on the host.
# Useful where Windows Smart App Control blocks unsigned build tools or dev builds.
#   scripts/dotnet-in-docker.sh test
#   scripts/dotnet-in-docker.sh publish src/Fermata.Desktop -c Release -r win-x64 -o /out/desktop-win-x64
set -eu
repo="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$repo/artifacts"
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$repo:/repo:ro" \
  -v "$repo/artifacts:/out" \
  -v fermata-nuget:/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e FERMATA_SCREENSHOTS=/out/screenshots \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c 'mkdir /src && cd /repo && tar --exclude=bin --exclude=obj --exclude=.git --exclude=artifacts -cf - . | tar -xf - -C /src && cd /src && dotnet "$@"' -- "$@"
