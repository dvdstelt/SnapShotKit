#!/usr/bin/env bash
# Builds an AppImage from a staged tree: the download for a GNOME desktop that is neither Fedora nor
# Debian shaped.
#
#   usage: appimage.sh <rid> <staging-dir> <output-dir>
#
# appimagetool is fetched if it is not already on PATH; set APPIMAGETOOL to point at a local copy.
#
# SnapShotKit is not one program, so the AppImage is not either. A systemd unit starts the daemon,
# the daemon starts the overlay, the editor and the capture helper beside itself, and GNOME runs
# the client when Print is pressed. AppRun is therefore a dispatcher: the first argument names
# which of them to start, and `setup` writes the unit, the keybinding and the launcher entries to
# start the AppImage file with the right verb, since the mount this runs from is gone next time.
#
# libpipewire is deliberately not bundled. The capture helper has to speak the protocol of the
# PipeWire that is running, so it links the system's copy, which every GNOME desktop has.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: appimage.sh <rid> <staging-dir> <output-dir>}"
STAGE="$(abspath "${2:?}")"
OUT="$(ensure_dir "${3:?}")"
VERSION="$(app_version)"
ARCH="$(rpm_arch "$RID")" # AppImage spells architectures the way rpm does.

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/$APP_NAME.AppDir"
mkdir -p "$APPDIR"

cp -a "$STAGE"/. "$APPDIR"/

# The package's own daemon unit and D-Bus activation file point at /usr/lib/snapshotkit, which does
# not exist on a machine that only has the AppImage. setup writes a per-user unit instead.
rm -f "$APPDIR/usr/lib/systemd/user/snapshotkitd.service" \
      "$APPDIR/usr/share/dbus-1/services/org.snapshotkit.Daemon.service"

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# Dispatches to one of SnapShotKit's programs. See scripts/package/appimage.sh.
HERE="$(dirname "$(readlink -f "$0")")"
LIB="$HERE/usr/lib/snapshotkit"

case "${1:-}" in
    daemon)
        shift
        exec "$LIB/snapshotkitd" "$@" ;;
    editor)
        shift
        exec "$LIB/snapshotkit-editor" "$@" ;;
    capture|status|snapshots|setup|help|--help|-h)
        exec "$LIB/snapshotkit" "$@" ;;
    *)
        # Started from a file manager or with a picture: open the editor, which is what somebody
        # double-clicking a downloaded AppImage expects to see.
        exec "$LIB/snapshotkit-editor" "$@" ;;
esac
APPRUN
chmod 755 "$APPDIR/AppRun"

# appimagetool looks for exactly one desktop entry and its icon at the top level of the AppDir, and
# reads .DirIcon for the icon a file manager shows. The editor is the one to represent it.
cp "$STAGE/usr/share/applications/$APP-editor.desktop" "$APPDIR/$APP-editor.desktop"
cp "$ROOT/packaging/icons/256.png" "$APPDIR/$APP.png"
ln -sf "$APP.png" "$APPDIR/.DirIcon"

TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [ -z "$TOOL" ]; then
  echo "Fetching appimagetool for $ARCH" >&2
  TOOL="$WORK/appimagetool"
  curl -fsSL -o "$TOOL" \
    "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
  chmod +x "$TOOL"
fi

FILE="$OUT/$APP_NAME-$VERSION-$ARCH.AppImage"
rm -f "$FILE"
# --appimage-extract-and-run keeps appimagetool from needing FUSE, which CI runners lack.
ARCH="$ARCH" "$TOOL" --appimage-extract-and-run "$APPDIR" "$FILE"
echo "built $FILE"
