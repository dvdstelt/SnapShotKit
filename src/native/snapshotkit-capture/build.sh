#!/usr/bin/env bash
# Builds the snapshotkit-capture helper. Requires pipewire-devel.
set -euo pipefail

cd "$(dirname "$0")"

if ! pkg-config --exists libpipewire-0.3; then
    echo "pipewire-devel is not installed. Install it with:" >&2
    echo "    sudo dnf install pipewire-devel" >&2
    exit 1
fi

gcc -O2 -Wall -Wextra \
    $(pkg-config --cflags libpipewire-0.3) \
    -o snapshotkit-capture snapshotkit-capture.c \
    $(pkg-config --libs libpipewire-0.3)

echo "Built $(pwd)/snapshotkit-capture"
