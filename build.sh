#!/usr/bin/env bash
set -e
case "$OSTYPE" in
  msys*|cygwin*)
    echo "On Windows, use build.cmd from PowerShell or cmd.exe; build.sh requires a Unix environment." >&2
    exit 1
    ;;
esac
scriptroot="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$scriptroot/eng/common/build.sh" --restore --build "$@"
