# Packaging

## What a package has to contain

Five binaries, and they must stay together. The daemon finds the overlay, the editor and the capture helper by looking beside itself, which is what lets an installed copy work without a single configured path. `make install` therefore puts all of them in `/usr/lib/snapshotkit/` and symlinks only the two commands a person types into `/usr/bin`.

Beyond the binaries: a systemd user unit, a D-Bus activation file, two desktop entries, the `.ssk` MIME type, icons at nine sizes, and the GNOME Shell extension.

## Three formats, two builds

Every release carries an RPM, a `.deb` and an AppImage, for x86-64 and for arm64.

| Format | Built from | .NET | For |
|---|---|---|---|
| RPM | `packaging/snapshotkit.spec`, in a Fedora container | Fedora's own runtime | Fedora 44, and the same package COPR builds |
| `.deb` | the staged tree, on Ubuntu 24.04 | bundled | Debian 13, Ubuntu 25.04 onwards |
| AppImage | the same staged tree | bundled | any other distribution with GNOME 48 |

The RPM is deliberately not packed from the staged tree. What someone downloads from a release should be what `dnf install` from COPR gives them, and building it on Fedora from the spec is also what proves the spec still builds where COPR will build it.

The other two carry the runtime because Debian ships no .NET at all. That makes them about 125 MB installed, most of it the runtime, and it is the only real cost.

**GNOME 48 is the floor for every format**, because it is the oldest shell the extension's `metadata.json` declares. Ubuntu 24.04 LTS ships GNOME 46, so a package for it would install an extension that never loads. The same floor is what makes Ubuntu 24.04 the right machine to build the self-contained packages on: a self-contained build links against the build machine's glibc, and every distribution with GNOME 48 has a newer one.

**libpipewire is never bundled.** The capture helper has to speak the protocol of the PipeWire daemon that is running, so every format links the system's copy. The `.deb` depends on it under the name Debian 13 and Ubuntu gave it for the 64-bit `time_t` transition, `libpipewire-0.3-0t64`, with the old name as a fallback.

## One layout

The Makefile's `install` target is the only description of where files go. The RPM installs through it from the spec, and `scripts/package/stage.sh` stages the `.deb` and the AppImage through it with `DESTDIR`, so a layout mistake shows up in all three at once. `SELF_CONTAINED=true` is the only difference between the two builds.

## The AppImage is not one program

A package installs a systemd unit, a D-Bus activation file, launcher entries and the extension into `/usr`. An AppImage can install nothing, and it is mounted at a new path every time it starts and unmounted when it exits, so nothing may ever be pointed at a path inside it.

Two things follow. `AppRun` is a dispatcher: its first argument picks the daemon, the editor or the client, and with none it opens the editor. And `snapshotkit setup`, run from an AppImage (it knows from `$APPIMAGE`), writes a user unit whose `ExecStart` is the AppImage with `daemon`, binds Print to the AppImage with `capture`, copies the extension out of the mount, and installs the launcher entries, icons and `.ssk` type into `~/.local/share` with their `Exec` lines rewritten the same way. Each of those three is read by a different parser with different quoting rules, which is why `AppImage.cs` has three quoting functions.

Setup records where the AppImage was when it ran, so moving the file afterwards breaks Print and the launcher until setup is run again. The shell extension is unaffected, since it only talks D-Bus to the daemon.

The package's own unit and D-Bus activation file are left out of the AppImage, since both point at `/usr/lib/snapshotkit`.

## The D-Bus activation file is not optional

`/usr/share/dbus-1/services/org.snapshotkit.Daemon.service` is what makes the first capture after a login work. Without it, `snapshotkit capture` and the launcher entry both fail until something has started the daemon, and they fail quietly, which is the worst way to fail.

This is also why the daemon registers its method handler *before* claiming its bus name. The name appearing is what tells the world it is ready, and with activation the call that started it arrives the instant it appears; claiming the name first loses exactly that call.

## The extension ships with the application

It is installed to `/usr/share/gnome-shell/extensions/`, not downloaded from extensions.gnome.org. The extension and the daemon speak a private D-Bus interface and have to move together: a version skew between them shows up as a menu item that does nothing.

This is the strongest argument for an RPM over a Flatpak. A Flatpak cannot install a shell extension, so a Flathub release means two separate installs that can drift apart.

## Build requirements

```
dotnet-sdk-10.0 gcc make pipewire-devel glib2-devel
```

`global.json` asks for SDK 10.0.100 with `latestFeature`, which Fedora's 10.0.111 satisfies. It used to ask for 10.0.200, which it does not: a higher feature band than the distro ships means the build cannot find an SDK at all.

## Macros need declaring

A build root is minimal, and a macro it does not know is not an error until the very end: `%{_userunitdir}` expanded to itself, and the build failed assembling the file list after everything had already compiled. `systemd-rpm-macros` is a build requirement for that reason. `%{_metainfodir}` needs nothing, because `redhat-rpm-config` is in every build root already.

