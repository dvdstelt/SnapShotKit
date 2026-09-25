#!/usr/bin/env bash
# Builds the Fedora RPM from packaging/snapshotkit.spec, the same spec COPR builds.
#
#   usage: rpm.sh [output-dir]      default: dist/
#
# Unlike the .deb and the AppImage this is not packed from the staged tree. It is built on Fedora
# against Fedora's own .NET SDK and runs on Fedora's .NET runtime, so that what the release offers
# for download is what `dnf install` from COPR gives, and so that building it proves the spec
# still works where COPR will build it. Run it on Fedora, with the spec's build requirements
# installed (`dnf builddep packaging/snapshotkit.spec`).
#
# The source tarball is made from HEAD, so uncommitted changes are not in it.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"

OUT="$(ensure_dir "${1:-$BUILD}")"
VERSION="$(app_version)"
RPM_VERSION="$(rpm_version "$VERSION")"
RPM_RELEASE="$(rpm_release "$VERSION")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK"/{SOURCES,SPECS}

# The version is written into a copy, never into the spec in the tree, whose own Version is what
# COPR builds from.
sed -e "s/^Version:.*/Version:        $RPM_VERSION/" \
    -e "s/^Release:.*/Release:        $RPM_RELEASE%{?dist}/" \
    "$ROOT/packaging/snapshotkit.spec" > "$WORK/SPECS/snapshotkit.spec"

# Prefixed with the repository name, matching what GitHub's archive endpoint and COPR both
# produce, so the one spec unpacks all three.
git -C "$ROOT" archive --format=tar.gz --prefix="$APP_NAME-$RPM_VERSION/" \
    -o "$WORK/SOURCES/$APP_NAME-$RPM_VERSION.tar.gz" HEAD

# semver is what MinVer is told, since the tarball has no history for it to read. It differs from
# the RPM version for a pre-release: 0.2.0-alpha.0.3 against 0.2.0 release 0.alpha.0.3.
rpmbuild --define "_topdir $WORK" --define "semver $VERSION" -bb "$WORK/SPECS/snapshotkit.spec"

find "$WORK/RPMS" -name '*.rpm' -exec cp {} "$OUT"/ \;
ls "$OUT"/$APP-*.rpm
