#!/usr/bin/env sh
# Runs the test suite in a Linux container. See dotnet-in-docker.sh.
exec "$(dirname "$0")/dotnet-in-docker.sh" test "$@"
