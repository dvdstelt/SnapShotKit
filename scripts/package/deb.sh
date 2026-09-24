#!/usr/bin/env bash
# Builds a .deb from a staged tree.
#
#   usage: deb.sh <rid> <staging-dir> <output-dir>
#
# A .deb is an ar archive of debian-binary, control.tar and data.tar, so it is built here with ar
# and tar rather than dpkg-deb. That keeps the build working on any distribution, including the
# Fedora this is developed on, instead of only where dpkg happens to be installed.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

RID="${1:?usage: deb.sh <rid> <staging-dir> <output-dir>}"
STAGE="$(abspath "${2:?}")"
OUT="$(ensure_dir "${3:?}")"
VERSION="$(app_version)"
DEB_VERSION="$(deb_version "$VERSION")"
ARCH="$(deb_arch "$RID")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/control"

INSTALLED_KB=$(du -sk "$STAGE" | cut -f1)

# libpipewire is the one library that must come from the system, since it has to speak the same
# protocol as the PipeWire daemon that is running. Debian 13 and Ubuntu 24.04 onwards renamed it
# for the 64-bit time_t transition; the old name is the fallback for anything that did not.
#
# Avalonia draws through XWayland and opens libX11, libICE and libSM at run time, which is why they
# are listed although nothing links them. Skia links fontconfig.
#
# The shell extension declares GNOME 48 and later, so gnome-shell is recommended rather than
# required: without it capture still works from the command line and the launcher.
cat > "$WORK/control/control" <<CONTROL
Package: $APP
Version: $DEB_VERSION
Architecture: $ARCH
Maintainer: $MAINTAINER
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libpipewire-0.3-0t64 | libpipewire-0.3-0, xdg-desktop-portal, wl-clipboard
Recommends: xdg-desktop-portal-gnome, gnome-shell (>= 48), cups-client
Section: graphics
Priority: optional
Homepage: $HOMEPAGE
Description: $SUMMARY
 SnapShotKit captures the whole screen the moment you press the key, then
 lets you choose a region from the frozen image, so nothing moves under the
 cursor while you aim.
 .
 Snapshots are kept as objects rather than pixels: an arrow, a blur or a
 numbered marker can be moved or deleted next week, and the capture
 underneath is never modified.
 .
 Built for GNOME on Wayland. Run "snapshotkit setup" once to bind Print and
 start the capture daemon.
CONTROL

# Refreshing these caches is what makes the launcher, its icon and the .ssk file type appear.
# Each is guarded: a desktop without one of these tools must not fail the install.
#
# The daemon is deliberately not enabled here. It is per-user and per-session, and a system package
# has no business deciding that every account on the machine wants one; `snapshotkit setup` does it
# for the user who runs it.
cat > "$WORK/control/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
    command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
    command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
exit 0
POSTINST

cat > "$WORK/control/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    command -v update-desktop-database >/dev/null && update-desktop-database -q /usr/share/applications || true
    command -v update-mime-database    >/dev/null && update-mime-database /usr/share/mime || true
    command -v gtk-update-icon-cache   >/dev/null && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
exit 0
POSTRM

chmod 755 "$WORK/control/postinst" "$WORK/control/postrm"

# md5sums lets dpkg detect locally modified files; symlinks and directories are excluded.
( cd "$STAGE" && find . -type f -printf '%P\0' | xargs -0 md5sum > "$WORK/control/md5sums" )

TAR_OPTS=(--owner=0 --group=0 --numeric-owner --sort=name --mtime=@0)
tar -C "$WORK/control" "${TAR_OPTS[@]}" -czf "$WORK/control.tar.gz" .
tar -C "$STAGE"        "${TAR_OPTS[@]}" -cJf "$WORK/data.tar.xz" .
echo "2.0" > "$WORK/debian-binary"

FILE="$OUT/${APP}_${DEB_VERSION}_${ARCH}.deb"
rm -f "$FILE"
# The member order is fixed by the format: debian-binary first, then control, then data.
( cd "$WORK" && ar rc "$FILE" debian-binary control.tar.gz data.tar.xz )
echo "built $FILE"
