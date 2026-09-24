#!/usr/bin/env bash
# Builds the self-contained Linux packages for one architecture: the .deb and the AppImage.
#
#   usage: all.sh [rid] [output-dir]      default: this machine's architecture, dist/
#
# Both are packed from one staged tree, so a layout mistake shows up in both at once rather than
# in whichever happened to be tested. The RPM is built by rpm.sh instead, on Fedora, from the spec.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/common.sh"

RID="${1:-$(host_rid)}"
OUT="$(ensure_dir "${2:-$BUILD}")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Resolve the version once and hand it to every format, so they cannot disagree and so the restore
# MinVer needs happens a single time.
SNAPSHOTKIT_VERSION="$(app_version)"
export SNAPSHOTKIT_VERSION
echo "Building $APP_NAME $SNAPSHOTKIT_VERSION for $RID"

echo "==> staging"
"$HERE/stage.sh" "$RID" "$STAGE"

echo "==> deb"
"$HERE/deb.sh" "$RID" "$STAGE" "$OUT"

echo "==> AppImage"
"$HERE/appimage.sh" "$RID" "$STAGE" "$OUT"

echo
echo "Built for $RID:"
ls -1sh "$OUT"