The cheap check is to expand the spec and confirm every packaged path starts with a slash:

```bash
rpmspec -P packaging/snapshotkit.spec | sed -n '/^%files/,/^%post/p'
```

## The tarball is named after the repository

Both GitHub's archive endpoint and COPR name the directory inside the tarball after the repository, `SnapShotKit-0.1.0`, not after the package. `%prep` unpacks that, and the release workflow prefixes its own tarball to match, so one spec handles all three. Building only from a tarball the workflow generated hides this: it was the workflow's own prefix that agreed with the spec, and nothing else did.

## Publishing flags that matter

Both are in the Makefile, and both were mistakes worth recording.

**A runtime identifier is required.** Published without `-r linux-x64`, Avalonia keeps native libraries for every platform it supports: the editor came to 564 MB, almost all of it Windows and macOS binaries. With it, 25 MB.

**Symbols have to be turned off.** The ahead-of-time compiled overlay ships a 51 MB `.dbg` file otherwise, which is larger than everything else in the package put together.

## RPM

`packaging/snapshotkit.spec` builds through the Makefile. To build it the way a release does, from a tarball of `HEAD` with the version from the tags:

```bash
sudo dnf builddep packaging/snapshotkit.spec
scripts/package/rpm.sh
```

The script writes the version into a copy of the spec. The spec in the tree keeps its own `Version:`, which is what COPR builds, and the source tarball has no git history for MinVer to read, so the spec passes the version to the Makefile explicitly: `semver` when the release workflow defines it, since a pre-release is spelled differently as an RPM version, and `%{version}` otherwise.

For COPR, the project needs **network access enabled in the build settings**. NuGet restore needs the network and Fedora's mock disables it by default. This is the usual arrangement for .NET packages; the alternative is vendoring every dependency into the source tarball.

The package deliberately does not enable the daemon in `%post`. A capture daemon is a per-user, per-session decision, and a system package has no business making it for every account on the machine. `snapshotkit setup` does it for the user who runs it.

## The .deb and the AppImage

```bash
scripts/package/all.sh [linux-x64|linux-arm64] [dist]
```

This builds on Fedora as well as on Ubuntu: the `.deb` is assembled with `ar` and `tar` rather than `dpkg-deb`, and appimagetool is downloaded if it is not on the path. It needs `pipewire-devel`, `clang` for the ahead-of-time client, and `glib-compile-schemas`.

## Releasing

`RELEASING.md` is the procedure. In short: draft a release on GitHub, dispatch the Release workflow with the draft's tag to attach every package, check them, then publish.

`.github/workflows/packages.yml` is the one place packages are built, and both `ci.yml` and `release.yml` call it. CI builds x86-64 on every push and pull request, installs the RPM on Fedora and the `.deb` on Debian 13, and renders an export from the `.deb` and from the AppImage; a release does the same for both architectures and then attaches the results with a `sha256sums.txt`. A release therefore never attaches something that has not been through CI's steps.

The `.deb` is installed on Debian rather than on the Ubuntu that built it, so a dependency spelled only the way Ubuntu spells it fails there.

Building the RPM in a Fedora container rather than on the Ubuntu runner is deliberate: the package is built against Fedora's .NET SDK and pipewire, and building it against anything else would prove something other than what is shipped. It is also what caught the `global.json` feature band problem, which only appears when the SDK is the distro's rather than a hand-installed one.

arm64 is built on arm64 runners rather than cross-compiled. The capture helper is C against libpipewire and the client and overlay are compiled ahead of time, and all three want a native toolchain.

## COPR

COPR gives users `dnf install` and automatic updates, which a GitHub release does not. It needs no code: create a project at [copr.fedorainfracloud.org](https://copr.fedorainfracloud.org), set the source to **SCM** pointing at this repository with `packaging/snapshotkit.spec`, and turn on **internet access during builds** — NuGet restore needs the network and mock disables it by default. COPR provides a webhook URL to paste into the repository settings so a push rebuilds.

The spec allows `aarch64` as well as `x86_64`, but COPR only builds the chroots the project has. arm64 `dnf` users need a `fedora-44-aarch64` chroot added to the project, alongside the existing one:

```bash
copr-cli modify snapshotkit --chroot fedora-44-x86_64 --chroot fedora-44-aarch64
```

## What is not packaged yet

- **Flatpak.** Wants the capture shortcut moved to the GlobalShortcuts portal first, since a sandbox cannot write gsettings keybindings or install a shell extension. GNOME 50 does implement that portal, so it is a real option rather than a dead end.
- **Ubuntu 24.04 LTS and anything else before GNOME 48.** The extension would not load. Supporting it means testing the extension against GNOME 46 and declaring it, not changing the packaging.
- **arm64 on real hardware.** A release builds and installs the arm64 packages and renders an export from them, but nobody has pressed Print on an arm64 machine yet.
