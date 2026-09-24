#!/usr/bin/env bash
# Lays out the filesystem tree that the .deb and the AppImage both pack.
#
#   usage: stage.sh <rid> <staging-dir>
#
# The layout itself is the Makefile's install target, which is also what the RPM installs through.
# Staging through it rather than beside it means there is one layout, and a mistake in it shows up
# in every format at once rather than in whichever happened to be tested.
#
# Self-contained, because Debian ships no .NET runtime to depend on. The RPM is the one format that
# is not, and it is built from the spec rather than from here.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: stage.sh <rid> <staging-dir>}"
STAGE="$(ensure_dir "${2:?usage: stage.sh <rid> <staging-dir>}")"
VERSION="$(app_version)"

rm -rf "${STAGE:?}"/*

make -C "$ROOT" build RUNTIME="$RID" SELF_CONTAINED=true VERSION="$VERSION"
make -C "$ROOT" install DESTDIR="$STAGE" PREFIX=/usr

install -Dm644 "$ROOT/LICENSE"   "$STAGE/usr/share/doc/$APP/copyright"
install -Dm644 "$ROOT/README.md" "$STAGE/usr/share/doc/$APP/README.md"

echo "staged $APP $VERSION for $RID into $STAGE"
