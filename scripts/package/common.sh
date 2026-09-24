# Shared settings and helpers for the packaging scripts. Sourced, not run.

APP=snapshotkit
APP_NAME=SnapShotKit
APP_ID=io.github.dvdstelt.SnapShotKit
MAINTAINER="Dennis van der Stelt <dvdstelt@gmail.com>"
HOMEPAGE="https://github.com/dvdstelt/SnapShotKit"
SUMMARY="Capture a region of the screen and annotate it"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD="$ROOT/dist"

# Every format spells the same architecture differently, which is a classic source of a download
# that installs nowhere. The mapping lives here once.
#   .NET RID        linux-x64     linux-arm64
#   dpkg            amd64         arm64
#   rpm / AppImage  x86_64        aarch64
deb_arch() { case "$1" in linux-x64) echo amd64 ;; linux-arm64) echo arm64 ;; *) echo "unknown RID: $1" >&2; return 1 ;; esac; }
rpm_arch() { case "$1" in linux-x64) echo x86_64 ;; linux-arm64) echo aarch64 ;; *) echo "unknown RID: $1" >&2; return 1 ;; esac; }

# The RID of the machine this runs on, for when none is given.
host_rid() { case "$(uname -m)" in x86_64) echo linux-x64 ;; aarch64) echo linux-arm64 ;; *) echo "unsupported machine: $(uname -m)" >&2; return 1 ;; esac; }

# MinVer derives this from the nearest git tag. Its target ships inside MinVer's NuGet package, so
# it does not exist until a project has been restored: a developer's checkout always is, a CI
# checkout never is, and that difference is handled here rather than assumed away.
#
# SNAPSHOTKIT_VERSION lets all.sh resolve the version once and pass it to every format, and lets
# the release workflow supply the version a draft release is about to get.
app_version() {
  if [ -n "${SNAPSHOTKIT_VERSION:-}" ]; then echo "$SNAPSHOTKIT_VERSION"; return 0; fi

  local project="$ROOT/src/SnapShotKit.Contracts/SnapShotKit.Contracts.csproj"
  dotnet restore "$project" >/dev/null || {
    echo "common.sh: dotnet restore failed, so the version cannot be determined." >&2
    return 1
  }

  local version
  # No 2>/dev/null here: hiding MSBuild's stderr turns a one-line error into a silent failure.
  version="$(dotnet msbuild "$project" -t:MinVer -getProperty:MinVerVersion -v:q -nologo | tr -d '[:space:]')" || {
    echo "common.sh: MinVer could not be asked for the version." >&2
    return 1
  }
  [ -n "$version" ] || { echo "common.sh: MinVer returned an empty version." >&2; return 1; }
  echo "$version"
}

# Debian and RPM both reject a '-' in a version, which every pre-release from MinVer contains.
# 0.2.1-alpha.0.7 becomes 0.2.1~alpha.0.7 for dpkg, which sorts it *before* 0.2.1 as intended,
# and 0.2.1 with release 0.alpha.0.7 for rpm, which is that ecosystem's equivalent.
deb_version() { echo "${1/-/\~}"; }
rpm_version() { echo "${1%%-*}"; }
rpm_release() { case "$1" in *-*) echo "0.${1#*-}" ;; *) echo 1 ;; esac; }

# Paths given on the command line may be relative, and several of these scripts cd elsewhere before
# using them. Resolving once up front is the difference between writing the package where it was
# asked for and writing it into whatever directory happened to be current.
abspath() {
  cd "$1" 2>/dev/null && pwd || { echo "common.sh: no such directory: $1" >&2; return 1; }
}

# For an output directory, which may not exist yet.
ensure_dir() { mkdir -p "$1" && abspath "$1"; }
